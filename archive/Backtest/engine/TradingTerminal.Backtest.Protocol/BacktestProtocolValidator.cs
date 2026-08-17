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
        RequireLowerSha256(
            request.ExpectedHostEngineAssemblySha256,
            "invalid_host_engine_hash");
        Require(string.Equals(request.EngineVersion, BacktestProtocolVersions.ManagedEngine, StringComparison.Ordinal),
            "unsupported_engine_version",
            $"Engine version '{request.EngineVersion}' is unsupported; expected '{BacktestProtocolVersions.ManagedEngine}'.");
        Require(string.Equals(request.SdkVersion, BacktestProtocolVersions.Sdk, StringComparison.Ordinal),
            "unsupported_sdk_version",
            $"SDK version '{request.SdkVersion}' is unsupported; expected '{BacktestProtocolVersions.Sdk}'.");
        Require(string.Equals(request.StrategyContractVersion, BacktestProtocolVersions.StrategyContract, StringComparison.Ordinal),
            "unsupported_strategy_contract_version",
            $"Strategy contract version '{request.StrategyContractVersion}' is unsupported; expected '{BacktestProtocolVersions.StrategyContract}'.");
        RequireNonEmpty(strategy.Id, "invalid_strategy_id", 128);
        Require(strategy.ContractVersion == request.StrategyContractVersion, "strategy_contract_mismatch",
            "The strategy reference and request declare different contract versions.");
        if (strategy.ExpectedAssemblySha256 is not null)
            Require(BacktestProtocolHash.IsSha256(strategy.ExpectedAssemblySha256), "invalid_strategy_hash",
                "ExpectedAssemblySha256 must be a 64-character hexadecimal SHA-256.");

        var activationParameters = strategy.ActivationParameters
                                   ?? throw new BacktestProtocolException(
                                       "missing_activation_parameters",
                                       "Strategy.ActivationParameters is required.");
        switch (strategy.Source)
        {
            case BacktestStrategySource.Native:
                Require(strategy.InstalledBundle is null, "unexpected_bundle_reference",
                    "Native strategies cannot carry an installed-bundle reference.");
                Require(activationParameters.Count == 0, "unexpected_activation_parameters",
                    "Native strategies use Run.Parameters and cannot carry factory activation parameters.");
                break;

            case BacktestStrategySource.InstalledBundle:
                ValidateInstalledBundle(strategy, activationParameters);
                Require(run.ParametersOrEmpty.Values.Count == 0, "ambiguous_bundle_parameters",
                    "Installed bundles use Strategy.ActivationParameters; Run.Parameters must be empty.");
                break;

            default:
                throw new BacktestProtocolException(
                    "unsupported_strategy_source",
                    $"Strategy source '{strategy.Source}' is unsupported.");
        }

        Require(!string.IsNullOrWhiteSpace(run.StrategyId), "missing_run_strategy",
            "Run.StrategyId is required for worker dispatch.");
        Require(string.Equals(
                run.StrategyId,
                strategy.Id,
                strategy.Source == BacktestStrategySource.Native
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal),
            "strategy_id_mismatch", "Run.StrategyId and Strategy.Id must match.");
        Require(BacktestProtocolHash.IsSha256(request.ParametersSha256), "invalid_parameters_hash",
            "ParametersSha256 must be a 64-character hexadecimal SHA-256.");
        var expectedParametersHash = strategy.Source == BacktestStrategySource.Native
            ? BacktestProtocolHash.ComputeParametersSha256(run.ParametersOrEmpty)
            : BacktestProtocolHash.ComputeActivationParametersSha256(activationParameters);
        Require(string.Equals(
                request.ParametersSha256,
                expectedParametersHash,
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

    private static void ValidateInstalledBundle(
        BacktestStrategyReference strategy,
        IReadOnlyList<BacktestStrategyParameter> parameters)
    {
        var bundle = strategy.InstalledBundle
                     ?? throw new BacktestProtocolException(
                         "missing_bundle_reference",
                         "Installed bundle strategies require Strategy.InstalledBundle.");
        RequirePortableIdentifier(strategy.Id, "invalid_strategy_id");
        Require(strategy.ExpectedAssemblySha256 is not null, "missing_strategy_hash",
            "Installed bundle strategies require ExpectedAssemblySha256.");
        RequirePortableIdentifier(bundle.PublisherId, "invalid_bundle_publisher");
        RequireNonEmpty(bundle.StrategyVersion, "invalid_bundle_version", 64);
        RequireLowerSha256(bundle.ContentRootSha256, "invalid_bundle_content_root");
        RequireLowerSha256(bundle.ArchiveSha256, "invalid_bundle_archive_hash");
        RequireLowerSha256(strategy.ExpectedAssemblySha256!, "invalid_strategy_hash");
        ValidateBundleTrustEvidence(bundle.TrustEvidence);

        Require(parameters.Count <= BacktestProtocolLimits.MaxStrategyParameters,
            "too_many_activation_parameters",
            $"At most {BacktestProtocolLimits.MaxStrategyParameters} activation parameters are allowed.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (parameter is null)
                throw new BacktestProtocolException(
                    "invalid_activation_parameter",
                    "Activation parameters cannot contain null entries.");
            RequireNonEmpty(parameter.Key, "invalid_activation_parameter_key",
                BacktestProtocolLimits.MaxStrategyParameterKeyCharacters);
            Require(keys.Add(parameter.Key), "duplicate_activation_parameter",
                $"Activation parameter '{parameter.Key}' is duplicated.");

            var hasInteger = parameter.IntegerValue is not null;
            var hasNumber = parameter.NumberValue is not null;
            var hasBoolean = parameter.BooleanValue is not null;
            var hasString = parameter.StringValue is not null;
            switch (parameter.Kind)
            {
                case BacktestStrategyParameterKind.Integer:
                    Require(hasInteger && !hasNumber && !hasBoolean && !hasString,
                        "invalid_activation_parameter_value",
                        $"Parameter '{parameter.Key}' must carry only IntegerValue.");
                    break;
                case BacktestStrategyParameterKind.Number:
                    Require(hasNumber && !hasInteger && !hasBoolean && !hasString &&
                            double.IsFinite(parameter.NumberValue!.Value),
                        "invalid_activation_parameter_value",
                        $"Parameter '{parameter.Key}' must carry only a finite NumberValue.");
                    break;
                case BacktestStrategyParameterKind.Boolean:
                    Require(hasBoolean && !hasInteger && !hasNumber && !hasString,
                        "invalid_activation_parameter_value",
                        $"Parameter '{parameter.Key}' must carry only BooleanValue.");
                    break;
                case BacktestStrategyParameterKind.Choice:
                case BacktestStrategyParameterKind.Text:
                    Require(hasString && !hasInteger && !hasNumber && !hasBoolean &&
                            parameter.StringValue!.Length <= BacktestProtocolLimits.MaxStrategyParameterStringCharacters,
                        "invalid_activation_parameter_value",
                        $"Parameter '{parameter.Key}' must carry only a bounded StringValue.");
                    break;
                default:
                    throw new BacktestProtocolException(
                        "unsupported_activation_parameter_kind",
                        $"Parameter '{parameter.Key}' has unsupported kind '{parameter.Kind}'.");
            }
        }
    }

    private static void ValidateBundleTrustEvidence(BacktestBundleTrustEvidence? evidence)
    {
        if (evidence is null)
            throw new BacktestProtocolException(
                "missing_bundle_trust_evidence",
                "Installed bundle strategies require accepted publisher trust evidence.");
        Require(
            string.Equals(
                evidence.SignatureAlgorithm,
                BacktestBundleTrustEvidence.PublisherSignatureAlgorithm,
                StringComparison.Ordinal),
            "unsupported_bundle_signature_algorithm",
            "The installed bundle uses an unsupported publisher signature algorithm.");
        switch (evidence.Kind)
        {
            case BacktestBundleTrustKind.UnsignedLocalDevelopment:
                Require(
                    evidence.PublisherKeyId is null &&
                    evidence.PublisherKeyFingerprintSha256 is null,
                    "invalid_local_bundle_trust_evidence",
                    "Unsigned local-development evidence cannot name a publisher key.");
                break;
            case BacktestBundleTrustKind.VerifiedPublisher:
                RequireNonEmpty(
                    evidence.PublisherKeyId ?? string.Empty,
                    "invalid_bundle_publisher_key",
                    200);
                RequireLowerSha256(
                    evidence.PublisherKeyFingerprintSha256!,
                    "invalid_bundle_publisher_key_fingerprint");
                break;
            default:
                throw new BacktestProtocolException(
                    "unsupported_bundle_trust_kind",
                    $"Bundle trust kind '{evidence.Kind}' is unsupported.");
        }
    }

    private static void RequirePortableIdentifier(string value, string code)
    {
        RequireNonEmpty(value, code, 128);
        Require(value.All(static character =>
                char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '_' or '-'),
            code,
            "The value must be a lowercase portable identifier.");
        Require(char.IsAsciiLetterOrDigit(value[0]),
            code,
            "The value must start with an ASCII letter or digit.");
    }

    private static void RequireLowerSha256(string value, string code) =>
        Require(
            BacktestProtocolHash.IsSha256(value) &&
            string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal),
            code,
            "The value must be a lowercase 64-character hexadecimal SHA-256.");

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
