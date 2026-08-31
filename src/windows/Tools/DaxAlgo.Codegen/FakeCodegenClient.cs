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

    /// <param name="replies">Model replies to return in order (may include csharp fences — the
    /// orchestrator extracts them). Empty ⇒ a single always-compiles kernel.</param>
    public FakeCodegenClient(params string[] replies)
    {
        _replies = new Queue<string>(replies.Length > 0 ? replies : [DefaultKernel]);
    }

    /// <summary>A fake that answers with a unit of the requested kind. <c>--provider fake</c> uses this,
    /// so the offline path exercises the same session, compiler and ladder as a real provider — the only
    /// thing stubbed is the model.</summary>
    public static FakeCodegenClient ForKind(AuthoringKind kind) =>
        new(kind == AuthoringKind.Visualizer ? DefaultVisualizer : DefaultKernel);

    public string ProviderId => "fake";
    public string DisplayName => "Fake (deterministic)";
    public bool IsAvailable => true;

    /// <summary>How many times the loop asked this client to generate — the auto-fix retry count + 1.</summary>
    public int CallCount { get; private set; }

    /// <summary>Canned usage, so a test can assert the session sums tokens across generations.</summary>
    public CodegenUsage Usage { get; init; } = new(100, 50);

    /// <summary>The prompt the session actually built — what a test inspects to prove the thread was
    /// compacted and the current files were attached.</summary>
    public StrategyCodegenRequest? LastRequest { get; private set; }

    public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        CallCount++;
        LastRequest = request;
        if (_replies.Count > 0) _last = _replies.Dequeue();

        // A reply with no code is a question — same semantics as a real provider.
        var files = CodegenCodeExtractor.ExtractFiles(_last);
        return Task.FromResult(files.Count == 0
            ? StrategyCodegenResponse.Reply(_last, Usage)
            : StrategyCodegenResponse.Ok(files, _last, Usage));
    }

    /// <summary>
    /// A minimal always-compiling strategy: one class, no usings (they are ambient), declared parameters
    /// it actually reads, a target on its own book, and a picture.
    ///
    /// <para><b>It was an <c>IOrderRoutedStrategy</c> until 2026-09-01</b>, and its doc comment claimed
    /// it matched the output contract. That contract was archived; <c>AuthoredUnitVerifier</c> refuses a
    /// unit written against it before instantiating anything. Nothing referenced this constant, so
    /// nothing noticed — the CLI's <c>--provider fake</c> printed a message and used the scaffold rather
    /// than ever asking for it. Every rung it now has to clear is a rung the offline path checks for
    /// real.</para>
    /// </summary>
    public const string DefaultKernel = """
        ```csharp
        // file: GeneratedStrategy.cs
        public sealed class GeneratedStrategy : IStrategyKernel
        {
            public const string InstrumentParameter = "instrument";
            public const string PeriodParameter = "period";

            private const int HistoryCapacity = 240;
            private readonly List<double> _closes = new(HistoryCapacity);
            private readonly List<double> _averages = new(HistoryCapacity);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Instrument(InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
                StrategyParameter.Int(PeriodParameter, "Average period", 10, min: 2, max: 200, group: "Signal", unit: "bars"));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(context);
                _closes.Clear();
                _averages.Clear();
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(bar);
                ArgumentNullException.ThrowIfNull(context);

                var instrument = context.Parameters.GetInstrument(InstrumentParameter);
                var period = context.Parameters.GetInt(PeriodParameter);
                if (bar.InstrumentId != instrument) return Task.CompletedTask;

                if (_closes.Count == HistoryCapacity) _closes.RemoveAt(0);
                _closes.Add(bar.Close);

                var average = new Sma(period);
                foreach (var close in _closes) average.Update(close);

                if (_averages.Count == HistoryCapacity) _averages.RemoveAt(0);
                _averages.Add(average.IsReady ? average.Value : bar.Close);

                if (average.IsReady)
                    context.Book.SetTargetPosition(instrument, bar.Close > average.Value ? 1d : 0d);

                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                ArgumentNullException.ThrowIfNull(surface);

                using var panel = surface.Panel("Average", RenderPanelKind.Chart);
                if (_closes.Count == 0) { Plot.Waiting(surface, "Waiting for bars…"); return; }

                Series.Chart(surface, [
                    SeriesData.Line("Close", _closes.ToArray(), RenderThemeColor.Neutral),
                    SeriesData.Line("Average", _averages.ToArray(), RenderThemeColor.Accent),
                ]);
            }
        }
        ```
        """;

    /// <summary>The visualizer half of the same idea — a unit with no book, which is the one structural
    /// difference between the contracts and the one a model gets wrong.</summary>
    public const string DefaultVisualizer = """
        ```csharp
        // file: GeneratedVisualizer.cs
        public sealed class GeneratedVisualizer : IVisualizer
        {
            public const string InstrumentParameter = "instrument";
            public const string PeriodParameter = "period";

            private const int HistoryCapacity = 240;
            private readonly List<double> _mids = new(HistoryCapacity);
            private readonly List<double> _averages = new(HistoryCapacity);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Instrument(InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
                StrategyParameter.Int(PeriodParameter, "Average period", 10, min: 2, max: 200, group: "Display", unit: "quotes"));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

            public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(context);
                _mids.Clear();
                _averages.Clear();
                return Task.CompletedTask;
            }

            public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
            {
                ArgumentNullException.ThrowIfNull(context);

                var instrument = context.Parameters.GetInstrument(InstrumentParameter);
                var period = context.Parameters.GetInt(PeriodParameter);
                if (quote.InstrumentId != instrument) return Task.CompletedTask;

                var mid = (quote.Bid + quote.Ask) / 2d;
                if (mid <= 0d) return Task.CompletedTask;

                if (_mids.Count == HistoryCapacity) _mids.RemoveAt(0);
                _mids.Add(mid);

                var average = new Sma(period);
                foreach (var value in _mids) average.Update(value);

                if (_averages.Count == HistoryCapacity) _averages.RemoveAt(0);
                _averages.Add(average.IsReady ? average.Value : mid);

                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                ArgumentNullException.ThrowIfNull(surface);

                using var panel = surface.Panel("Mid", RenderPanelKind.Chart);
                if (_mids.Count == 0) { Plot.Waiting(surface, "Waiting for quotes…"); return; }

                Series.Chart(surface, [
                    SeriesData.Line("Mid", _mids.ToArray(), RenderThemeColor.Neutral),
                    SeriesData.Line("Average", _averages.ToArray(), RenderThemeColor.Accent),
                ]);
            }
        }
        ```
        """;
}
