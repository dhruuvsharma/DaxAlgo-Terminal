namespace TradingTerminal.Backtest.Protocol;

public sealed class BacktestProtocolException : Exception
{
    public BacktestProtocolException(string code, string message) : base(message) => Code = code;
    public BacktestProtocolException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>Pure request validation shared by the client and worker; filesystem checks stay local.</summary>
public static class BacktestProtocolValidator
{
    public static void Validate(BacktestJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Require(request.ProtocolVersion == BacktestProtocolVersions.Current, "unsupported_protocol",
            $"Protocol {request.ProtocolVersion} is unsupported; expected {BacktestProtocolVersions.Current}.");
        ValidateJobId(request.JobId);
        var strategy = request.Strategy
                       ?? throw new BacktestProtocolException("missing_strategy", "Strategy is required.");
        var run = request.Run
                  ?? throw new BacktestProtocolException("missing_run", "Run is required.");
        var universe = run.Universe
                       ?? throw new BacktestProtocolException("missing_universe", "Run.Universe is required.");
        var instruments = universe.Instruments
                          ?? throw new BacktestProtocolException("missing_instruments", "Run.Universe.Instruments is required.");
        _ = run.Data ?? throw new BacktestProtocolException("missing_data_spec", "Run.Data is required.");
        var input = request.Input
                    ?? throw new BacktestProtocolException("missing_input", "Input is required.");
        var limits = request.Limits
                     ?? throw new BacktestProtocolException("missing_limits", "Limits are required.");
        var artifacts = request.Artifacts
                        ?? throw new BacktestProtocolException("missing_artifacts", "Artifacts are required.");

        RequireNonEmpty(request.EngineVersion, "invalid_engine_version", 64);
        RequireNonEmpty(request.SdkVersion, "invalid_sdk_version", 64);
        RequireNonEmpty(request.StrategyContractVersion, "invalid_strategy_contract_version", 64);
        Require(string.Equals(request.EngineVersion, BacktestProtocolVersions.ManagedEngine, StringComparison.Ordinal),
            "unsupported_engine_version",
            $"Engine version '{request.EngineVersion}' is unsupported; expected '{BacktestProtocolVersions.ManagedEngine}'.");
        Require(string.Equals(request.SdkVersion, BacktestProtocolVersions.Sdk, StringComparison.Ordinal),
            "unsupported_sdk_version",
            $"SDK version '{request.SdkVersion}' is unsupported; expected '{BacktestProtocolVersions.Sdk}'.");
        Require(string.Equals(request.StrategyContractVersion, BacktestProtocolVersions.StrategyContract, StringComparison.Ordinal),
            "unsupported_strategy_contract_version",
            $"Strategy contract version '{request.StrategyContractVersion}' is unsupported; expected '{BacktestProtocolVersions.StrategyContract}'.");
        Require(strategy.Source == BacktestStrategySource.Native, "unsupported_strategy_source",
            "P2 workers execute canonical native kernels only.");
        RequireNonEmpty(strategy.Id, "invalid_strategy_id", 128);
        Require(strategy.ContractVersion == request.StrategyContractVersion, "strategy_contract_mismatch",
            "The strategy reference and request declare different contract versions.");
        if (strategy.ExpectedAssemblySha256 is not null)
            Require(BacktestProtocolHash.IsSha256(strategy.ExpectedAssemblySha256), "invalid_strategy_hash",
                "ExpectedAssemblySha256 must be a 64-character hexadecimal SHA-256.");

        Require(!string.IsNullOrWhiteSpace(run.StrategyId), "missing_run_strategy",
            "Run.StrategyId is required for worker dispatch.");
        Require(string.Equals(run.StrategyId, strategy.Id, StringComparison.OrdinalIgnoreCase),
            "strategy_id_mismatch", "Run.StrategyId and Strategy.Id must match.");
        Require(BacktestProtocolHash.IsSha256(request.ParametersSha256), "invalid_parameters_hash",
            "ParametersSha256 must be a 64-character hexadecimal SHA-256.");
        Require(string.Equals(
                request.ParametersSha256,
                BacktestProtocolHash.ComputeParametersSha256(run.ParametersOrEmpty),
                StringComparison.OrdinalIgnoreCase),
            "parameters_hash_mismatch", "The parameter bag does not match ParametersSha256.");

        Require(instruments.Count > 0, "empty_universe",
            "At least one instrument is required.");
        Require(instruments.Count <= 128, "universe_too_large",
            "A worker job may contain at most 128 instruments.");
        Require(instruments.All(x => x is not null), "invalid_instrument",
            "Universe instruments cannot contain null entries.");
        Require(instruments.All(x => !x.Id.IsNone && x.Contract is not null), "invalid_instrument",
            "Every universe instrument must have a canonical non-zero InstrumentId.");
        Require(instruments.All(x => double.IsFinite(x.TickSize) && x.TickSize > 0 &&
                                     double.IsFinite(x.ContractMultiplier) && x.ContractMultiplier > 0),
            "invalid_instrument_economics", "Tick size and contract multiplier must be finite and positive.");
        Require(double.IsFinite(run.StartingCash) && run.StartingCash > 0,
            "invalid_starting_cash", "StartingCash must be finite and greater than zero.");

        ValidateInput(input);
        ValidateLimits(limits);
        Require(artifacts.IncludeReport, "missing_report_artifact",
            "The P2 worker requires the report artifact.");

        if (request.DeadlineUtc is { } deadline)
        {
            Require(deadline.Kind == DateTimeKind.Utc, "invalid_deadline", "DeadlineUtc must have UTC kind.");
            Require(deadline > DateTime.UtcNow, "expired_deadline", "DeadlineUtc has already elapsed.");
        }
    }

    public static void ValidateJobId(string jobId)
    {
        Require(!string.IsNullOrWhiteSpace(jobId), "invalid_job_id", "JobId is required.");
        Require(jobId.Length <= BacktestProtocolLimits.MaxJobIdCharacters, "invalid_job_id",
            $"JobId may contain at most {BacktestProtocolLimits.MaxJobIdCharacters} characters.");
        Require(jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "invalid_job_id",
            "JobId may contain only ASCII letters, digits, '-' and '_'.");
    }

    private static void ValidateInput(BacktestInputReference input)
    {
        RequireNonEmpty(input.Schema, "invalid_input_schema", 128);
        RequireNonEmpty(input.Provenance, "invalid_input_provenance", 512);
        Require(string.Equals(input.OrderingPolicy, "timestamp_utc_ascending", StringComparison.Ordinal),
            "unsupported_ordering", "Only timestamp_utc_ascending input ordering is supported.");

        switch (input.Kind)
        {
            case BacktestInputKind.Synthetic:
                Require(string.Equals(input.Schema, "synthetic-quotes-v1", StringComparison.Ordinal),
                    "unsupported_input_schema", "Synthetic input requires schema 'synthetic-quotes-v1'.");
                Require(input.Synthetic is not null, "missing_synthetic_spec", "Synthetic input settings are required.");
                var synthetic = input.Synthetic!;
                Require(input.Path is null && input.Sha256 is null && input.LengthBytes is null,
                    "invalid_synthetic_reference", "Synthetic input must not carry file identity fields.");
                Require(synthetic.EventCount > 0 && synthetic.EventCount <= BacktestProtocolLimits.MaxSyntheticEvents,
                    "invalid_synthetic_count", $"Synthetic EventCount must be between 1 and {BacktestProtocolLimits.MaxSyntheticEvents}.");
                Require(double.IsFinite(synthetic.StartPrice) && synthetic.StartPrice > 0,
                    "invalid_synthetic_price", "Synthetic StartPrice must be finite and greater than zero.");
                Require(double.IsFinite(synthetic.Spread) && synthetic.Spread > 0,
                    "invalid_synthetic_spread", "Synthetic Spread must be finite and greater than zero.");
                break;

            case BacktestInputKind.Parquet:
                Require(string.Equals(input.Schema, "daxalgo-ticks-parquet-v1", StringComparison.Ordinal),
                    "unsupported_input_schema", "Parquet input requires schema 'daxalgo-ticks-parquet-v1'.");
                Require(!string.IsNullOrWhiteSpace(input.Path) && System.IO.Path.IsPathFullyQualified(input.Path),
                    "invalid_input_path", "Parquet Path must be absolute.");
                Require(BacktestProtocolHash.IsSha256(input.Sha256), "invalid_input_hash",
                    "Parquet Sha256 must be a 64-character hexadecimal SHA-256.");
                Require(input.LengthBytes is > 0 and <= BacktestProtocolLimits.MaxInputBytes,
                    "invalid_input_length", "Parquet LengthBytes is outside the protocol limits.");
                Require(input.Synthetic is null, "invalid_parquet_reference",
                    "Parquet input must not carry synthetic settings.");
                break;

            default:
                throw new BacktestProtocolException("unsupported_input_kind", $"Input kind '{input.Kind}' is unsupported.");
        }
    }

    private static void ValidateLimits(BacktestResourceLimits limits)
    {
        Require(limits.MaxWallClockMilliseconds is > 0 and <= BacktestProtocolLimits.MaxWallClockMilliseconds,
            "invalid_wall_clock_limit", "MaxWallClockMilliseconds is outside the protocol limits.");
        Require(limits.MaxInputBytes is > 0 and <= BacktestProtocolLimits.MaxInputBytes,
            "invalid_input_limit", "MaxInputBytes is outside the protocol limits.");
        Require(limits.MaxArtifactBytes is > 0 and <= BacktestProtocolLimits.MaxArtifactBytes,
            "invalid_artifact_limit", "MaxArtifactBytes is outside the protocol limits.");
        Require(limits.MaxWorkingSetBytes is > 0 and <= BacktestProtocolLimits.MaxWorkingSetBytes,
            "invalid_memory_limit", "MaxWorkingSetBytes is outside the protocol limits.");
        Require(limits.MaxProgressMessages is >= 4 and <= BacktestProtocolLimits.MaxProgressMessages,
            "invalid_progress_limit", "MaxProgressMessages is outside the protocol limits.");
    }

    private static void RequireNonEmpty(string value, string code, int maxLength)
    {
        Require(!string.IsNullOrWhiteSpace(value), code, "The value is required.");
        Require(value.Length <= maxLength, code, $"The value may contain at most {maxLength} characters.");
    }

    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new BacktestProtocolException(code, message);
    }
}
