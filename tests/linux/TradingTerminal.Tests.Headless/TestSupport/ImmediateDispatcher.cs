using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Tests.TestSupport;

/// <summary>UI dispatcher stand-in for tests; runs everything inline on the calling thread.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
