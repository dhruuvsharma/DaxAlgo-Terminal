using System.Security.Cryptography;
using System.Runtime.InteropServices;
using DaxAlgo.Strategy.Bundle;
using FluentAssertions;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

public sealed class StrategyBundleStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-tests", "strategy-store-" + Guid.NewGuid().ToString("N"));

    public StrategyBundleStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Local_install_creates_immutable_objects_receipt_and_resolvable_activation()
    {
        var bundlePath = Pack("local.daxstrategy");
        var store = CreateStore();

        var installed = store.Install(bundlePath, LocalPolicy());
        var active = store.Activate(
            installed.Receipt.ContentRootSha256,
            installed.Receipt.ArchiveSha256,
            LocalPolicy());
        var resolved = store.ResolveActive("tests.store", LocalPolicy());

        installed.Receipt.Schema.Should().Be(StrategyBundleInstallReceipt.CurrentSchema);
        installed.Receipt.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.Missing);
        installed.ObjectDirectory.Should().Be(active.ObjectDirectory).And.Be(resolved.ObjectDirectory);
        installed.ArchivePath.Should().Be(resolved.ArchivePath);
        File.Exists(installed.ManifestPath).Should().BeTrue();
        File.Exists(installed.ReceiptPath).Should().BeTrue();
        File.ReadAllBytes(installed.ManifestPath)
            .Should().Equal(StrategyBundleManifestCodec.WriteCanonical(installed.Manifest));
    }

    [Fact]
    public void Publisher_policy_rejects_unsigned_and_local_policy_rejects_invalid_signature()
    {
        var unsignedPath = Pack("unsigned.daxstrategy");
        var store = CreateStore();
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustedKey = StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "publisher-key", trusted);

        var unsignedAct = () => store.Install(
            unsignedPath,
            StrategyBundleInstallPolicy.VerifiedPublisher("1.5.0", "0.2.0-alpha", [trustedKey]));

        unsignedAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.SignatureRejected);

        var signedPath = Path.Combine(_root, "signed-by-other-key.daxstrategy");
        using var untrustedSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DaxStrategyBundle.Sign(unsignedPath, signedPath, untrustedSigner, "publisher-key");
        var invalidAct = () => store.Install(
            signedPath,
            StrategyBundleInstallPolicy.LocalDevelopment("1.5.0", "0.2.0-alpha", [trustedKey]));

        invalidAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.SignatureRejected);
    }

    [Fact]
    public void Verified_publisher_bundle_installs_and_reverifies()
    {
        var unsignedPath = Pack("unsigned-for-signing.daxstrategy");
        var signedPath = Path.Combine(_root, "verified.daxstrategy");
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DaxStrategyBundle.Sign(unsignedPath, signedPath, publisher, "verified-key");
        var policy = StrategyBundleInstallPolicy.VerifiedPublisher(
            "1.5.0",
            "0.2.0-alpha",
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "verified-key", publisher)]);
        var store = CreateStore();

        var installed = store.Install(signedPath, policy);
        var verified = store.VerifyInstallation(
            installed.Receipt.ContentRootSha256,
            installed.Receipt.ArchiveSha256,
            policy);

        verified.Receipt.PublisherSignature.Status.Should().Be(StrategyBundleSignatureStatus.Verified);
        verified.Receipt.PublisherSignature.KeyId.Should().Be("verified-key");
        verified.Receipt.PublisherSignature.KeyFingerprintSha256.Should().Be(
            Convert.ToHexStringLower(SHA256.HashData(publisher.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Install_policy_does_not_expose_mutable_trust_root_storage()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = publisher.ExportSubjectPublicKeyInfo();
        var policy = StrategyBundleInstallPolicy.VerifiedPublisher(
            "1.5.0",
            "0.2.0-alpha",
            [StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "publisher-key", publisher)]);
        var exposed = policy.TrustedPublisherKeys.Single().SubjectPublicKeyInfo;
        MemoryMarshal.TryGetArray(exposed, out var exposedSegment).Should().BeTrue();

        exposedSegment.Array![exposedSegment.Offset] ^= 0xff;

        policy.TrustedPublisherKeys.Single().SubjectPublicKeyInfo.ToArray().Should().Equal(original);
    }

    [Fact]
    public void Resigning_same_content_adds_evidence_without_mutating_content_object()
    {
        var unsignedPath = Pack("resign-source.daxstrategy");
        var firstPath = Path.Combine(_root, "first-signature.daxstrategy");
        var secondPath = Path.Combine(_root, "second-signature.daxstrategy");
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DaxStrategyBundle.Sign(unsignedPath, firstPath, firstKey, "first-key");
        DaxStrategyBundle.Sign(unsignedPath, secondPath, secondKey, "second-key");
        var policy = StrategyBundleInstallPolicy.VerifiedPublisher(
            "1.5.0",
            "0.2.0-alpha",
            [
                StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "first-key", firstKey),
                StrategyBundlePublisherKey.FromEcdsa("tests.publisher", "second-key", secondKey),
            ]);
        var store = CreateStore();

        var first = store.Install(firstPath, policy);
        var contentBefore = Directory.GetFiles(first.ObjectDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(first.ObjectDirectory, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        var second = store.Install(secondPath, policy);

        second.Receipt.ContentRootSha256.Should().Be(first.Receipt.ContentRootSha256);
        second.Receipt.ArchiveSha256.Should().NotBe(first.Receipt.ArchiveSha256);
        second.ObjectDirectory.Should().Be(first.ObjectDirectory);
        Directory.GetDirectories(Path.Combine(store.RootDirectory, "objects", "sha256"))
            .Should().ContainSingle();
        Directory.GetDirectories(Path.Combine(store.RootDirectory, "evidence", "sha256"))
            .Should().HaveCount(2);
        foreach (var (relativePath, bytes) in contentBefore)
            File.ReadAllBytes(Path.Combine(second.ObjectDirectory, relativePath)).Should().Equal(bytes);
    }

    [Fact]
    public void Verification_rejects_payload_tamper_and_extra_files()
    {
        var firstStore = CreateStore("tamper-store");
        var first = firstStore.Install(Pack("tamper.daxstrategy"), LocalPolicy());
        var enginePath = Path.Combine(
            first.ObjectDirectory,
            first.Manifest.Engine.AssemblyPath.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(enginePath, "tamper");

        var tamperAct = () => firstStore.VerifyInstallation(
            first.Receipt.ContentRootSha256,
            first.Receipt.ArchiveSha256,
            LocalPolicy());

        tamperAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.CorruptInstallation);

        var secondStore = CreateStore("extra-store");
        var second = secondStore.Install(Pack("extra.daxstrategy"), LocalPolicy());
        File.WriteAllText(Path.Combine(second.ObjectDirectory, "unexpected.txt"), "extra");

        var extraAct = () => secondStore.VerifyInstallation(
            second.Receipt.ContentRootSha256,
            second.Receipt.ArchiveSha256,
            LocalPolicy());

        extraAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.CorruptInstallation);
    }

    [Fact]
    public void Activation_rechecks_current_publisher_policy()
    {
        var store = CreateStore();
        var installed = store.Install(Pack("activation-policy.daxstrategy"), LocalPolicy());
        store.Activate(
            installed.Receipt.ContentRootSha256,
            installed.Receipt.ArchiveSha256,
            LocalPolicy());

        var act = () => store.ResolveActive(
            "tests.store",
            StrategyBundleInstallPolicy.VerifiedPublisher(
                "1.5.0",
                "0.2.0-alpha",
                Array.Empty<StrategyBundlePublisherKey>()));

        act.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.SignatureRejected);
    }

    [Fact]
    public void Install_enforces_sdk_and_host_compatibility_before_writing_objects()
    {
        var sdkStore = CreateStore("sdk-store");
        var sdkAct = () => sdkStore.Install(
            Pack("sdk-mismatch.daxstrategy"),
            StrategyBundleInstallPolicy.LocalDevelopment("1.5.0", "0.3.0-alpha"));

        sdkAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.IncompatibleSdk);
        Directory.GetDirectories(Path.Combine(sdkStore.RootDirectory, "objects", "sha256"))
            .Should().BeEmpty();

        var hostStore = CreateStore("host-store");
        var hostAct = () => hostStore.Install(
            Pack("host-mismatch.daxstrategy"),
            StrategyBundleInstallPolicy.LocalDevelopment("0.9.9", "0.2.0-alpha"));

        hostAct.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.IncompatibleHost);
        Directory.GetDirectories(Path.Combine(hostStore.RootDirectory, "objects", "sha256"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Install_requires_exact_sdk_identity_including_build_metadata()
    {
        var store = CreateStore("sdk-identity-store");
        var bundlePath = Pack("sdk-build-metadata.daxstrategy", "0.2.0-alpha+bundle");

        var act = () => store.Install(
            bundlePath,
            StrategyBundleInstallPolicy.LocalDevelopment("1.5.0", "0.2.0-alpha+host"));

        act.Should().Throw<StrategyBundleStoreException>()
            .Which.Error.Should().Be(StrategyBundleStoreError.IncompatibleSdk);
        Directory.GetDirectories(Path.Combine(store.RootDirectory, "objects", "sha256"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Install_honors_compressed_bundle_limit_before_creating_objects()
    {
        var bundlePath = Pack("bounded.daxstrategy");
        var maximum = new FileInfo(bundlePath).Length - 1;
        var store = new StrategyBundleStore(
            Path.Combine(_root, "bounded-store"),
            StrategyBundleLimitOptions.Default with
            {
                MaxCompressedBundleBytes = maximum,
                MaxCompressedEntryBytes = maximum,
            });

        var act = () => store.Install(bundlePath, LocalPolicy());

        act.Should().Throw<StrategyBundleValidationException>()
            .Which.Error.Should().Be(StrategyBundleValidationError.LimitExceeded);
        Directory.GetDirectories(Path.Combine(store.RootDirectory, "objects", "sha256"))
            .Should().BeEmpty();
    }

    private StrategyBundleStore CreateStore(string name = "store") =>
        new(Path.Combine(_root, name));

    private string Pack(string fileName, string targetSdkVersion = "0.2.0-alpha")
    {
        var path = Path.Combine(_root, fileName);
        DaxStrategyBundle.Pack(path, Request(targetSdkVersion));
        return path;
    }

    private static StrategyBundleInstallPolicy LocalPolicy() =>
        StrategyBundleInstallPolicy.LocalDevelopment("1.5.0", "0.2.0-alpha");

    private static StrategyBundlePackRequest Request(string targetSdkVersion = "0.2.0-alpha") => new(
        new StrategyBundleIdentity("tests.store", "Store Strategy", "1.2.3", "tests.publisher"),
        new StrategyBundleCompatibility(targetSdkVersion, "1.0.0", "2.0.0"),
        new StrategyBundleEngineEntryPoint(
            "payload/engine/DaxAlgo.SamplePlugin.dll",
            "DaxAlgo.SamplePlugin.SampleStrategyEngineFactory",
            StrategyBundleEngineEntryPoint.CurrentContract,
            StrategyBundleEngineEntryPoint.CurrentActivation),
        ["market-data.l1"],
        [
            StrategyBundlePayloadSource.FromFile(
                "payload/engine/DaxAlgo.SamplePlugin.dll",
                StrategyBundlePayloadRole.Engine,
                EnginePath),
        ]);

    private static string EnginePath => Path.Combine(
        AppContext.BaseDirectory,
        "TestPlugins",
        "DaxAlgo.SamplePlugin",
        "DaxAlgo.SamplePlugin.dll");
}
