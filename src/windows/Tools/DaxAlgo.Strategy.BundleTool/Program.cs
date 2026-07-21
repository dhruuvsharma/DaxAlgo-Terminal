using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using DaxAlgo.Strategy.Bundle;

return BundleTool.Run(args);

internal static class BundleTool
{
    private const int Success = 0;
    private const int UnexpectedFailure = 1;
    private const int UsageFailure = 2;
    private const int ValidationFailure = 3;
    private const int SignatureFailure = 4;
    private const int IoFailure = 5;
    private const int MaxPemCharacters = 1024 * 1024;

    private static readonly StringComparer FileSystemPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> PackOptions = new(StringComparer.Ordinal)
    {
        "id", "name", "version", "publisher", "sdk", "engine", "entry-type", "output",
        "min-host", "max-host", "ui", "dependency", "resource", "sbom", "provenance", "capability",
    };

    private static readonly HashSet<string> SignOptions = new(StringComparer.Ordinal)
    {
        "bundle", "key", "key-id", "output",
    };

    private static readonly HashSet<string> VerifyOptions = new(StringComparer.Ordinal)
    {
        "bundle", "public-key", "publisher", "key-id",
    };

    private static readonly HashSet<string> InspectOptions = new(StringComparer.Ordinal)
    {
        "bundle",
    };

    public static int Run(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0 || IsHelp(arguments[0]))
            {
                PrintUsage();
                return Success;
            }

            var command = arguments[0].ToLowerInvariant();
            // Treat help only as the command's sole argument so option values such as
            // "--name help" remain ordinary data.
            if (arguments.Length == 2 && IsHelp(arguments[1]))
            {
                PrintCommandUsage(command);
                return Success;
            }

            var options = ParsedOptions.Parse(arguments.Skip(1));
            return command switch
            {
                "pack" => Pack(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                "inspect" => Inspect(options),
                _ => throw new CliUsageException("Unknown command."),
            };
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"usage error: {TerminalSafe(ex.Message)}");
            Console.Error.WriteLine("Run 'daxalgo-bundle --help' for usage.");
            return UsageFailure;
        }
        catch (StrategyBundleValidationException ex)
        {
            Console.Error.WriteLine($"validation failed ({ex.Error}): {TerminalSafe(ex.Message)}");
            return ValidationFailure;
        }
        catch (BundleSignatureException ex)
        {
            Console.Error.WriteLine($"signature failed: {TerminalSafe(ex.Message)}");
            return SignatureFailure;
        }
        catch (CryptographicException ex)
        {
            Console.Error.WriteLine($"signature failed: {TerminalSafe(ex.Message)}");
            return SignatureFailure;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"I/O failed: {TerminalSafe(ex.Message)}");
            return IoFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected failure: {TerminalSafe(ex.Message)}");
            return UnexpectedFailure;
        }
    }

    private static int Pack(ParsedOptions options)
    {
        options.RequireOnly(PackOptions);

        var identity = new StrategyBundleIdentity(
            options.RequiredSingle("id"),
            options.RequiredSingle("name"),
            options.RequiredSingle("version"),
            options.RequiredSingle("publisher"));
        var compatibility = new StrategyBundleCompatibility(
            options.RequiredSingle("sdk"),
            options.OptionalSingle("min-host"),
            options.OptionalSingle("max-host"));

        var payloads = new List<StrategyBundlePayloadSource>();
        var payloadSourcePaths = new List<string>();
        var engineBundlePath = AddPayload(
            payloads,
            payloadSourcePaths,
            options.RequiredSingle("engine"),
            StrategyBundlePayloadRole.Engine,
            "engine");
        AddPayloads(payloads, payloadSourcePaths, options.Many("ui"), StrategyBundlePayloadRole.WindowsUi, "windows");
        AddPayloads(
            payloads,
            payloadSourcePaths,
            options.Many("dependency"),
            StrategyBundlePayloadRole.ManagedDependency,
            "deps");
        AddPayloads(
            payloads,
            payloadSourcePaths,
            options.Many("resource"),
            StrategyBundlePayloadRole.Resource,
            "resources");
        AddPayloads(payloads, payloadSourcePaths, options.Many("sbom"), StrategyBundlePayloadRole.Sbom, "sbom");
        AddPayloads(
            payloads,
            payloadSourcePaths,
            options.Many("provenance"),
            StrategyBundlePayloadRole.Provenance,
            "provenance");

        var request = new StrategyBundlePackRequest(
            identity,
            compatibility,
            new StrategyBundleEngineEntryPoint(
                engineBundlePath,
                options.RequiredSingle("entry-type"),
                StrategyBundleEngineEntryPoint.CurrentContract,
                StrategyBundleEngineEntryPoint.CurrentActivation),
            options.Many("capability"),
            payloads);
        var outputPath = RequireBundlePath(options.RequiredSingle("output"), "output");
        RejectOutputAlias(outputPath, payloadSourcePaths, "payload");
        RequireAvailableOutput(outputPath, allowExistingExactPath: null);
        var result = WriteAtomically(
            outputPath,
            replaceExisting: false,
            output => DaxStrategyBundle.Pack(output, request));

        PrintBundle(
            result.Manifest,
            result.ContentRootSha256,
            result.BundleLength,
            expandedLength: null,
            new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.Missing,
                null,
                null,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                "The packed bundle is unsigned."));
        Console.WriteLine($"output: {TerminalSafe(Path.GetFullPath(outputPath))}");
        return Success;
    }

    private static int Sign(ParsedOptions options)
    {
        options.RequireOnly(SignOptions);
        var inputPath = RequireExistingBundle(options.RequiredSingle("bundle"));
        var keySource = options.RequiredSingle("key");
        var keyId = options.RequiredSingle("key-id");
        var outputPath = RequireBundlePath(options.OptionalSingle("output") ?? inputPath, "output");
        var isExactInPlace = FileSystemPathComparer.Equals(outputPath, inputPath);
        if (LooksLikeInlinePem(keySource))
            throw new BundleSignatureException(
                "--key accepts a PEM file path or '-', not inline key material.");
        if (keySource != "-")
        {
            var keyPath = RequireExistingPemFile(keySource, "private key");
            RejectOutputAlias(outputPath, [keyPath], "private-key");
        }
        RequireAvailableOutput(outputPath, isExactInPlace ? inputPath : null);

        using var privateKey = LoadPrivateKey(keySource);
        StrategyBundleSignResult result;
        try
        {
            result = WriteAtomically(
                outputPath,
                replaceExisting: isExactInPlace,
                output =>
                {
                    using var input = File.OpenRead(inputPath);
                    return DaxStrategyBundle.Sign(input, output, privateKey, keyId);
                });
        }
        catch (ArgumentException ex)
        {
            throw new BundleSignatureException(ex.Message, ex);
        }

        PrintBundle(
            result.Manifest,
            result.ContentRootSha256,
            result.BundleLength,
            expandedLength: null,
            new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.PresentUnverified,
                result.KeyId,
                null,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                "Signature written; use verify with the publisher public key to authenticate it."));
        Console.WriteLine($"output: {TerminalSafe(Path.GetFullPath(outputPath))}");
        return Success;
    }

    private static int Verify(ParsedOptions options)
    {
        options.RequireOnly(VerifyOptions);
        var bundlePath = RequireExistingBundle(options.RequiredSingle("bundle"));
        var keySource = options.RequiredSingle("public-key");
        var publisherId = options.RequiredSingle("publisher");
        var keyId = options.RequiredSingle("key-id");

        using var publicKey = LoadPublicKey(keySource);
        StrategyBundleVerification verification;
        try
        {
            verification = DaxStrategyBundle.Verify(
                bundlePath,
                [StrategyBundlePublisherKey.FromEcdsa(publisherId, keyId, publicKey)]);
        }
        catch (ArgumentException ex)
        {
            throw new BundleSignatureException(ex.Message, ex);
        }

        var inspection = verification.Inspection;
        PrintBundle(
            inspection.Manifest,
            inspection.ContentRootSha256,
            inspection.CompressedBundleLength,
            inspection.TotalExpandedLength,
            verification.PublisherSignature);

        if (!verification.IsPublisherVerified)
            throw new BundleSignatureException(
                verification.PublisherSignature.Detail ??
                $"Publisher signature status is {verification.PublisherSignature.Status}.");

        return Success;
    }

    private static int Inspect(ParsedOptions options)
    {
        options.RequireOnly(InspectOptions);
        var bundlePath = RequireExistingBundle(options.RequiredSingle("bundle"));
        var inspection = DaxStrategyBundle.Inspect(bundlePath);
        PrintBundle(
            inspection.Manifest,
            inspection.ContentRootSha256,
            inspection.CompressedBundleLength,
            inspection.TotalExpandedLength,
            inspection.PublisherSignature);
        return Success;
    }

    private static void AddPayloads(
        ICollection<StrategyBundlePayloadSource> payloads,
        ICollection<string> payloadSourcePaths,
        IReadOnlyList<string> sourcePaths,
        StrategyBundlePayloadRole role,
        string roleDirectory)
    {
        foreach (var sourcePath in sourcePaths)
            AddPayload(payloads, payloadSourcePaths, sourcePath, role, roleDirectory);
    }

    private static string AddPayload(
        ICollection<StrategyBundlePayloadSource> payloads,
        ICollection<string> payloadSourcePaths,
        string sourcePath,
        StrategyBundlePayloadRole role,
        string roleDirectory)
    {
        var fullSourcePath = RequireExistingFile(sourcePath, role.ToString());
        var fileName = Path.GetFileName(fullSourcePath).Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new CliUsageException($"Payload '{sourcePath}' has no file name.");

        // Keep caller paths out of the bundle. The core validates aliases, reserved names, traversal,
        // role counts, and the final normalized path set.
        var bundlePath = $"payload/{roleDirectory}/{fileName}";
        payloads.Add(StrategyBundlePayloadSource.FromFile(bundlePath, role, fullSourcePath));
        payloadSourcePaths.Add(fullSourcePath);
        return bundlePath;
    }

    private static ECDsa LoadPrivateKey(string source)
    {
        var pem = ReadPem(source, "private key", allowInline: false);
        if (!pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new BundleSignatureException(
                "--key must identify an EC private-key PEM file or '-' for standard input.");

        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.ExportParameters(includePrivateParameters: true).D is null)
            {
                key.Dispose();
                throw new BundleSignatureException("The PEM does not contain an ECDSA private key.");
            }
            return key;
        }
        catch (BundleSignatureException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new BundleSignatureException("The publisher private-key PEM is invalid or unsupported.", ex);
        }
    }

    private static ECDsa LoadPublicKey(string source)
    {
        var pem = ReadPem(source, "public key", allowInline: true);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new BundleSignatureException("--public-key requires a public-key PEM, not private-key material.");

        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new BundleSignatureException("The publisher public-key PEM is invalid or unsupported.", ex);
        }
    }

    private static string ReadPem(string source, string description, bool allowInline)
    {
        if (LooksLikeInlinePem(source))
        {
            if (!allowInline)
                throw new BundleSignatureException(
                    "--key accepts a PEM file path or '-', not inline key material.");
            if (source.Length > MaxPemCharacters)
                throw new BundleSignatureException($"The {description} PEM exceeds the 1 MiB input limit.");
            return StripPemBom(source);
        }

        TextReader reader;
        IDisposable? owned = null;
        if (source == "-")
        {
            reader = Console.In;
        }
        else
        {
            var path = RequireExistingPemFile(source, description);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            owned = reader;
        }

        try
        {
            var content = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (content.Length + read > MaxPemCharacters)
                    throw new BundleSignatureException($"The {description} PEM exceeds the 1 MiB input limit.");
                content.Append(buffer, 0, read);
            }
            // Some Windows secret providers and process wrappers prefix redirected UTF-8 input
            // with a BOM. It is transport metadata, not part of the PEM armor.
            return StripPemBom(content.ToString());
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static bool LooksLikeInlinePem(string value) =>
        value.Contains("-----BEGIN", StringComparison.Ordinal) ||
        value.IndexOf('\r') >= 0 ||
        value.IndexOf('\n') >= 0;

    private static string StripPemBom(string value) =>
        value.Length > 0 && value[0] == '\uFEFF' ? value[1..] : value;

    private static T WriteAtomically<T>(
        string outputPath,
        bool replaceExisting,
        Func<Stream, T> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new IOException("The output path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            T result;
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.SequentialScan))
            {
                result = write(output);
                output.Flush(flushToDisk: true);
            }

            // Only an exact, normalized sign-in-place operation may replace a destination. Pack and
            // sign-to-another-path use the non-overwriting move as the final TOCTOU-safe guard: an
            // existing junction/symlink/hard-link alias to a payload or key therefore cannot be clobbered.
            File.Move(tempPath, fullOutputPath, overwrite: replaceExisting);
            return result;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best effort only. The requested destination is never replaced until the complete
                // core output has been flushed and closed.
            }
        }
    }

    private static string RequireExistingFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CliUsageException($"The {description} path is empty.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The {description} file was not found: {fullPath}", fullPath);
        return fullPath;
    }

    private static string RequireExistingPemFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CliUsageException($"The {description} path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BundleSignatureException($"The {description} PEM path is invalid.", ex);
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The {description} PEM file was not found.");
        return fullPath;
    }

    private static string RequireBundlePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new CliUsageException($"The {description} path is empty.");

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetExtension(fullPath),
                DaxStrategyBundle.FileExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"The {description} path must use the '{DaxStrategyBundle.FileExtension}' extension.");
        }

        return fullPath;
    }

    private static string RequireExistingBundle(string path)
    {
        var fullPath = RequireBundlePath(path, "bundle");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The bundle file was not found: {fullPath}", fullPath);
        return fullPath;
    }

    private static void RejectOutputAlias(
        string outputPath,
        IEnumerable<string> inputPaths,
        string inputDescription)
    {
        if (inputPaths.Any(path => FileSystemPathComparer.Equals(outputPath, Path.GetFullPath(path))))
        {
            throw new CliUsageException(
                $"The bundle output path must not overwrite a {inputDescription} input.");
        }
    }

    private static void RequireAvailableOutput(string outputPath, string? allowExistingExactPath)
    {
        if (!File.Exists(outputPath)) return;
        if (allowExistingExactPath is not null &&
            FileSystemPathComparer.Equals(outputPath, Path.GetFullPath(allowExistingExactPath)))
        {
            return;
        }

        throw new CliUsageException(
            "The bundle output already exists. Pack and sign-to-another-path never overwrite files; " +
            "omit --output to sign the validated input bundle in place.");
    }

    private static void PrintBundle(
        StrategyBundleManifest manifest,
        string contentRoot,
        long bundleLength,
        long? expandedLength,
        StrategyBundleSignatureEvidence signature)
    {
        Console.WriteLine($"content root: sha256:{TerminalSafe(contentRoot)}");
        Console.WriteLine($"strategy: {TerminalSafe(manifest.Identity.Id)}");
        Console.WriteLine($"name: {TerminalSafe(manifest.Identity.Name)}");
        Console.WriteLine($"version: {TerminalSafe(manifest.Identity.Version)}");
        Console.WriteLine($"publisher: {TerminalSafe(manifest.Identity.PublisherId)}");
        Console.WriteLine($"target SDK: {TerminalSafe(manifest.Compatibility.TargetSdkVersion)}");
        if (manifest.Compatibility.MinimumHostVersion is not null)
            Console.WriteLine($"minimum host: {TerminalSafe(manifest.Compatibility.MinimumHostVersion)}");
        if (manifest.Compatibility.MaximumHostVersion is not null)
            Console.WriteLine($"maximum host: {TerminalSafe(manifest.Compatibility.MaximumHostVersion)}");
        Console.WriteLine($"engine assembly: {TerminalSafe(manifest.Engine.AssemblyPath)}");
        Console.WriteLine($"engine factory: {TerminalSafe(manifest.Engine.TypeName)}");
        Console.WriteLine($"engine contract: {TerminalSafe(manifest.Engine.Contract)}");
        Console.WriteLine($"bundle bytes: {bundleLength}");
        if (expandedLength.HasValue) Console.WriteLine($"expanded bytes: {expandedLength.Value}");
        Console.WriteLine($"payloads ({manifest.Payloads.Count}):");
        foreach (var payload in manifest.Payloads)
            Console.WriteLine(
                $"  {payload.Role,-18} {payload.Length,10}  sha256:{TerminalSafe(payload.Sha256)}  " +
                TerminalSafe(payload.Path));
        Console.WriteLine($"managed assemblies ({manifest.ManagedAssemblies.Count}):");
        foreach (var assembly in manifest.ManagedAssemblies)
            Console.WriteLine(
                $"  {TerminalSafe(assembly.Name)} -> {TerminalSafe(assembly.Path)} " +
                $"[{TerminalSafe(string.Join(", ", assembly.References))}]");
        if (manifest.Capabilities.Count > 0)
            Console.WriteLine($"capabilities: {TerminalSafe(string.Join(", ", manifest.Capabilities))}");
        Console.WriteLine($"publisher signature: {signature.Status}");
        if (!string.IsNullOrWhiteSpace(signature.KeyId))
            Console.WriteLine($"signature key id: {TerminalSafe(signature.KeyId)}");
        Console.WriteLine($"signature algorithm: {TerminalSafe(signature.Algorithm)}");
        if (!string.IsNullOrWhiteSpace(signature.Detail))
            Console.WriteLine($"signature detail: {TerminalSafe(signature.Detail)}");
    }

    internal static string TerminalSafe(string value)
    {
        StringBuilder? escaped = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (!char.IsControl(character) && !char.IsSurrogate(character) &&
                category is not UnicodeCategory.Format and
                    not UnicodeCategory.LineSeparator and
                    not UnicodeCategory.ParagraphSeparator and
                    not UnicodeCategory.Surrogate)
            {
                escaped?.Append(character);
                continue;
            }

            escaped ??= new StringBuilder(value.Length + 8).Append(value, 0, index);
            escaped.Append("\\u").Append(((int)character).ToString("X4"));
        }

        return escaped?.ToString() ?? value;
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void PrintCommandUsage(string command)
    {
        switch (command)
        {
            case "pack":
                Console.WriteLine(PackUsage);
                break;
            case "sign":
                Console.WriteLine(SignUsage);
                break;
            case "verify":
                Console.WriteLine(VerifyUsage);
                break;
            case "inspect":
                Console.WriteLine(InspectUsage);
                break;
            default:
                PrintUsage();
                break;
        }
    }

    private static void PrintUsage() => Console.WriteLine(
        $"""
        daxalgo-bundle <command> [options]

        Commands:
          pack      Create an unsigned {DaxStrategyBundle.FileExtension} bundle.
          sign      Add a publisher signature. Defaults to a safe in-place rewrite.
          verify    Validate content and authenticate a publisher signature.
          inspect   Passively inspect content and signature presence without loading payload code.

        Run 'daxalgo-bundle <command> --help' for command options.

        Exit codes: 0 success, 2 usage, 3 bundle validation, 4 signature/key failure, 5 I/O.
        """);

    private const string PackUsage =
        """
        daxalgo-bundle pack
          --id <strategy-id> --name <display-name> --version <semver> --publisher <publisher-id>
          --sdk <semver> --engine <dll> --entry-type <fully.qualified.Factory> --output <file.daxstrategy>
          [--min-host <semver>] [--max-host <semver>] [--ui <dll>]
          [--dependency <file>]... [--resource <file>]... [--sbom <file>]...
          [--provenance <file>]... [--capability <id>]...

        The entry type must implement DaxAlgo.Sdk.IStrategyEngineFactory. Source directory names are
        not stored; internal paths are derived from each role and file name.
        """;

    private const string SignUsage =
        """
        daxalgo-bundle sign --bundle <file.daxstrategy> --key <private-key.pem|-> --key-id <id>
          [--output <file.daxstrategy>]

        Omit --output to safely replace the input after a complete signed bundle has been flushed.
        Private key material is accepted only from a PEM file or standard input, never as an option value.
        """;

    private const string VerifyUsage =
        """
        daxalgo-bundle verify --bundle <file.daxstrategy> --public-key <public-key.pem|-|PEM>
          --publisher <publisher-id> --key-id <id>
        """;

    private const string InspectUsage =
        """
        daxalgo-bundle inspect --bundle <file.daxstrategy>

        Inspection parses and hashes the passive archive; it never loads a payload assembly.
        """;
}

internal sealed class ParsedOptions
{
    private readonly Dictionary<string, List<string>> _values;

    private ParsedOptions(Dictionary<string, List<string>> values) => _values = values;

    public static ParsedOptions Parse(IEnumerable<string> arguments)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tokens = arguments.ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                throw new CliUsageException("Unexpected positional argument.");

            string name;
            string value;
            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                name = token[2..equals];
                value = token[(equals + 1)..];
                if (value.Length == 0)
                    throw new CliUsageException("An option requires a value.");
            }
            else
            {
                name = token[2..];
                if (index + 1 >= tokens.Length)
                    throw new CliUsageException("An option requires a value.");
                var candidate = tokens[index + 1];
                if (candidate.StartsWith("--", StringComparison.Ordinal) &&
                    !IsInlinePublicKeyValue(name, candidate))
                {
                    throw new CliUsageException("An option requires a value.");
                }
                value = tokens[++index];
            }

            if (name.Length == 0)
                throw new CliUsageException("An option name is empty.");
            if (!values.TryGetValue(name, out var list))
            {
                list = [];
                values.Add(name, list);
            }
            list.Add(value);
        }
        return new ParsedOptions(values);
    }

    private static bool IsInlinePublicKeyValue(string optionName, string value) =>
        optionName == "public-key" &&
        value.Contains("-----BEGIN", StringComparison.Ordinal);

    public void RequireOnly(IReadOnlySet<string> allowed)
    {
        var unknown = _values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null) throw new CliUsageException("Unknown option.");
    }

    public string RequiredSingle(string name) =>
        OptionalSingle(name) ?? throw new CliUsageException($"--{name} is required.");

    public string? OptionalSingle(string name)
    {
        if (!_values.TryGetValue(name, out var values)) return null;
        if (values.Count != 1)
            throw new CliUsageException($"--{name} may be supplied only once.");
        if (string.IsNullOrWhiteSpace(values[0]))
            throw new CliUsageException($"--{name} must not be empty.");
        return values[0];
    }

    public IReadOnlyList<string> Many(string name) =>
        _values.TryGetValue(name, out var values) ? values : [];
}

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class BundleSignatureException : Exception
{
    public BundleSignatureException(string message) : base(message) { }
    public BundleSignatureException(string message, Exception innerException) : base(message, innerException) { }
}
