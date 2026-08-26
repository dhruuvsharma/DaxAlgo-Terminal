using System.IO;
using System.Text;
using DaxAlgo.Package;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What writing an artifact did.</summary>
/// <param name="Success">Whether a file was written.</param>
/// <param name="Path">Absolute path of the artifact, or null.</param>
/// <param name="Message">One line for the status bar.</param>
/// <param name="Manifest">The manifest that went into it, for a caller that wants to show or log it.</param>
public sealed record AuthoredArtifactResult(
    bool Success,
    string? Path,
    string Message,
    DaxPackageManifest? Manifest = null);

/// <summary>
/// Turns a compiled authored unit into a real <c>.daxalgostrategy</c> / <c>.daxalgovisualizer</c> file.
///
/// <para>Until this existed, everything Hyperion produced lived only in the running process: registered
/// into the kernel and visualizer registries, drawn, verified — and gone at the next launch, with no file
/// anywhere the user could back up, send to somebody, or install on a second machine. A strategy you
/// cannot keep is not a delivered strategy.</para>
///
/// <para><b>Writing an artifact deliberately does not install it.</b> Installation runs through
/// <c>PluginInstaller</c> and the trust policy, which is what decides whether unsigned code may load —
/// Permissive allows it, Curated pins publisher thumbprints. A builder that wrote a package and then
/// installed it for you would be a way around that gate, and it would be the most attractive way in,
/// because it is the one path where the bytes are attacker-influenced by design: an authored unit is
/// whatever some model was talked into writing. So this produces a file, and installing files stays the
/// Plugin Manager's job — two acts, two consents.</para>
///
/// <para>Both the compiled assembly and the source go in. The assembly is what makes the package
/// installable at all (<c>InstallFromArtifact</c> refuses a source-only package, which is a correct
/// refusal rather than a limitation). The source is what makes it reviewable a year later, by someone
/// who was not there when the model wrote it.</para>
/// </summary>
public static class AuthoredArtifact
{
    /// <summary>
    /// Where artifacts go by default: <c>%LocalAppData%\DaxAlgo Terminal\authored</c>.
    ///
    /// <para>Under the user's own profile rather than beside the installed application, because these are
    /// the user's documents in every sense that matters — their strategies, kept whether or not the app is
    /// reinstalled, and writable without administrator rights.</para>
    /// </summary>
    public static string DefaultRoot { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "authored");

    /// <summary>
    /// The version an authored artifact carries when the caller names none.
    ///
    /// <para>Fixed, and fixed on purpose. An authored unit has no release process behind it — the user
    /// regenerates it until it is right — so a version that climbed on every build would imply a history
    /// nobody kept, and would make the installer announce an "update" over what is simply the same
    /// strategy again. Installing over a previous build reads as a reinstall, which is what it is.</para>
    /// </summary>
    public const string DefaultVersion = "0.1.0";

    /// <summary>
    /// Writes the artifact and returns where it went. Never throws — a failure here must not cost the
    /// user a unit that compiled, verified and registered perfectly well.
    /// </summary>
    /// <param name="script">The source that produced it, carried as <see cref="DaxPayloadRole.Source"/>.</param>
    /// <param name="compiled">A successful compile. Its assembly image is the installable payload.</param>
    /// <param name="root">Where to write. Defaults to <see cref="DefaultRoot"/>.</param>
    /// <param name="version">Package version. Defaults to <see cref="DefaultVersion"/>.</param>
    /// <param name="uiPayload">Optional UI declaration, for when Hyperion composes the window (#42) and
    /// the layout travels with the unit rather than being recomputed by whoever installs it.</param>
    public static AuthoredArtifactResult Write(
        StrategyScript script,
        StrategyCompileResult compiled,
        string? root = null,
        string? version = null,
        string? uiPayload = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(compiled);

        if (!compiled.Success)
            return new AuthoredArtifactResult(false, null, "It did not compile, so there is nothing to package.");

        if (compiled.Unit is not { } unit)
        {
            return new AuthoredArtifactResult(
                false, null, "Nothing was resolved from the compiled code, so there is nothing to package.");
        }

        if (compiled.Authored?.Image is not { Length: > 0 } image)
        {
            // Source-only would write, and then refuse to install — a worse outcome than saying so here.
            return new AuthoredArtifactResult(
                false, null, "The compiler produced no assembly image, so the package would not be installable.");
        }

        if (string.IsNullOrWhiteSpace(script.Id))
            return new AuthoredArtifactResult(false, null, "Give the unit an id before packaging it.");

        var entryTypeName = unit.Type.FullName;
        if (string.IsNullOrWhiteSpace(entryTypeName))
        {
            return new AuthoredArtifactResult(
                false, null, $"'{unit.Type.Name}' has no full type name, so the host could not resolve it.");
        }

        var kind = unit.Kind == AuthoringKind.Visualizer ? DaxPackageKind.Visualizer : DaxPackageKind.Strategy;
        var stem = Sanitize(script.Id);
        var directory = root ?? DefaultRoot;
        var path = System.IO.Path.Combine(directory, stem + DaxPackage.ExtensionFor(kind));

        try
        {
            Directory.CreateDirectory(directory);

            var result = DaxPackage.Write(path, new DaxPackageRequest
            {
                Kind = kind,
                Id = script.Id,
                Version = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version!,
                DisplayName = string.IsNullOrWhiteSpace(script.DisplayName) ? script.Id : script.DisplayName,
                Publisher = "Authored locally",
                EntryTypeName = entryTypeName!,
                Payloads = Payloads(script, image, stem, uiPayload),
            });

            return new AuthoredArtifactResult(
                true,
                result.Path,
                $"Saved {System.IO.Path.GetFileName(result.Path)} to {directory}.",
                result.Manifest);
        }
        catch (DaxPackageException ex)
        {
            return new AuthoredArtifactResult(false, null, $"The package was rejected: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new AuthoredArtifactResult(false, null, $"Could not write the package: {ex.Message}");
        }
    }

    private static List<DaxPayloadSource> Payloads(
        StrategyScript script, byte[] image, string stem, string? uiPayload)
    {
        // The assembly is named after the id, because the installer takes the plugin folder's name from
        // the assembly payload's file name and the loader expects plugins/<Name>/<Name>.dll.
        var payloads = new List<DaxPayloadSource>
        {
            DaxPayloadSource.FromBytes($"payload/{stem}.dll", DaxPayloadRole.Assembly, image),
        };

        // Sources under their own folder so they cannot collide with the assembly, and named by leaf only:
        // a file called "../evil.cs" would otherwise be a path the writer has to reject, and rejecting the
        // whole package over a filename is a poor trade when the leaf is all that was ever meant.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in script.Files ?? [])
        {
            if (file is null || string.IsNullOrEmpty(file.Content)) continue;

            var name = Leaf(file.Name);
            if (!used.Add(name)) name = $"{used.Count}-{name}";

            payloads.Add(DaxPayloadSource.FromBytes(
                $"payload/src/{name}", DaxPayloadRole.Source, Encoding.UTF8.GetBytes(file.Content)));
        }

        if (!string.IsNullOrWhiteSpace(uiPayload))
        {
            payloads.Add(DaxPayloadSource.FromBytes(
                "payload/ui/window.json", DaxPayloadRole.Ui, Encoding.UTF8.GetBytes(uiPayload!)));
        }

        return payloads;
    }

    /// <summary>The file name only. Anything that looks like a path is reduced to its last segment, so a
    /// crafted source file name cannot steer where a payload lands.</summary>
    private static string Leaf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unit.cs";

        var leaf = name!.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        leaf = Sanitize(leaf, "Unit.cs");
        return string.IsNullOrWhiteSpace(leaf) ? "Unit.cs" : leaf;
    }

    /// <summary>
    /// Keeps letters, digits, dot, dash and underscore, collapsing every run of the rest into a single
    /// dash — so an id a user typed or a model invented is always a legal file name, and always the same
    /// one for the same id.
    ///
    /// <para>Runs are collapsed rather than mapped one-for-one because the naive version turned
    /// <c>my strategy/../v2</c> into <c>my-strategy-..-v2</c>: harmless as a path, since there is no
    /// separator left to traverse with, but a file name containing <c>..</c> is a thing other tools
    /// mishandle and nobody should have to reason about twice.</para>
    ///
    /// <para>An id made entirely of punctuation sanitises to nothing, which would have produced a file
    /// called <c>.daxalgostrategy</c> — no name, and hidden on some systems. That falls back to
    /// <paramref name="fallback"/>.</para>
    /// </summary>
    private static string Sanitize(string value, string fallback = "unit")
    {
        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                pendingSeparator = false;
                builder.Append(character);
            }
            else if (character == '.' && builder.Length > 0 && !pendingSeparator)
            {
                // A dot only survives between two kept characters, which is where it means something —
                // "packaged.kernel", "Unit.cs" — and never in a run.
                builder.Append('.');
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var result = builder.ToString().Trim('-', '.');
        return result.Length == 0 ? fallback : result;
    }
}
