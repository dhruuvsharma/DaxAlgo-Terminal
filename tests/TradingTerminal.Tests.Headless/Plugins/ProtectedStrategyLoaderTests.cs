using System.IO;
using System.IO.Compression;
using System.Text;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

public sealed class ProtectedStrategyLoaderTests : IDisposable
{
    private const string InstallerRequired = "Protected strategies require the official DaxAlgo installer.";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "daxq-loader-" + Guid.NewGuid().ToString("N"));

    public ProtectedStrategyLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Daxq_without_engine_is_skipped_with_clear_non_quarantining_outcome()
    {
        var path = Path.Combine(_root, "Protected.daxq");
        WriteDaxq(path, "protected.test");
        var state = new PluginStateStore(_root);
        state.SetInstalledHash("Protected", "stale-hash-that-must-not-matter-without-an-engine");
        var errors = new List<Exception>();

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, state,
            onError: (_, ex) => errors.Add(ex), protectedStrategyEngine: null);

        report.Loaded.Should().BeEmpty();
        var problem = report.Problems.Should().ContainSingle().Subject;
        problem.PluginFolderName.Should().Be("Protected");
        problem.Outcome.Should().Be(PluginLoadOutcome.ProtectedEngineUnavailable);
        problem.Reason.Should().Be(InstallerRequired);
        errors.Should().BeEmpty("an expected open-core skip is not a loader error");
        state.QuarantineFor("Protected").Should().BeNull();
    }

    [Fact]
    public void Direct_and_folder_packages_flow_all_three_strategy_seams_through_the_guard()
    {
        var direct = Path.Combine(_root, "Direct.daxq");
        WriteDaxq(direct, "direct.test");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Nested"));
        var nested = Path.Combine(folder.FullName, "Nested.daxq");
        WriteDaxq(nested, "nested.test");
        var engine = new FakeEngine(path =>
            [Registration(Path.GetFileNameWithoutExtension(path).ToLowerInvariant())]);
        var services = new ServiceCollection();

        var report = PluginLoader.LoadWithReport(
            services, _root, SdkInfo.Version, protectedStrategyEngine: engine);

        report.Problems.Should().BeEmpty();
        report.Loaded.Should().HaveCount(2);
        engine.Paths.Should().BeEquivalentTo([direct, nested]);
        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITradingStrategy>().ToArray();
        strategies.Should().HaveCount(2);
        provider.GetServices<BacktestStrategyOption>().Should().HaveCount(2);
        provider.GetServices<StrategyFactoryRegistration>().Should().HaveCount(2);
        provider.Dispose();
        strategies.Cast<TestStrategy>().Should().OnlyContain(strategy => strategy.Disposed,
            "the provider owns protected VM strategy lifetimes");
    }

    [Fact]
    public void Canonical_folder_install_wins_over_a_leftover_direct_root_copy()
    {
        var direct = Path.Combine(_root, "download.daxq");
        WriteDaxq(direct, "same.strategy");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "same.strategy"));
        var canonical = Path.Combine(folder.FullName, "same.strategy.daxq");
        WriteDaxq(canonical, "same.strategy");
        var engine = new FakeEngine(_ => [Registration("same.strategy")]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, protectedStrategyEngine: engine);

        report.Problems.Should().BeEmpty();
        report.Loaded.Should().ContainSingle().Which.AssemblyPath.Should().Be(canonical);
        engine.Paths.Should().Equal(canonical);
    }

    [Fact]
    public void Disabled_daxq_is_skipped_before_the_engine_is_invoked()
    {
        WriteDaxq(Path.Combine(_root, "Disabled.daxq"), "disabled.test");
        var state = new PluginStateStore(_root);
        state.SetDisabled("Disabled", true);
        var engine = new FakeEngine(_ => [Registration("should.not.load")]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, state,
            protectedStrategyEngine: engine);

        report.Problems.Should().ContainSingle(p => p.Outcome == PluginLoadOutcome.Disabled);
        engine.Paths.Should().BeEmpty();
        state.QuarantineFor("Disabled").Should().BeNull();
    }

    [Fact]
    public void Protected_registration_fault_is_atomic_reported_and_quarantined()
    {
        WriteDaxq(Path.Combine(_root, "Broken.daxq"), "broken.test");
        var first = Registration("first");
        var invalid = new ProtectedStrategyRegistration(
            null!,
            new BacktestStrategyOption("invalid", "Invalid", _ => new NoopBacktestStrategy()),
            new StrategyFactoryRegistration("invalid", _ => new object(), _ => new object()));
        var engine = new FakeEngine(_ => [first, invalid]);
        var state = new PluginStateStore(_root);
        var services = new ServiceCollection();
        var errors = new List<Exception>();

        var report = PluginLoader.LoadWithReport(
            services, _root, SdkInfo.Version, state,
            onError: (_, ex) => errors.Add(ex), protectedStrategyEngine: engine);

        report.Loaded.Should().BeEmpty();
        report.Problems.Should().ContainSingle(p => p.Outcome == PluginLoadOutcome.Faulted);
        errors.Should().ContainSingle();
        state.QuarantineFor("Broken").Should().NotBeNull();
        services.Should().BeEmpty("the guarded collection commits only after every descriptor is staged");
        ((TestStrategy)first.Strategy).Disposed.Should().BeTrue("uncommitted engine resources are released");
    }

    [Fact]
    public void Protected_packages_honor_installed_hash_revocation_and_build_pins_before_the_engine()
    {
        WriteDaxq(Path.Combine(_root, "Changed.daxq"), "changed.test");
        WriteDaxq(Path.Combine(_root, "Revoked.daxq"), "revoked.test");
        WriteDaxq(Path.Combine(_root, "Pinned.daxq"), "pinned.test");
        var state = new PluginStateStore(_root);
        state.SetInstalledHash("Changed", "not-the-installed-hash");
        PluginRevocationList.Merge(_root, [new RevokedPlugin(Id: "revoked.test", Reason: "withdrawn")]);
        File.WriteAllText(Path.Combine(_root, PluginTrustedHashes.FileName),
            """{"plugins":[{"plugin":"Pinned","assemblies":{"Pinned.daxq":"not-the-shipped-hash"}}]}""");
        var engine = new FakeEngine(_ => [Registration("unexpected")]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, state,
            protectedStrategyEngine: engine);

        report.Problems.Should().HaveCount(3);
        report.Problems.Should().Contain(p => p.PluginFolderName == "Changed" && p.Outcome == PluginLoadOutcome.Tampered);
        report.Problems.Should().Contain(p => p.PluginFolderName == "Revoked" && p.Outcome == PluginLoadOutcome.Revoked);
        report.Problems.Should().Contain(p => p.PluginFolderName == "Pinned" && p.Outcome == PluginLoadOutcome.Tampered);
        state.Quarantined.Select(q => q.Plugin).Should().BeEquivalentTo(["Changed", "Revoked", "Pinned"]);
        engine.Paths.Should().BeEmpty();
    }

    [Fact]
    public void Detector_requires_a_root_daxq_manifest_and_bounds_its_cleartext_read()
    {
        WriteDaxq(Path.Combine(_root, "WrongKind.daxq"), "wrong.test", kind: "other");
        WriteDaxq(Path.Combine(_root, "NestedManifest.daxq"), "nested-manifest.test",
            manifestEntryName: "nested/manifest.json");
        WriteDaxq(Path.Combine(_root, "Oversized.daxq"), "oversized.test", paddingLength: 70_000);
        var engine = new FakeEngine(_ => [Registration("unexpected")]);

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), _root, SdkInfo.Version, protectedStrategyEngine: engine);

        report.Loaded.Should().BeEmpty();
        report.Problems.Should().ContainSingle(p =>
            p.PluginFolderName == "Oversized" && p.Outcome == PluginLoadOutcome.ManifestInvalid);
        engine.Paths.Should().BeEmpty();
    }

    private static ProtectedStrategyRegistration Registration(string id) => new(
        new TestStrategy(id),
        new BacktestStrategyOption(id, id, _ => new NoopBacktestStrategy()),
        new StrategyFactoryRegistration(id, _ => new object(), _ => new object()));

    private static void WriteDaxq(
        string path,
        string strategyId,
        string kind = "daxq",
        string manifestEntryName = "manifest.json",
        int paddingLength = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(manifestEntryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write($$"""{"kind":"{{kind}}","strategyId":"{{strategyId}}","padding":"{{new string('x', paddingLength)}}"}""");
    }

    private sealed class FakeEngine(Func<string, IReadOnlyList<ProtectedStrategyRegistration>> load)
        : IProtectedStrategyEngine
    {
        public List<string> Paths { get; } = [];

        public IReadOnlyList<ProtectedStrategyRegistration> LoadStrategies(string daxqPath)
        {
            Paths.Add(daxqPath);
            return load(daxqPath);
        }
    }

    private sealed class TestStrategy(string id) : ITradingStrategy, IDisposable
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public string Description => "Protected strategy loader fixture.";
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class NoopBacktestStrategy : IBacktestStrategy
    {
        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    }
}
