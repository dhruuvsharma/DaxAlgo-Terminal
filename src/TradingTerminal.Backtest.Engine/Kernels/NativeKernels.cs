using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Kernels;

/// <summary>
/// The built-in native kernels the engine ships, as registry descriptors. Compose a
/// <see cref="StrategyKernelRegistry"/> from this (plus adapter-wrapped legacy strategies, added at
/// integration time) so the catalog, optimizer, and CLI discover every kernel uniformly.
/// </summary>
public static class NativeKernels
{
    public static IReadOnlyList<StrategyKernelDescriptor> All { get; } = new[]
    {
        MeanReversionKernel.Descriptor,
    };
}
