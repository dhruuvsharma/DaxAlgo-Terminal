using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Deterministic codegen client for tests and the CLI's <c>--provider fake</c> (CI has no keys and must
/// not call a network). It replies from a queue of canned answers, so a test can script "returns broken
/// code, then good code" to exercise the auto-fix loop; once the queue drains it repeats the last reply.
/// It also counts calls, so a test can assert the loop stopped early on success.
/// </summary>
public sealed class FakeCodegenClient : IStrategyCodegenClient
{
    private readonly Queue<string> _replies;
    private string _last = string.Empty;

    /// <param name="replies">Model replies to return in order (may include ```csharp fences — the
    /// orchestrator extracts them). Empty ⇒ a single always-compiles EMA kernel.</param>
    public FakeCodegenClient(params string[] replies)
    {
        _replies = new Queue<string>(replies.Length > 0 ? replies : [DefaultKernel]);
    }

    public string ProviderId => "fake";
    public string DisplayName => "Fake (deterministic)";
    public bool IsAvailable => true;

    /// <summary>How many times the loop asked this client to generate — the auto-fix retry count + 1.</summary>
    public int CallCount { get; private set; }

    /// <summary>Canned usage, so a test can assert the session sums tokens across generations.</summary>
    public CodegenUsage Usage { get; init; } = new(100, 50);

    public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        CallCount++;
        if (_replies.Count > 0) _last = _replies.Dequeue();

        // A reply with no code is a question — same semantics as a real provider.
        var files = CodegenCodeExtractor.ExtractFiles(_last);
        return Task.FromResult(files.Count == 0
            ? StrategyCodegenResponse.Reply(_last, Usage)
            : StrategyCodegenResponse.Ok(files, _last, Usage));
    }

    /// <summary>A minimal always-compiling kernel that matches output contract (a): single class, no
    /// namespace/usings (ambient), flattens at end.</summary>
    public const string DefaultKernel = """
        ```csharp
        public sealed class GeneratedStrategy(Contract contract) : IBacktestStrategy
        {
            private readonly Contract _contract = contract;
            private int _ticks;
            private int _seq;

            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

            public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
            {
                var mid = (tick.Bid + tick.Ask) / 2.0;
                if (mid <= 0 || ++_ticks != 10) return;
                await router.PlaceOrderAsync(new OrderRequest(
                    ClientOrderId: $"gen-{clock.UtcNow:HHmmssfff}-{_seq++}",
                    Contract: _contract, Side: OrderSide.Buy, Type: OrderType.Market, Quantity: 1), ct);
            }

            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        }
        ```
        """;
}
