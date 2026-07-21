using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Backtest.Protocol;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Request_round_trips_with_canonical_versions_and_parameter_hash()
    {
        var request = WorkerTestData.Request();

        var json = BacktestProtocolJson.Serialize(request);
        var roundTrip = BacktestProtocolJson.Deserialize<BacktestJobRequest>(json);

        json.Should().Contain("\"protocol_version\":1");
        roundTrip.Should().BeEquivalentTo(request);
        var act = () => BacktestProtocolValidator.Validate(roundTrip);
        act.Should().NotThrow();
    }

    [Fact]
    public void Report_artifact_round_trips_metric_bag_into_BacktestReport()
    {
        var artifact = WorkerTestData.ReportArtifact();

        var roundTrip = BacktestProtocolJson.Deserialize<BacktestReportArtifact>(
            BacktestProtocolJson.Serialize(artifact));
        var report = roundTrip.ToReport();

        report.Summary.EventsProcessed.Should().Be(500);
        report.Metrics.Sharpe.Should().Be(1.25);
        report.Equity.Should().ContainSingle();
    }

    [Fact]
    public void Validator_rejects_tampered_parameter_bag()
    {
        var request = WorkerTestData.Request();
        var tampered = request with
        {
            Run = request.Run with { Parameters = request.Run.ParametersOrEmpty.With("qty", 99) },
        };

        var act = () => BacktestProtocolValidator.Validate(tampered);

        act.Should().Throw<BacktestProtocolException>()
            .Which.Code.Should().Be("parameters_hash_mismatch");
    }

    [Fact]
    public void Validator_rejects_incompatible_component_versions()
    {
        var request = WorkerTestData.Request();

        Action engine = () => BacktestProtocolValidator.Validate(request with { EngineVersion = "2.0" });
        Action sdk = () => BacktestProtocolValidator.Validate(request with { SdkVersion = "9.0" });
        Action strategy = () => BacktestProtocolValidator.Validate(request with
        {
            StrategyContractVersion = "2.0",
            Strategy = request.Strategy with { ContractVersion = "2.0" },
        });

        engine.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("unsupported_engine_version");
        sdk.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("unsupported_sdk_version");
        strategy.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("unsupported_strategy_contract_version");
    }

    [Fact]
    public void Json_rejects_numeric_enum_values()
    {
        var json = BacktestProtocolJson.Serialize(WorkerTestData.Request())
            .Replace("\"kind\":\"synthetic\"", "\"kind\":0", StringComparison.Ordinal);

        var act = () => BacktestProtocolJson.Deserialize<BacktestJobRequest>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Json_rejects_an_omitted_compatibility_version()
    {
        var json = BacktestProtocolJson.Serialize(WorkerTestData.Request())
            .Replace(
                $"\"engine_version\":\"{BacktestProtocolVersions.ManagedEngine}\",",
                string.Empty,
                StringComparison.Ordinal);

        var act = () => BacktestProtocolJson.Deserialize<BacktestJobRequest>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Validator_reports_missing_required_object_graph_nodes()
    {
        var request = WorkerTestData.Request();

        Action strategy = () => BacktestProtocolValidator.Validate(request with { Strategy = null! });
        Action run = () => BacktestProtocolValidator.Validate(request with { Run = null! });
        Action universe = () => BacktestProtocolValidator.Validate(request with
        {
            Run = request.Run with { Universe = null! },
        });
        Action input = () => BacktestProtocolValidator.Validate(request with { Input = null! });

        strategy.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("missing_strategy");
        run.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("missing_run");
        universe.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("missing_universe");
        input.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("missing_input");
    }

    [Fact]
    public void Validator_requires_the_exact_schema_for_each_input_kind()
    {
        var request = WorkerTestData.Request();
        var invalid = request with { Input = request.Input with { Schema = "synthetic-quotes-v2" } };

        var act = () => BacktestProtocolValidator.Validate(invalid);

        act.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("unsupported_input_schema");
    }
}
