using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// View-model for the Strategy Authoring pane — the heart of "build a custom strategy
/// without limitation". The user writes C# implementing <c>IBacktestStrategy</c>, presses
/// Compile, and on success the strategy is compiled in-memory (<see cref="IStrategyCompiler"/>)
/// and registered into the live <see cref="IBacktestStrategyRegistry"/> — no project, no
/// recompile of the host. From there it shows up in the Backtest tab like any built-in.
///
/// If the authored class exposes a declarative <c>Schema</c>, its tunables render
/// automatically in <see cref="Parameters"/> via the shared auto-editor — so a custom
/// strategy gets a custom parameter UI for free.
/// </summary>
public sealed partial class StrategyAuthoringViewModel : ViewModelBase
{
    private readonly IStrategyCompiler _compiler;
    private readonly IBacktestStrategyRegistry _registry;
    private readonly ILogger<StrategyAuthoringViewModel> _logger;

    public StrategyAuthoringViewModel(
        IStrategyCompiler compiler,
        IBacktestStrategyRegistry registry,
        ILogger<StrategyAuthoringViewModel> logger)
    {
        _compiler = compiler;
        _registry = registry;
        _logger = logger;
        Diagnostics = new ObservableCollection<StrategyDiagnostic>();
    }

    [ObservableProperty] private string _strategyId = "myStrategy";
    [ObservableProperty] private string _displayName = "My custom strategy";
    [ObservableProperty] private string _sourceCode = TemplateSource;

    [ObservableProperty] private string? _status = "Write a strategy and press Compile.";
    [ObservableProperty] private bool _compiledOk;

    /// <summary>Auto-generated editor for the compiled strategy's tunables, or null when it
    /// declares none / hasn't compiled yet.</summary>
    [ObservableProperty] private StrategyParametersViewModel? _parameters;

    /// <summary>Errors + warnings from the most recent compile, mapped to a UI-friendly shape.</summary>
    public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }

    [RelayCommand]
    private void Compile()
    {
        Diagnostics.Clear();
        CompiledOk = false;
        Parameters = null;

        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            Status = "Give the strategy an id before compiling.";
            return;
        }

        StrategyCompileResult result;
        try
        {
            result = _compiler.Compile(new StrategyScript(StrategyId.Trim(), DisplayName.Trim(), SourceCode));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy compile threw for {Id}", StrategyId);
            Status = $"Compiler error: {ex.Message}";
            return;
        }

        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add(diagnostic);

        if (!result.Success || result.Option is null)
        {
            Status = $"Compile failed — {result.Errors.Count()} error(s).";
            return;
        }

        _registry.Register(result.Option);
        CompiledOk = true;
        if (result.Option.HasParameters)
            Parameters = StrategyParametersViewModel.FromSchema(result.Option.Schema);

        Status = $"Compiled & registered '{result.Option.DisplayName}'. " +
                 "It's now available in the Backtest tab.";
        _logger.LogInformation("Authored strategy {Id} compiled and registered", result.Option.Id);
    }

    /// <summary>Starter strategy shown in the editor — a complete, compiling skeleton with a
    /// declarative parameter schema so the auto-editor lights up on first compile.</summary>
    private const string TemplateSource = """
        // Authored strategy. The following namespaces are imported for you:
        //   System, System.Collections.Generic, System.Linq, System.Threading(.Tasks),
        //   TradingTerminal.Core.Domain / Trading / Time / Backtest / MarketData,
        //   TradingTerminal.Core.Strategies.Parameters
        //
        // Rules: define exactly ONE public class implementing IBacktestStrategy with a
        // public (Contract) constructor. Optionally add a static Schema and a static
        // Create(Contract, StrategyParameters) to expose tunable parameters in the UI.

        public sealed class MyStrategy : IBacktestStrategy
        {
            public static StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
                StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1));

            public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
                new MyStrategy(contract, p.GetInt("lookback"), p.GetDouble("threshold"));

            private readonly Contract _contract;
            private readonly int _lookback;
            private readonly double _threshold;

            public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }

            public MyStrategy(Contract contract, int lookback, double threshold)
            {
                _contract = contract;
                _lookback = lookback;
                _threshold = threshold;
            }

            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;

            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
            {
                // Your signal logic here. Submit orders via
                // router.PlaceOrderAsync(new OrderRequest(...)). _contract names the instrument.
                if (_lookback <= 0 || _threshold <= 0 || _contract is null) return Task.CompletedTask;
                return Task.CompletedTask;
            }

            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;
        }
        """;
}
