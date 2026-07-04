using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Ml;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Round-trip coverage for the SQLite trained-model registry: save/version/load, latest-per-key
/// resolution, listing/filtering, soft-delete, and retention pruning against a throwaway DB file.
/// </summary>
public sealed class SqliteModelRegistryTests : IDisposable
{
    private readonly string _path;

    public SqliteModelRegistryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mlmodels_{Guid.NewGuid():N}.db");
    }

    private SqliteModelRegistry NewRegistry() => new(_path);

    [Fact]
    public void Save_AssignsVersionOneAndDigest_AndLoadLatestReturnsIt()
    {
        using var reg = NewRegistry();
        var artifact = Artifact(mae: 1.25);

        var stored = reg.Save(artifact);

        stored.Version.Should().Be(1);
        stored.Sha256.Should().NotBeNullOrEmpty();
        stored.ModelId.Should().NotBeNullOrEmpty();

        var latest = reg.LoadLatest(artifact.Key);
        latest.Should().NotBeNull();
        latest!.InstrumentKey.Should().Be(artifact.InstrumentKey);
        latest.Metrics.MlMaeTicks.Should().Be(1.25);
        latest.Bank("nextbar")!.Learners[0].Coefficients.Should().Equal(0.1, 0.2);
    }

    [Fact]
    public void Save_SameKeyTwice_IncrementsVersion_AndLoadLatestReturnsNewest()
    {
        using var reg = NewRegistry();
        var v1 = reg.Save(Artifact(mae: 2.0));
        var v2 = reg.Save(Artifact(mae: 1.0));

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);

        var latest = reg.LoadLatest(Key());
        latest!.Metrics.MlMaeTicks.Should().Be(1.0, "the newest version wins");
    }

    [Fact]
    public void Load_ByModelId_ReturnsThatSpecificVersion()
    {
        using var reg = NewRegistry();
        var v1 = reg.Save(Artifact(mae: 2.0));
        reg.Save(Artifact(mae: 1.0));

        var loaded = reg.Load(v1.ModelId);
        loaded!.Metrics.MlMaeTicks.Should().Be(2.0);
    }

    [Fact]
    public void DifferentKeys_AreIsolated()
    {
        using var reg = NewRegistry();
        reg.Save(Artifact(instrument: "SIM:AAA"));
        reg.Save(Artifact(instrument: "SIM:BBB"));

        reg.LoadLatest(Key("SIM:AAA")).Should().NotBeNull();
        reg.LoadLatest(Key("SIM:BBB")).Should().NotBeNull();
        reg.LoadLatest(Key("SIM:CCC")).Should().BeNull();

        reg.List(null, 10).Should().HaveCount(2);
        reg.List(Key("SIM:AAA"), 10).Should().ContainSingle();
    }

    [Fact]
    public void Delete_SoftDeletes_AndLoadLatestFallsBackToOlderVersion()
    {
        using var reg = NewRegistry();
        reg.Save(Artifact(mae: 2.0));                 // v1
        var v2 = reg.Save(Artifact(mae: 1.0));        // v2

        reg.Delete(v2.ModelId).Should().BeTrue();
        reg.Delete(v2.ModelId).Should().BeFalse("already deleted");

        var latest = reg.LoadLatest(Key());
        latest!.Metrics.MlMaeTicks.Should().Be(2.0, "v2 is gone, v1 is the newest live version");
        reg.Load(v2.ModelId).Should().BeNull();
    }

    [Fact]
    public void PruneOlderThan_SoftDeletesStaleModels()
    {
        using var reg = NewRegistry();
        reg.Save(Artifact(created: DateTime.UtcNow.AddDays(-100)));
        reg.Save(Artifact(created: DateTime.UtcNow, mae: 0.5));

        var pruned = reg.PruneOlderThan(30);

        pruned.Should().Be(1);
        var latest = reg.LoadLatest(Key());
        latest!.Metrics.MlMaeTicks.Should().Be(0.5, "only the fresh model survives");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static ModelKey Key(string instrument = "SIM:BTCUSD") =>
        new("footprint-nextbar", instrument, "1m", "rls");

    private static ModelArtifact Artifact(string instrument = "SIM:BTCUSD", double mae = 1.5, DateTime? created = null) =>
        new(
            SchemaVersion: ModelArtifact.CurrentSchemaVersion,
            ModelKind: "footprint-nextbar",
            Algorithm: "rls",
            InstrumentKey: instrument,
            Timeframe: "1m",
            Features: new FeatureContract(2, new[] { "bias", "x" }),
            OptionsJson: "{}",
            Banks: new[]
            {
                new BankState("nextbar", new[]
                {
                    new ForecasterState("rls", 2, 10, new[] { 0.1, 0.2 }, new[] { 1.0, 0.0, 0.0, 1.0 }),
                }),
            },
            Scaler: new FeatureScalerState(2, 10, new[] { 0.0, 0.5 }, new[] { 0.0, 1.0 }),
            Scalars: new[] { new ScalarState("tick_size", 0.25) },
            Metrics: new ModelMetrics(mae, 0.6, 2.0, 0.5, 42),
            SamplesTrained: 10,
            TrainedThroughUtc: created ?? DateTime.UtcNow,
            CreatedUtc: created ?? DateTime.UtcNow);

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort temp cleanup */ }
    }
}
