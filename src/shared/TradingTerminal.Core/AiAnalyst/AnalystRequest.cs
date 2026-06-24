namespace TradingTerminal.Core.AiAnalyst;

/// <summary>
/// Payload sent to the Python sidecar's <c>/analyst/run</c> endpoint. Keeps provider /
/// model choice on the request so the same client can switch between OpenAI, Anthropic,
/// Qwen, or MiniMax per call without re-DI'ing anything.
/// </summary>
public sealed record AnalystRequest(
    string Symbol,
    string Timeframe,
    int BarCount,
    string Provider,
    string Model,
    string VisionModel,
    IReadOnlyList<AnalystBar> Bars);
