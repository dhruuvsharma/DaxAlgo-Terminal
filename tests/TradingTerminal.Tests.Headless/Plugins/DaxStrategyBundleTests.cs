using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaxAlgo.Strategy.Bundle;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

public sealed class DaxStrategyBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-tests", "strategy-bundle-" + Guid.NewGuid().ToString("N"));

    public DaxStrategyBundleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Identical_inputs_produce_identical_unsigned_bundle_bytes_and_content_root()
    {
        var firstPath = Path.Combine(_root, "first.daxstrategy");
        var secondPath = Path.Combine(_root, "second.daxstrategy");

        var first = DaxStrategyBundle.Pack(firstPath, Request());
        var second = DaxStrategyBundle.Pack(secondPath, Request());

        second.ContentRootSha256.Should().Be(first.ContentRootSha256);
        File.ReadAllBytes(secondPath).Should().Equal(File.ReadAllBytes(firstPath),
            "entry order, timestamps, JSON and compression are deterministic");

        var inspection = DaxStrategyBundle.Inspect(firstPath);
        inspection.ContentRootSha256.Should().Be(first.ContentRootSha256);
        inspection.Manifest.Identity.Id.Should().Be("tests.repeatable");
        inspection.Manifest.Payloads.Should().ContainSingle(p => p.Role == StrategyBundlePayloadRole.Engine);
        inspection.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.Missing);
    }

    [Fact]
    public void Signing_preserves_content_root_and_verifies_standard_dsse_pae()
    {
        var unsignedPath = Pack();
        var signedPath = Path.Combine(_root, "signed.daxstrategy");
        var unsigned = DaxStrategyBundle.Inspect(unsignedPath);
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var signed = DaxStrategyBundle.Sign(unsignedPath, signedPath, publisher, "test-key-2026");
        var verified = DaxStrategyBundle.Verify(
            signedPath,
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "test-key-2026", publisher)]);

        signed.ContentRootSha256.Should().Be(unsigned.ContentRootSha256);
        verified.IsPublisherVerified.Should().BeTrue(verified.PublisherSignature.Detail);
        verified.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.Verified);
        verified.Inspection.ContentRootSha256.Should().Be(unsigned.ContentRootSha256);

        // Independently reconstruct DSSE PAE rather than relying on the verifier under test.
        using var zip = ZipFile.OpenRead(signedPath);
        var manifest = ReadAll(zip.GetEntry(DaxStrategyBundle.ManifestEntryPath)!);
        using var envelope = JsonDocument.Parse(ReadAll(zip.GetEntry(DaxStrategyBundle.PublisherSignatureEntryPath)!));
        var signature = Convert.FromBase64String(
            envelope.RootElement.GetProperty("signatures")[0].GetProperty("sig").GetString()!);
        publisher.VerifyData(
                DssePae(DaxStrategyBundle.PublisherSignaturePayloadType, manifest),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue("publisher signatures cover standard DSSE pre-authentication bytes");
    }

    [Fact]
    public void Fixed_external_dsse_p256_vector_verifies()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERcoAbl47bX0dRNZqlnQwv5qPc4CZM+j6HIVo3W61JqpqhnI3ctvcVhofh7Iz3OC/sPTR5B4ZxGE31fCAMv2NSw==";
        const string signature =
            "QX0TX9iMnRk8bhkDJCk+KE7tp0aYX1UvXTzzdGs0E5lCcuR94qTtcST9aPdCGWbE2l6S70WgEDkZ6W0EdZFV0Q==";
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out var bytesRead);

        bytesRead.Should().Be(Convert.FromBase64String(publicKey).Length);
        key.VerifyData(
                DssePae(
                    DaxStrategyBundle.PublisherSignaturePayloadType,
                    Encoding.UTF8.GetBytes("daxstrategy-vector-v1")),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue("this committed vector was generated independently of the bundle implementation");
    }

    [Fact]
    public void A_valid_signature_from_an_unknown_key_is_not_reported_as_verified()
    {
        var signedPath = Path.Combine(_root, "signed.daxstrategy");
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DaxStrategyBundle.Sign(Pack(), signedPath, publisher, "publisher-key");

        var unknown = DaxStrategyBundle.Verify(signedPath, []);

        unknown.IsPublisherVerified.Should().BeFalse();
        unknown.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.UnknownKey);
    }

    [Fact]
    public void A_different_public_key_with_the_claimed_key_id_is_invalid()
    {
        var signedPath = Path.Combine(_root, "signed.daxstrategy");
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DaxStrategyBundle.Sign(Pack(), signedPath, publisher, "publisher-key");

        var verification = DaxStrategyBundle.Verify(
            signedPath,
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "publisher-key", impostor)]);

        verification.IsPublisherVerified.Should().BeFalse();
        verification.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.Invalid);
    }

    [Fact]
    public void Publisher_verification_binds_key_to_manifest_publisher_and_normalizes_key_id()
    {
        var signedPath = Path.Combine(_root, "publisher-bound.daxstrategy");
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DaxStrategyBundle.Sign(Pack(), signedPath, publisher, "cafe\u0301-key");

        signed.KeyId.Should().Be("caf\u00e9-key");
        using (var zip = ZipFile.OpenRead(signedPath))
        using (var envelope = JsonDocument.Parse(ReadAll(zip.GetEntry(DaxStrategyBundle.PublisherSignatureEntryPath)!)))
        {
            var signature = envelope.RootElement.GetProperty("signatures")[0];
            signature.TryGetProperty("keyid", out _).Should().BeTrue("DSSE spells the field keyid");
            signature.TryGetProperty("keyId", out _).Should().BeFalse();
        }

        var wrongPublisher = DaxStrategyBundle.Verify(
            signedPath,
            [StrategyBundlePublisherKey.FromEcdsa("someone-else", "cafe\u0301-key", publisher)]);
        wrongPublisher.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.UnknownKey);

        var verified = DaxStrategyBundle.Verify(
            signedPath,
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "cafe\u0301-key", publisher)]);
        verified.IsPublisherVerified.Should().BeTrue();

        var nonstandardEnvelope = Path.Combine(_root, "nonstandard-key-id.daxstrategy");
        File.Copy(signedPath, nonstandardEnvelope);
        byte[] envelopeBytes;
        using (var zip = ZipFile.OpenRead(nonstandardEnvelope))
            envelopeBytes = ReadAll(zip.GetEntry(DaxStrategyBundle.PublisherSignatureEntryPath)!);
        RewriteEntry(
            nonstandardEnvelope,
            DaxStrategyBundle.PublisherSignatureEntryPath,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(envelopeBytes)
                .Replace("\"keyid\"", "\"keyId\"", StringComparison.Ordinal)));
        var nonstandardAct = () => DaxStrategyBundle.Inspect(nonstandardEnvelope);
        nonstandardAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidSignatureEnvelope);
    }

    [Fact]
    public void Tampering_with_an_engine_payload_breaks_the_manifest_root_chain()
    {
        var path = Pack();
        RewriteEntry(path, "payload/engine/DaxAlgo.SamplePlugin.dll", "tampered"u8.ToArray());

        var act = () => DaxStrategyBundle.Inspect(path);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.PayloadMismatch);
    }

    [Fact]
    public void Managed_assembly_graph_is_recomputed_from_payload_metadata()
    {
        var path = Pack("graph-tamper.daxstrategy");
        byte[] manifestBytes;
        using (var zip = ZipFile.OpenRead(path))
            manifestBytes = ReadAll(zip.GetEntry(DaxStrategyBundle.ManifestEntryPath)!);
        var tampered = Encoding.UTF8.GetString(manifestBytes)
            .Replace("\"DaxAlgo.Sdk\"", "\"DaxAlgo.Sdk.Fake\"", StringComparison.Ordinal);
        RewriteEntry(path, DaxStrategyBundle.ManifestEntryPath, Encoding.UTF8.GetBytes(tampered));

        var act = () => DaxStrategyBundle.Inspect(path);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);
    }

    [Fact]
    public void Engine_closure_is_deterministic_and_excludes_unreferenced_windows_ui()
    {
        var manifest = GraphManifest(
            ["Graph.Bridge", "System.Runtime", "Graph.Leaf"],
            ["Graph.Leaf"]);

        var closure = DaxStrategyBundle.ResolveEngineClosure(manifest);

        closure.Select(static assembly => assembly.Name).Should().Equal(
            "Graph.Engine",
            "Graph.Leaf",
            "Graph.Bridge");
        closure.Select(static assembly => assembly.Role).Should().Equal(
            StrategyBundlePayloadRole.Engine,
            StrategyBundlePayloadRole.ManagedDependency,
            StrategyBundlePayloadRole.ManagedDependency);
        closure[0].References.Should().Equal("Graph.Bridge", "Graph.Leaf", "System.Runtime");
        closure[1].Length.Should().Be(103);
        closure[1].Sha256.Should().Be(new string('3', 64));
        closure.Should().NotContain(static assembly => assembly.Name == "Graph.Ui");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Engine_closure_rejects_direct_and_transitive_windows_ui_references(bool direct)
    {
        var manifest = direct
            ? GraphManifest(["Graph.Ui"], [])
            : GraphManifest(["Graph.Bridge"], ["Graph.Ui"]);

        var act = () => DaxStrategyBundle.ResolveEngineClosure(manifest);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Message.Should().Contain("Windows UI assembly 'Graph.Ui'");
    }

    [Fact]
    public void Engine_closure_rejects_reachable_assemblies_with_non_runtime_roles()
    {
        var manifest = GraphManifest(
            ["Graph.Bridge"],
            [],
            bridgeRole: StrategyBundlePayloadRole.Resource);

        var act = () => DaxStrategyBundle.ResolveEngineClosure(manifest);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Message.Should().Contain("invalid role 'resource'");
    }

    [Fact]
    public void Private_managed_dependencies_must_be_present_in_the_bundle_graph()
    {
        var payloads = new Dictionary<string, byte[]>
        {
            ["payload/engine/TradingTerminal.Tests.Headless.dll"] =
                File.ReadAllBytes(typeof(DaxStrategyBundleTests).Assembly.Location),
        };

        var act = () => StrategyBundlePayloadPolicy.DescribeManagedAssemblies(payloads);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Message.Should().Contain("is not bundled");
    }

    [Fact]
    public void Private_managed_dependency_full_identity_must_match_the_engine_reference()
    {
        var dependencyV1 = CompileManagedAssembly(
            "Graph.Private",
            "1.0.0.0",
            "namespace Graph; public sealed class PrivateDependency { }");
        var engine = CompileManagedAssembly(
            "Graph.Engine",
            "1.0.0.0",
            "namespace Graph; public sealed class Engine { public PrivateDependency Value { get; } = new(); }",
            dependencyV1);
        var dependencyV2 = CompileManagedAssembly(
            "Graph.Private",
            "2.0.0.0",
            "namespace Graph; public sealed class PrivateDependency { }");
        var payloads = new Dictionary<string, byte[]>
        {
            ["payload/engine/Graph.Engine.dll"] = engine,
            ["payload/deps/Graph.Private.dll"] = dependencyV2,
        };

        var act = () => StrategyBundlePayloadPolicy.DescribeManagedAssemblies(payloads);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Message.Should().Contain("Version=1.0.0.0")
            .And.Contain("Version=2.0.0.0");
    }

    [Theory]
    [InlineData("System.Runtime", true)]
    [InlineData("TradingTerminal.Core", true)]
    [InlineData("System.Evil", false)]
    [InlineData("TradingTerminal.DoesNotExist", false)]
    [InlineData("Microsoft.Extensions.DoesNotExist", false)]
    public void External_managed_reference_allowlist_is_exact(string assemblyName, bool expected)
    {
        StrategyBundleExternalAssemblyPolicy.IsAllowed(assemblyName).Should().Be(expected);
    }

    [Theory]
    [InlineData("ICSharpCode.AvalonEdit", true)]
    [InlineData("PresentationUI", true)]
    [InlineData("PresentationFramework.Aero2", true)]
    [InlineData("ScottPlot.WPF", true)]
    [InlineData("System.Runtime", false)]
    public void Engine_ui_reference_denylist_covers_host_ui_assemblies(string assemblyName, bool expected)
    {
        StrategyBundlePayloadPolicy.IsForbiddenEngineAssemblyReference(assemblyName).Should().Be(expected);
    }

    [Fact]
    public void Engine_dependencies_must_not_reference_host_ui_contracts()
    {
        var testAssembly = File.ReadAllBytes(typeof(DaxStrategyBundleTests).Assembly.Location);

        var act = () => StrategyBundlePayloadPolicy.Validate(
            "payload/deps/TradingTerminal.Tests.Headless.dll",
            StrategyBundlePayloadRole.ManagedDependency,
            testAssembly);

        act.Should().Throw<StrategyBundleValidationException>();
    }

    [Theory]
    [InlineData("safe\u202Eevil", "safe\\u202Eevil")]
    [InlineData("first\u2028second\u2029third", "first\\u2028second\\u2029third")]
    [InlineData("isolate\u2066text", "isolate\\u2066text")]
    public void Cli_output_escapes_bidi_line_break_and_surrogate_characters(string value, string expected)
    {
        BundleTool.TerminalSafe(value).Should().Be(expected);
    }

    [Fact]
    public void Cli_output_escapes_unpaired_surrogates()
    {
        var value = "surrogate" + new string((char)0xD800, 1);

        BundleTool.TerminalSafe(value).Should().Be("surrogate\\uD800");
    }

    [Fact]
    public void Cli_sign_does_not_overwrite_private_key_through_directory_junction()
    {
        if (!OperatingSystem.IsWindows()) return;

        var realDirectory = Path.Combine(_root, "real");
        var aliasDirectory = Path.Combine(_root, "junction");
        Directory.CreateDirectory(realDirectory);
        CreateDirectoryJunction(aliasDirectory, realDirectory);

        try
        {
            var bundlePath = Pack("junction-input.daxstrategy");
            var keyPath = Path.Combine(realDirectory, "publisher.daxstrategy");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(keyPath, key.ExportECPrivateKeyPem());
            var originalKey = File.ReadAllBytes(keyPath);

            var exitCode = BundleTool.Run(
            [
                "sign",
                "--bundle", bundlePath,
                "--key", keyPath,
                "--key-id", "junction-test-key",
                "--output", Path.Combine(aliasDirectory, "publisher.daxstrategy"),
            ]);

            exitCode.Should().Be(2);
            File.ReadAllBytes(keyPath).Should().Equal(originalKey);
        }
        finally
        {
            if (Directory.Exists(aliasDirectory)) Directory.Delete(aliasDirectory);
        }
    }

    [Fact]
    public void Cli_sign_still_allows_exact_in_place_bundle_rewrite()
    {
        var bundlePath = Pack("in-place.daxstrategy");
        var keyPath = Path.Combine(_root, "in-place-private.pem");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, key.ExportECPrivateKeyPem());

        var exitCode = BundleTool.Run(
        [
            "sign",
            "--bundle", bundlePath,
            "--key", keyPath,
            "--key-id", "in-place-test-key",
        ]);

        exitCode.Should().Be(0);
        var verification = DaxStrategyBundle.Verify(
            bundlePath,
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "in-place-test-key", key)]);
        verification.IsPublisherVerified.Should().BeTrue();
    }

    [Fact]
    public void Unlisted_payloads_and_case_aliases_are_rejected()
    {
        var unlisted = Pack("unlisted.daxstrategy");
        AddEntry(unlisted, "payload/resources/extra.txt", "extra"u8.ToArray());
        var extraAct = () => DaxStrategyBundle.Inspect(unlisted);
        extraAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.PayloadMismatch);

        var alias = Pack("alias.daxstrategy");
        AddEntry(alias, "payload/ENGINE/DaxAlgo.SamplePlugin.dll", "alias"u8.ToArray());
        var aliasAct = () => DaxStrategyBundle.Inspect(alias);
        aliasAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.DuplicatePath);
    }

    [Fact]
    public void Noncanonical_manifest_and_unknown_format_version_fail_closed()
    {
        var noncanonical = Pack("noncanonical.daxstrategy");
        byte[] noncanonicalManifest;
        using (var zip = ZipFile.OpenRead(noncanonical))
            noncanonicalManifest = ReadAll(zip.GetEntry(DaxStrategyBundle.ManifestEntryPath)!);
        RewriteEntry(
            noncanonical,
            DaxStrategyBundle.ManifestEntryPath,
            [.. noncanonicalManifest, (byte)'\n']);
        var canonicalAct = () => DaxStrategyBundle.Inspect(noncanonical);
        canonicalAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidManifest);

        var future = Pack("future.daxstrategy");
        byte[] manifest;
        using (var zip = ZipFile.OpenRead(future))
            manifest = ReadAll(zip.GetEntry(DaxStrategyBundle.ManifestEntryPath)!);
        var json = Encoding.UTF8.GetString(manifest)
            .Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal);
        RewriteEntry(future, DaxStrategyBundle.ManifestEntryPath, Encoding.UTF8.GetBytes(json));

        var versionAct = () => DaxStrategyBundle.Inspect(future);
        versionAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.UnsupportedVersion);
    }

    [Fact]
    public void Engine_cardinality_role_paths_and_managed_payload_policy_are_enforced()
    {
        var noEngine = Request() with { Payloads = [] };
        var noEngineAct = () => DaxStrategyBundle.Pack(new MemoryStream(), noEngine);
        noEngineAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);

        var wrongPath = Request() with
        {
            Payloads =
            [
                StrategyBundlePayloadSource.FromFile(
                    "payload/resources/engine.dll", StrategyBundlePayloadRole.Engine, EnginePath),
            ],
        };
        var wrongPathAct = () => DaxStrategyBundle.Pack(new MemoryStream(), wrongPath);
        wrongPathAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);

        var native = Request() with
        {
            Payloads =
            [
                StrategyBundlePayloadSource.FromBytes(
                    "payload/engine/native.dll", StrategyBundlePayloadRole.Engine, "not managed"u8),
            ],
        };
        var nativeAct = () => DaxStrategyBundle.Pack(new MemoryStream(), native);
        nativeAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);
    }

    [Fact]
    public void Engine_entry_point_must_name_the_declared_sdk_factory()
    {
        var wrongContract = Request() with
        {
            Engine = Request().Engine with
            {
                TypeName = "DaxAlgo.SamplePlugin.SampleBacktestStrategy",
            },
        };

        var act = () => DaxStrategyBundle.Pack(new MemoryStream(), wrongContract);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);
    }

    [Fact]
    public void Semantic_versions_and_host_ranges_are_strict()
    {
        var invalidPrerelease = Request() with
        {
            Identity = Request().Identity with { Version = "1.0.0-01" },
        };
        var prereleaseAct = () => DaxStrategyBundle.Pack(new MemoryStream(), invalidPrerelease);
        prereleaseAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidManifest);

        var reversedRange = Request() with
        {
            Compatibility = new StrategyBundleCompatibility("0.2.0-alpha", "2.0.0", "1.9.9"),
        };
        var rangeAct = () => DaxStrategyBundle.Pack(new MemoryStream(), reversedRange);
        rangeAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidManifest);
    }

    [Fact]
    public void Descendant_paths_and_executable_resources_are_rejected()
    {
        var conflict = Request() with
        {
            Payloads =
            [
                .. Request().Payloads,
                StrategyBundlePayloadSource.FromBytes(
                    "payload/resources/model", StrategyBundlePayloadRole.Resource, "root"u8),
                StrategyBundlePayloadSource.FromBytes(
                    "payload/resources/model/config.json", StrategyBundlePayloadRole.Resource, "child"u8),
            ],
        };
        var conflictAct = () => DaxStrategyBundle.Pack(new MemoryStream(), conflict);
        conflictAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.DuplicatePath);

        var renamedPe = Request() with
        {
            Payloads =
            [
                .. Request().Payloads,
                StrategyBundlePayloadSource.FromFile(
                    "payload/resources/model.dat", StrategyBundlePayloadRole.Resource, EnginePath),
            ],
        };
        var peAct = () => DaxStrategyBundle.Pack(new MemoryStream(), renamedPe);
        peAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);

        var script = Request() with
        {
            Payloads =
            [
                .. Request().Payloads,
                StrategyBundlePayloadSource.FromBytes(
                    "payload/resources/bootstrap.py", StrategyBundlePayloadRole.Resource, "print('no')"u8),
            ],
        };
        var scriptAct = () => DaxStrategyBundle.Pack(new MemoryStream(), script);
        scriptAct.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.InvalidPayloadSet);
    }

    [Fact]
    public void Unicode_manifest_and_managed_graph_have_frozen_canonical_bytes()
    {
        var manifest = new StrategyBundleManifest(
            StrategyBundleManifest.CurrentFormat,
            StrategyBundleManifest.CurrentFormatVersion,
            new StrategyBundleIdentity("tests.golden", "Caf\u00e9 \u0394 / \"quoted\"", "1.2.3", "tests.publisher"),
            new StrategyBundleCompatibility("0.1.1-alpha", "1.0.0"),
            new StrategyBundleEngineEntryPoint(
                "payload/engine/Golden.Engine.dll",
                "Golden.Engine.Factory",
                StrategyBundleEngineEntryPoint.CurrentContract,
                StrategyBundleEngineEntryPoint.CurrentActivation),
            [new StrategyBundleManagedAssemblyDescriptor(
                "payload/engine/Golden.Engine.dll", "Golden.Engine", ["DaxAlgo.Sdk", "System.Runtime"])],
            ["market-data.l1"],
            [new StrategyBundlePayloadDescriptor(
                "payload/engine/Golden.Engine.dll", StrategyBundlePayloadRole.Engine, 123, new string('0', 64))]);
        var canonical = StrategyBundleManifestCodec.WriteCanonical(manifest);
        const string expected =
            "{\"format\":\"daxstrategy\",\"formatVersion\":1,\"identity\":{\"id\":\"tests.golden\",\"name\":\"Caf\u00e9 \u0394 / \\\"quoted\\\"\",\"publisherId\":\"tests.publisher\",\"version\":\"1.2.3\"},\"compatibility\":{\"targetSdkVersion\":\"0.1.1-alpha\",\"minimumHostVersion\":\"1.0.0\",\"maximumHostVersion\":null},\"engine\":{\"assemblyPath\":\"payload/engine/Golden.Engine.dll\",\"typeName\":\"Golden.Engine.Factory\",\"contract\":\"daxalgo.strategy-engine-factory/1\",\"activation\":\"public-parameterless-constructor\"},\"managedAssemblies\":[{\"path\":\"payload/engine/Golden.Engine.dll\",\"name\":\"Golden.Engine\",\"references\":[\"DaxAlgo.Sdk\",\"System.Runtime\"]}],\"capabilities\":[\"market-data.l1\"],\"payloads\":[{\"path\":\"payload/engine/Golden.Engine.dll\",\"role\":\"engine\",\"length\":123,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}]}";

        Encoding.UTF8.GetString(canonical).Should().Be(expected);
        Convert.ToHexStringLower(SHA256.HashData(canonical)).Should().Be(
            "5d346ddf3734848ad71f1623a237b96c67bdbbffc0765dbe8017f7ebe73050fe");
        StrategyBundleManifestCodec.ParseCanonical(canonical, StrategyBundleLimitOptions.Default)
            .ManagedAssemblies.Should().ContainSingle();
    }

    [Fact]
    public void Configured_archive_limits_are_enforced_before_processing_payloads()
    {
        var path = Pack();
        var limits = StrategyBundleLimitOptions.Default with
        {
            MaxCompressedBundleBytes = new FileInfo(path).Length - 1,
            MaxCompressedEntryBytes = new FileInfo(path).Length - 1,
        };

        var act = () => DaxStrategyBundle.Inspect(path, limits);

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.LimitExceeded);
    }

    [Fact]
    public void Default_signature_limit_can_wrap_a_maximum_size_manifest()
    {
        var limits = StrategyBundleLimitOptions.Default;
        var envelope = StrategyBundleArchive.CreateEnvelope(
            "publisher-key",
            new byte[checked((int)limits.MaxManifestBytes)],
            new byte[64]);

        envelope.LongLength.Should().BeLessThanOrEqualTo(limits.MaxSignatureEnvelopeBytes);
        limits.Checked().Should().BeSameAs(limits);
    }

    private string Pack(string fileName = "unsigned.daxstrategy")
    {
        var path = Path.Combine(_root, fileName);
        DaxStrategyBundle.Pack(path, Request());
        return path;
    }

    private static StrategyBundlePackRequest Request() => new(
        new StrategyBundleIdentity("tests.repeatable", "Repeatable Strategy", "1.2.3", "tests.publisher"),
        new StrategyBundleCompatibility("0.2.0-alpha", "1.0.0"),
        new StrategyBundleEngineEntryPoint(
            "payload/engine/DaxAlgo.SamplePlugin.dll",
            "DaxAlgo.SamplePlugin.SampleStrategyEngineFactory",
            StrategyBundleEngineEntryPoint.CurrentContract,
            StrategyBundleEngineEntryPoint.CurrentActivation),
        ["market-data.l1", "market-data.bars"],
        [
            StrategyBundlePayloadSource.FromFile(
                "payload/engine/DaxAlgo.SamplePlugin.dll",
                StrategyBundlePayloadRole.Engine,
                EnginePath),
        ]);

    private static StrategyBundleManifest GraphManifest(
        IReadOnlyList<string> engineReferences,
        IReadOnlyList<string> bridgeReferences,
        StrategyBundlePayloadRole bridgeRole = StrategyBundlePayloadRole.ManagedDependency) => new(
        StrategyBundleManifest.CurrentFormat,
        StrategyBundleManifest.CurrentFormatVersion,
        new StrategyBundleIdentity("tests.graph", "Graph Strategy", "1.0.0", "tests.publisher"),
        new StrategyBundleCompatibility("0.2.0-alpha"),
        new StrategyBundleEngineEntryPoint(
            "payload/engine/Graph.Engine.dll",
            "Graph.Engine.Factory",
            StrategyBundleEngineEntryPoint.CurrentContract,
            StrategyBundleEngineEntryPoint.CurrentActivation),
        [
            new StrategyBundleManagedAssemblyDescriptor(
                "payload/windows/Graph.Ui.dll", "Graph.Ui", ["System.Runtime"]),
            new StrategyBundleManagedAssemblyDescriptor(
                "payload/deps/Z.Graph.Bridge.dll", "Graph.Bridge", bridgeReferences),
            new StrategyBundleManagedAssemblyDescriptor(
                "payload/engine/Graph.Engine.dll", "Graph.Engine", engineReferences),
            new StrategyBundleManagedAssemblyDescriptor(
                "payload/deps/A.Graph.Leaf.dll", "Graph.Leaf", ["System.Runtime"]),
        ],
        [],
        [
            new StrategyBundlePayloadDescriptor(
                "payload/deps/Z.Graph.Bridge.dll", bridgeRole, 102, new string('2', 64)),
            new StrategyBundlePayloadDescriptor(
                "payload/windows/Graph.Ui.dll", StrategyBundlePayloadRole.WindowsUi, 104, new string('4', 64)),
            new StrategyBundlePayloadDescriptor(
                "payload/deps/A.Graph.Leaf.dll", StrategyBundlePayloadRole.ManagedDependency, 103, new string('3', 64)),
            new StrategyBundlePayloadDescriptor(
                "payload/engine/Graph.Engine.dll", StrategyBundlePayloadRole.Engine, 101, new string('1', 64)),
        ]);

    private static string EnginePath => Path.Combine(
        AppContext.BaseDirectory, "TestPlugins", "DaxAlgo.SamplePlugin", "DaxAlgo.SamplePlugin.dll");

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void AddEntry(string archivePath, string entryPath, byte[] content)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        using var stream = zip.CreateEntry(entryPath, CompressionLevel.NoCompression).Open();
        stream.Write(content);
    }

    private static void RewriteEntry(string archivePath, string entryPath, byte[] content)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        zip.GetEntry(entryPath)!.Delete();
        using var stream = zip.CreateEntry(entryPath, CompressionLevel.NoCompression).Open();
        stream.Write(content);
    }

    private static byte[] DssePae(string payloadType, byte[] payload)
    {
        var typeBytes = Encoding.UTF8.GetBytes(payloadType);
        var prefix = Encoding.ASCII.GetBytes(
            $"DSSEv1 {typeBytes.Length} {payloadType} {payload.Length} ");
        return [.. prefix, .. payload];
    }

    private static byte[] CompileManagedAssembly(
        string assemblyName,
        string version,
        string source,
        params byte[][] additionalReferences)
    {
        var platformPaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = platformPaths
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Concat(additionalReferences.Select(static bytes => MetadataReference.CreateFromImage(bytes)));
        var syntax = CSharpSyntaxTree.ParseText(
            $"using System.Reflection; [assembly: AssemblyVersion(\"{version}\")] {source}");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntax],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink for the junction regression test.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(
            0,
            "mklink /J should create the test junction. stdout: {0}; stderr: {1}",
            standardOutput,
            standardError);
    }
}
