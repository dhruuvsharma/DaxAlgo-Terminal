namespace TradingTerminal.UI.Presets;

/// <summary>
/// A named snapshot of a strategy window's view options, persisted per user by
/// <see cref="ToolPresetStore{T}"/> under <c>tool-presets\strategy-{strategyId}.json</c>.
/// The base fields cover the shared chart-axis controls every
/// <c>LiveSignalStrategyViewModelBase</c> window has; <see cref="Extras"/> is a free-form
/// string bag for window-specific display toggles (each window owns its keys via the
/// <c>CaptureExtraPreset</c> / <c>ApplyExtraPreset</c> overrides), so the DTO never needs a
/// per-strategy schema and old preset files keep deserializing as windows evolve.
/// The instrument is deliberately not part of a preset.
/// </summary>
public sealed record StrategyViewPreset(
    int ChartBarsShown,
    bool YAutoScale,
    double YAxisMin,
    double YAxisMax,
    Dictionary<string, string>? Extras);
