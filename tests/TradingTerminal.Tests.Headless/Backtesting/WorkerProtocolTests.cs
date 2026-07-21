using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;
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

        json.Should().Contain("\"protocol_version\":2");
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
    public void Installed_bundle_request_round_trips_typed_parameters_and_evidence()
    {
        var native = WorkerTestData.Request("bundle-protocol");
        var run = native.Run with
        {
            StrategyId = "tests.bundle",
            Parameters = StrategyParameters.Empty,
        };
        BacktestStrategyParameter[] parameters =
        [
            new() { Key = "enabled", Kind = BacktestStrategyParameterKind.Boolean, BooleanValue = true },
            new() { Key = "lookback", Kind = BacktestStrategyParameterKind.Integer, IntegerValue = 21 },
            new() { Key = "mode", Kind = BacktestStrategyParameterKind.Choice, StringValue = "fast" },
            new() { Key = "threshold", Kind = BacktestStrategyParameterKind.Number, NumberValue = 1.25 },
        ];
        var request = BacktestJobRequest.CreateInstalledBundle(
            native.JobId,
            run,
            native.Input,
            new BacktestInstalledBundleReference
            {
                PublisherId = "tests.publisher",
                StrategyVersion = "1.2.3",
                ContentRootSha256 = new string('a', 64),
                ArchiveSha256 = new string('b', 64),
                TrustEvidence = new BacktestBundleTrustEvidence
                {
                    Kind = BacktestBundleTrustKind.VerifiedPublisher,
                    PublisherKeyId = "publisher-key",
                    PublisherKeyFingerprintSha256 = new string('d', 64),
                },
            },
            native.ExpectedHostEngineAssemblySha256,
            new string('c', 64),
            parameters);

        var json = BacktestProtocolJson.Serialize(request);
        var roundTrip = BacktestProtocolJson.Deserialize<BacktestJobRequest>(json);

        BacktestProtocolValidator.Validate(roundTrip);
        roundTrip.Strategy.Source.Should().Be(BacktestStrategySource.InstalledBundle);
        roundTrip.Strategy.ActivationParameters.Should().BeEquivalentTo(parameters, options => options.WithStrictOrdering());
        roundTrip.ParametersSha256.Should().Be(BacktestProtocolHash.ComputeActivationParametersSha256(parameters));
    }

    [Fact]
    public void Validator_rejects_bundle_source_confusion_and_parameter_tampering()
    {
        var native = WorkerTestData.Request("bundle-invalid");
        var run = native.Run with { StrategyId = "tests.bundle", Parameters = StrategyParameters.Empty };
        BacktestStrategyParameter[] parameters =
        [
            new() { Key = "lookback", Kind = BacktestStrategyParameterKind.Integer, IntegerValue = 21 },
        ];
        var request = BacktestJobRequest.CreateInstalledBundle(
            native.JobId,
            run,
            native.Input,
            new BacktestInstalledBundleReference
            {
                PublisherId = "tests.publisher",
                StrategyVersion = "1.0.0",
                ContentRootSha256 = new string('a', 64),
                ArchiveSha256 = new string('b', 64),
                TrustEvidence = new BacktestBundleTrustEvidence
                {
                    Kind = BacktestBundleTrustKind.UnsignedLocalDevelopment,
                },
            },
            native.ExpectedHostEngineAssemblySha256,
            new string('c', 64),
            parameters);

        Action missingBundle = () => BacktestProtocolValidator.Validate(request with
        {
            Strategy = request.Strategy with { InstalledBundle = null },
        });
        Action duplicate = () => BacktestProtocolValidator.Validate(request with
        {
            Strategy = request.Strategy with
            {
                ActivationParameters = [parameters[0], parameters[0]],
            },
        });
        Action nativeWithBundle = () => BacktestProtocolValidator.Validate(native with
        {
            Strategy = native.Strategy with { InstalledBundle = request.Strategy.InstalledBundle },
        });
        Action missingTrustEvidence = () => BacktestProtocolValidator.Validate(request with
        {
            Strategy = request.Strategy with
            {
                InstalledBundle = request.Strategy.InstalledBundle! with { TrustEvidence = null! },
            },
        });

        missingBundle.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("missing_bundle_reference");
        duplicate.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("duplicate_activation_parameter");
        nativeWithBundle.Should().Throw<BacktestProtocolException>().Which.Code.Should().Be("unexpected_bundle_reference");
        missingTrustEvidence.Should().Throw<BacktestProtocolException>()
            .Which.Code.Should().Be("missing_bundle_trust_evidence");
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
