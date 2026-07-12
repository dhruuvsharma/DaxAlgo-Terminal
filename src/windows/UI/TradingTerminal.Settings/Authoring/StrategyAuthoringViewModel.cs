using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
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
    private readonly IAiStrategyBuilder? _ai;
    private CancellationTokenSource? _generateCts;

    public StrategyAuthoringViewModel(
        IStrategyCompiler compiler,
        IBacktestStrategyRegistry registry,
        ILogger<StrategyAuthoringViewModel> logger,
        IAiStrategyBuilder? ai = null)
    {
        _compiler = compiler;
        _registry = registry;
        _logger = logger;
        _ai = ai;
        Diagnostics = new ObservableCollection<StrategyDiagnostic>();

        // Provider picker — every provider the app can build; unavailable ones show disabled so the user
        // sees "install Claude Code / add an API key". Null builder (AI not wired) ⇒ the pane hides.
        AiProviders = new ObservableCollection<AiProviderChoice>(
            (_ai?.Providers ?? []).Select(p => new AiProviderChoice(p)));
        SelectedAiProvider = AiProviders.FirstOrDefault(p =>
            _ai?.DefaultProvider is { } d && p.Client.ProviderId == d.ProviderId)
            ?? AiProviders.FirstOrDefault(p => p.IsAvailable)
            ?? AiProviders.FirstOrDefault();
    }

    /// <summary>True when the AI builder is wired and at least one provider is usable — drives the pane's
    /// visibility. When wired but nothing is usable, the pane shows setup guidance instead.</summary>
    public bool AiEnabled => _ai is not null;
    public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);

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

    // ── AI builder ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The codegen providers offered in the picker (available and not).</summary>
    public ObservableCollection<AiProviderChoice> AiProviders { get; }

    [ObservableProperty] private AiProviderChoice? _selectedAiProvider;

    /// <summary>The user's plain-English request, e.g. "a Bollinger-band mean reversion on 20-bar bands".</summary>
    [ObservableProperty] private string _aiPrompt = string.Empty;

    [ObservableProperty] private string? _aiStatus;
    [ObservableProperty] private bool _isGenerating;

    // Keep the Generate button in step with generation state (the command's CanExecute drives IsEnabled).
    partial void OnIsGeneratingChanged(bool value) => GenerateWithAiCommand.NotifyCanExecuteChanged();

    private bool CanGenerate => !IsGenerating;

    /// <summary>Take an instruction to a compiling strategy: generate → compile → feed errors back →
    /// retry (bounded), then drop the result into the editor. It does NOT register — the user reviews the
    /// AI-written code and presses Compile to add it, which is the consent for running model-authored
    /// code (it's already scan-gated, so a strategy that P/Invokes never even compiles).</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateWithAiAsync()
    {
        if (_ai is null || SelectedAiProvider is not { } choice) return;
        if (!choice.IsAvailable) { AiStatus = $"{choice.DisplayName} isn't set up — install it or add an API key in Settings."; return; }
        if (string.IsNullOrWhiteSpace(AiPrompt)) { AiStatus = "Describe the strategy you want first."; return; }
        if (string.IsNullOrWhiteSpace(StrategyId)) { AiStatus = "Give the strategy an id first."; return; }

        IsGenerating = true;
        AiStatus = $"Asking {choice.DisplayName}…";
        Diagnostics.Clear();
        CompiledOk = false;
        _generateCts?.Cancel();
        _generateCts = new CancellationTokenSource();

        try
        {
            var result = await _ai.BuildAsync(choice.Client, AiPrompt.Trim(), StrategyId.Trim(), DisplayName.Trim(), _generateCts.Token);

            if (result.Code is not null) SourceCode = result.Code;
            foreach (var diagnostic in result.Compile?.Diagnostics ?? [])
                Diagnostics.Add(diagnostic);

            if (result.ProviderError is not null)
                AiStatus = $"{choice.DisplayName} failed: {result.ProviderError}";
            else if (result.Success)
                AiStatus = $"Generated & compiled in {result.Attempts} attempt(s). Review it, then press Compile to add it to the catalog (it'll be marked DEV / unsigned).";
            else
                AiStatus = $"Couldn't get compiling code in {result.Attempts} attempts — the errors are below. Edit it, or try again with more detail.";

            _logger.LogInformation("AI generate for {Id} via {Provider}: success={Success} attempts={Attempts}",
                StrategyId, choice.Client.ProviderId, result.Success, result.Attempts);
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Generation cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI generate threw for {Id}", StrategyId);
            AiStatus = $"Generation error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

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
            // A policy-scan Block comes back as an error diagnostic, so a strategy that reaches for
            // P/Invoke / Process / the registry fails here with a clear reason, just like a plugin.
            Status = $"Compile failed — {result.Errors.Count()} error(s).";
            return;
        }

        _registry.Register(result.Option);
        CompiledOk = true;
        if (result.Option.HasParameters)
            Parameters = StrategyParametersViewModel.FromSchema(result.Option.Schema);

        // An authored strategy is unsigned by definition — the user (or an AI) just wrote it. Say so.
        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        var caveat = warnings > 0 ? $" ({warnings} capability warning(s) below)" : string.Empty;
        Status = $"Compiled & registered '{result.Option.DisplayName}' — DEV (unsigned){caveat}. " +
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

/// <summary>One row in the AI provider picker — wraps a codegen client with display + availability for
/// binding, so an unavailable provider shows disabled with a hint rather than vanishing.</summary>
public sealed class AiProviderChoice(IStrategyCodegenClient client)
{
    public IStrategyCodegenClient Client { get; } = client;
    public string DisplayName => Client.DisplayName;
    public bool IsAvailable => Client.IsAvailable;
    public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
}
