using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Research;

/// <summary>
/// One timestamped output of a reproduction, mapped onto canonical identity: an
/// <see cref="InstrumentId"/>-keyed signal / weight / prediction at a UTC time. Replayed through the
/// <c>ReproducedSignalStrategyKernel</c> into the backtest engine — there is no live order path
/// (data/signals only).
///
/// <para>Provenance (paper id + repo commit + env hash) rides on every signal, mirroring the
/// canonical market-data discipline where <c>Quote</c>/<c>TradePrint</c>/<c>OhlcvBar</c> always carry
/// their source — never strip it.</para>
/// </summary>
public sealed record ReproducedSignal(
    InstrumentId Instrument,
    DateTime EventTimeUtc,
    double Value,
    string PaperArxivId,
    string RepoCommit,
    EnvHash EnvHash,
    ReproSignalKind Kind = ReproSignalKind.Position);
