using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Execution;
using RichParameters = TradingTerminal.Core.Strategies.Parameters.StrategyParameters;

namespace TradingTerminal.BacktestStudio;

/// <summary>Dispatches a catalog entry to its declared in-process execution route.</summary>
public sealed class BacktestStudioRunner
{
    internal const string SignalPolicyVersion = "studio-signal-v1";

    public Task<BacktestReport> RunAsync(
        StrategyCatalogDescriptor descriptor,
        RichParameters parameters,
        Func<IMarketDataFeed> feedFactory,
        RunSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(feedFactory);
        ArgumentNullException.ThrowIfNull(spec);

        return Task.Run(async () =>
        {
            var kernel = descriptor.CreateKernel(parameters);
            var feed = feedFactory();
            if (descriptor.ExecutionRoute == StrategyExecutionRoute.OrderNative)
                return await new BacktestEngine(feed).RunAsync(spec, kernel, cancellationToken).ConfigureAwait(false);

            var fault = SignalExecutionPolicy.TryCreate(
                SignalPolicyVersion,
                SignalExecutionPolicyOptions.ConservativeDefault,
                out var policy);
            if (fault != SignalExecutionFault.None || policy is null)
                throw new InvalidOperationException($"The Studio signal policy is invalid ({fault}).");

            return await new SignalBacktestRunner(feed).RunAsync(
                spec,
                kernel,
                descriptor.Id,
                policy,
                UnitDefinition.ConservativeDefault,
                ct: cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
