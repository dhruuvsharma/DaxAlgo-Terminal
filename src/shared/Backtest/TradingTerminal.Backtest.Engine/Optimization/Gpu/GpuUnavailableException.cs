namespace TradingTerminal.Backtest.Engine.Optimization.Gpu;

/// <summary>Thrown when the GPU optimizer can't run a job — binary missing, spec unsupported, or the
/// subprocess failed. Callers catch this and fall back to the CPU optimizer.</summary>
public sealed class GpuUnavailableException : Exception
{
    public GpuUnavailableException(string message) : base(message) { }
    public GpuUnavailableException(string message, Exception inner) : base(message, inner) { }
}
