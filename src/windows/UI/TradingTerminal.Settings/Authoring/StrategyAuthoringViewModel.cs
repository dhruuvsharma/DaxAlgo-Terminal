using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// View-model for the AI Strategy Builder — a chat with a coding model about ONE strategy, plus the
/// files it writes and the compiler that judges them.
/// <list type="bullet">
///   <item><b>Chat</b> — a running <see cref="StrategyBuildSession"/>: the thread persists across turns,
///     so follow-ups ("tighten the stop"), the compiler's own errors, and the model's questions back to
///     the user all land in the same context. A reply with no code is a question, not a failure.</item>
///   <item><b>Code</b> — the files of the turn (a strategy is usually several), hand-editable; edits are
///     fed back into the next turn so the model patches what the user is actually looking at.</item>
///   <item><b>Compile</b> — the same <see cref="IStrategyCompiler"/> the manual path uses, so the policy
///     scan applies to model-written code: a strategy that P/Invokes never compiles, so it can never be
///     registered. Pressing Compile is the consent for running it.</item>
/// </list>
/// If the compiled class exposes a declarative <c>Schema</c>, its tunables render automatically in
/// <see cref="Parameters"/> via the shared auto-editor.
/// </summary>
public sealed partial class StrategyAuthoringViewModel : ViewModelBase, IDisposable
{
    /// <summary>Keeps the activity strip and the chat from growing without bound over a long session.</summary>
    private const int MaxActivityRows = 200;
    private const int MaxMessages = 400;

    private readonly IStrategyCompiler _compiler;
    private readonly IBacktestStrategyRegistry _registry;
    private readonly ILogger<StrategyAuthoringViewModel> _logger;
    private readonly IAiStrategyBuilder? _ai;
    private readonly AiCodegenOptions _options;
    private readonly AuthoredStrategyInstaller? _installer;

    private CancellationTokenSource? _generateCts;
    private StrategyBuildSession? _session;
    private bool _filesEditedByUser;

    /// <summary>Set once the constructor's own property assignments are done, so seeding the pickers
    /// doesn't write the user-config file back with the defaults it just read.</summary>
    private bool _ready;

    public StrategyAuthoringViewModel(
        IStrategyCompiler compiler,
        IBacktestStrategyRegistry registry,
        ILogger<StrategyAuthoringViewModel> logger,
        IAiStrategyBuilder? ai = null,
        IOptions<AiCodegenOptions>? options = null,
        AuthoredStrategyInstaller? installer = null)
    {
        _compiler = compiler;
        _registry = registry;
        _logger = logger;
        _ai = ai;
        _options = options?.Value ?? new AiCodegenOptions();
        _installer = installer;

        Diagnostics = [];
        Messages = [];
        Activity = [];
        Files = [];

        // Provider picker — every provider the app can build; unavailable ones show disabled so the user
        // sees "install Claude Code / add an API key". Null builder (AI not wired) ⇒ the chat pane hides.
        AiProviders = new ObservableCollection<AiProviderChoice>(
            (_ai?.Providers ?? []).Select(p => new AiProviderChoice(p)));
        SelectedAiProvider = AiProviders.FirstOrDefault(p =>
            _ai?.DefaultProvider is { } d && p.ProviderId == d.ProviderId)
            ?? AiProviders.FirstOrDefault(p => p.IsAvailable)
            ?? AiProviders.FirstOrDefault();

        SetFiles([new StrategyFile(StrategyFile.DefaultName, TemplateSource)]);
        _filesEditedByUser = false;
        _ready = true;
    }

    /// <summary>True when the AI builder is wired at all — drives the chat pane's visibility. When wired
    /// but nothing is usable, the pane shows setup guidance instead.</summary>
    public bool AiEnabled => _ai is not null;
    public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);

    [ObservableProperty] private string _strategyId = "myStrategy";
    [ObservableProperty] private string _displayName = "My custom strategy";

    [ObservableProperty] private string? _status = "Describe a strategy in the chat, or write one yourself, then press Compile & Register.";
    [ObservableProperty] private bool _compiledOk;

    /// <summary>Auto-generated editor for the compiled strategy's tunables, or null when it declares none
    /// / hasn't compiled yet.</summary>
    [ObservableProperty] private StrategyParametersViewModel? _parameters;

    /// <summary>Errors + warnings from the most recent compile, mapped to a UI-friendly shape.</summary>
    public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }

    /// <summary>Selecting a diagnostic jumps the Code tab to the file it points at.</summary>
    [ObservableProperty] private StrategyDiagnostic? _selectedDiagnostic;

    partial void OnSelectedDiagnosticChanged(StrategyDiagnostic? value)
    {
        if (value is null || string.IsNullOrEmpty(value.File)) return;
        var file = Files.FirstOrDefault(f => f.Name.Equals(value.File, StringComparison.OrdinalIgnoreCase));
        if (file is not null) SelectedFile = file;
    }

    // ── Files ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strategy's source files — what the model wrote, or what the user typed.</summary>
    public ObservableCollection<AuthoredFile> Files { get; }

    [ObservableProperty] private AuthoredFile? _selectedFile;

    [RelayCommand]
    private void AddFile()
    {
        var name = UniqueFileName("Helpers.cs");
        var file = Track(new AuthoredFile(name, string.Empty));
        Files.Add(file);
        SelectedFile = file;
        _filesEditedByUser = true;
    }

    [RelayCommand]
    private void RemoveFile(AuthoredFile? file)
    {
        if (file is null || Files.Count <= 1) return;
        file.PropertyChanged -= OnFileEdited;
        Files.Remove(file);
        SelectedFile = Files.FirstOrDefault();
        _filesEditedByUser = true;
    }

    // ── Providers & models ──────────────────────────────────────────────────────────────────────────

    /// <summary>The codegen providers offered in the picker (available and not).</summary>
    public ObservableCollection<AiProviderChoice> AiProviders { get; }

    [ObservableProperty] private AiProviderChoice? _selectedAiProvider;

    /// <summary>Models offered for the selected provider — the curated shortlist plus whatever the
    /// provider itself reports. The picker is editable, so an unlisted model id can just be typed.</summary>
    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _isRefreshingModels;

    /// <summary>How hard the model thinks before answering. "Provider default" sends no effort parameter
    /// at all, which is the only setting a model that predates the parameter will accept.</summary>
    public IReadOnlyList<CodegenEffort> Efforts { get; } =
        [CodegenEffort.Default, CodegenEffort.Low, CodegenEffort.Medium, CodegenEffort.High, CodegenEffort.XHigh, CodegenEffort.Max];

    [ObservableProperty] private CodegenEffort _selectedEffort = CodegenEffort.Default;

    /// <summary>False for a provider with no effort knob (Ollama, DeepSeek, the Codex CLI) — the picker
    /// disables rather than sending a parameter the provider would reject.</summary>
    public bool EffortSupported => SelectedAiProvider is { } choice && AiModelCatalog.SupportsEffort(choice.ProviderId);

    partial void OnSelectedAiProviderChanged(AiProviderChoice? value)
    {
        // A different provider is a different conversation — its context window holds none of this thread.
        ResetSession("Switched provider.");
        Models.Clear();
        OnPropertyChanged(nameof(EffortSupported));
        if (value is null) return;

        foreach (var model in _ai?.ModelsFor(value.ProviderId) ?? []) Models.Add(model);
        SelectedModel = Models.FirstOrDefault();
        SelectedEffort = value.Client.Effort;
    }

    partial void OnSelectedModelChanged(string? value)
    {
        ResetSession("Switched model.");
        Persist();
    }

    partial void OnSelectedEffortChanged(CodegenEffort value)
    {
        // Effort changes how the model reasons, so the thread it produced is no longer representative.
        ResetSession("Switched effort.");
        Persist();
    }

    private void Persist()
    {
        if (_ready && SelectedAiProvider is { } choice)
            PersistSelection(choice.ProviderId, SelectedModel, SelectedEffort);
    }

    /// <summary>Ask the provider what models this key/endpoint can actually call (OpenAI, Anthropic and
    /// Ollama all expose a models endpoint). Falls back silently to the curated list.</summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_ai is null || SelectedAiProvider is not { } choice || IsRefreshingModels) return;

        IsRefreshingModels = true;
        try
        {
            var client = ResolveClient(choice) ?? choice.Client;
            var live = await client.ListModelsAsync(CancellationToken.None);
            if (live.Count == 0)
            {
                AiStatus = $"{choice.DisplayName} didn't return a model list — type the model id instead.";
                return;
            }

            var previous = SelectedModel;
            Models.Clear();
            foreach (var model in live) Models.Add(model);
            SelectedModel = live.Contains(previous ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                ? previous
                : live[0];
            AiStatus = $"{live.Count} model(s) available from {choice.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing models failed for {Provider}", choice.ProviderId);
            AiStatus = $"Couldn't list models: {ex.Message}";
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The conversation the user reads — their turns, and the model's replies verbatim.</summary>
    public ObservableCollection<AuthoringMessage> Messages { get; }

    /// <summary>What the builder is doing right now ("Asking Claude…", "Compiling 3 file(s)…") — the
    /// live feedback that a long generation is actually progressing.</summary>
    public ObservableCollection<string> Activity { get; }

    /// <summary>The chat composer. Multi-line: Enter adds a newline, Ctrl+Enter sends.</summary>
    [ObservableProperty] private string _composer = string.Empty;

    [ObservableProperty] private string? _aiStatus;
    [ObservableProperty] private bool _isGenerating;

    /// <summary>"1m 20s elapsed…" while a turn runs. A detailed brief at a high effort is a multi-minute
    /// request; without a clock ticking, a working generation is indistinguishable from a hang.</summary>
    [ObservableProperty] private string? _elapsedText;

    /// <summary>The model asked a question instead of writing code, and is waiting for the answer. It is
    /// a normal turn — the strategy is under-specified and it wants to know, rather than guess.</summary>
    [ObservableProperty] private bool _awaitingAnswer;

    /// <summary>The assistant bubble currently being streamed into, or null between turns.</summary>
    private AuthoringMessage? _streamingReply;

    [ObservableProperty] private int _inputTokens;
    [ObservableProperty] private int _outputTokens;

    /// <summary>Tokens billed this session — blank when the provider doesn't report them (agent CLIs).</summary>
    public string UsageText => InputTokens + OutputTokens == 0
        ? "tokens: not reported"
        : $"tokens: {InputTokens:N0} in · {OutputTokens:N0} out";

    partial void OnInputTokensChanged(int value) => OnPropertyChanged(nameof(UsageText));
    partial void OnOutputTokensChanged(int value) => OnPropertyChanged(nameof(UsageText));

    partial void OnIsGeneratingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnComposerChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private bool CanSend => !IsGenerating && !string.IsNullOrWhiteSpace(Composer);

    /// <summary>
    /// One turn: send what the user typed (plus their hand-edits, if any), let the session generate →
    /// compile → auto-fix, and land the result in the chat, the file list and the diagnostics. It does
    /// NOT register — the user reviews the code and presses Compile & Register, which is the consent for
    /// running model-authored code (it's already scan-gated, so a strategy that P/Invokes never compiles).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_ai is null || SelectedAiProvider is not { } choice) return;
        if (!choice.IsAvailable)
        {
            AiStatus = $"{choice.DisplayName} isn't set up — install it, or add an API key in Settings → AI providers.";
            return;
        }
        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            AiStatus = "Give the strategy an id first.";
            return;
        }

        var prompt = Composer.Trim();
        if (prompt.Length == 0) return;

        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        Activity.Clear();
        Diagnostics.Clear();
        CompiledOk = false;
        AwaitingAnswer = false;
        IsGenerating = true;

        _generateCts?.Cancel();
        _generateCts?.Dispose();
        _generateCts = new CancellationTokenSource();

        var ticking = TickElapsedAsync(_generateCts.Token);
        var session = EnsureSession(choice);
        var tokensBefore = session.TotalUsage;
        _streamingReply = null;

        try
        {
            var turn = await session.SendAsync(
                WithUserEdits(prompt),
                new Progress<string>(step => PushActivity(step)),
                _generateCts.Token,
                new Progress<CodegenEvent>(evt => OnStreamed(evt, tokensBefore)));

            // The session's running total is authoritative: a turn can be several generations (the
            // auto-fix retries), and the streamed updates are per-generation.
            InputTokens = session.TotalUsage.InputTokens;
            OutputTokens = session.TotalUsage.OutputTokens;

            if (turn.Kind == BuildTurnKind.ProviderError)
            {
                AiStatus = $"{choice.DisplayName} failed: {turn.Error}";
                Append(AuthoringMessage.System(AiStatus));
                return;
            }

            // The reply was streamed into a bubble as it arrived; settle it on the final text (the
            // provider's own assembled version). Nothing streamed ⇒ the provider doesn't stream, so the
            // bubble appears now, whole.
            if (_streamingReply is null) Append(new AuthoringMessage(CodegenRole.Assistant, turn.AssistantText));
            else _streamingReply.Text = turn.AssistantText;

            AwaitingAnswer = turn.Kind == BuildTurnKind.Question;

            if (turn.Files.Count > 0)
            {
                SetFiles(turn.Files);
                _filesEditedByUser = false;
            }

            foreach (var diagnostic in turn.Compile?.Diagnostics ?? [])
                Diagnostics.Add(diagnostic);

            AiStatus = turn.Kind switch
            {
                BuildTurnKind.Question =>
                    "The model asked you something — answer in the chat.",
                BuildTurnKind.Compiled =>
                    $"Wrote {turn.Files.Count} file(s) and compiled cleanly in {turn.Generations} generation(s). " +
                    "Review the Code tab, then press Compile & Register.",
                _ =>
                    $"Still {turn.Compile?.Errors.Count() ?? 0} error(s) after {turn.Generations} generation(s) — " +
                    "they're in the Diagnostics list. Ask for a fix, or edit the code yourself.",
            };

            _logger.LogInformation(
                "AI builder turn for {Id} via {Provider}/{Model}: {Kind}, {Files} file(s), {Generations} generation(s)",
                StrategyId, choice.ProviderId, SelectedModel ?? "(default)", turn.Kind, turn.Files.Count, turn.Generations);
        }
        catch (OperationCanceledException)
        {
            AiStatus = "Stopped.";
            PushActivity("Stopped by the user.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI builder turn threw for {Id}", StrategyId);
            AiStatus = $"Generation error: {ex.Message}";
            Append(AuthoringMessage.System(AiStatus));
        }
        finally
        {
            IsGenerating = false;
            _streamingReply = null;
            _generateCts?.Cancel();   // stops the elapsed ticker
            await ticking;
            ElapsedText = null;
        }
    }

    /// <summary>
    /// One streamed event, on the UI context (<see cref="Progress{T}"/> marshals it). Text grows the
    /// assistant's bubble as it is written — this is the whole point of streaming, and the difference
    /// between watching a strategy get written and staring at a spinner for four minutes.
    /// </summary>
    private void OnStreamed(CodegenEvent evt, CodegenUsage tokensBefore)
    {
        switch (evt)
        {
            case CodegenEvent.TextDelta delta:
                if (_streamingReply is null)
                {
                    _streamingReply = new AuthoringMessage(CodegenRole.Assistant, delta.Text);
                    Append(_streamingReply);
                }
                else
                {
                    _streamingReply.Text += delta.Text;
                }
                break;

            case CodegenEvent.UsageUpdate update:
                // The update is absolute for the CURRENT generation, so add it to what the session had
                // banked before this turn. The exact total is set from the session when the turn ends.
                InputTokens = tokensBefore.InputTokens + update.Usage.InputTokens;
                OutputTokens = tokensBefore.OutputTokens + update.Usage.OutputTokens;
                break;
        }
    }

    /// <summary>Ticks the elapsed clock on the UI context until the turn ends or the user stops it.</summary>
    private async Task TickElapsedAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var elapsed = DateTime.UtcNow - started;
                ElapsedText = elapsed.TotalSeconds < 60
                    ? $"{elapsed.TotalSeconds:0}s elapsed…"
                    : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s elapsed — a detailed brief at a high effort takes minutes.";
            }
        }
        catch (OperationCanceledException)
        {
            // The turn finished (or was stopped) — nothing to report.
        }
    }

    private bool CanStop => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _generateCts?.Cancel();

    /// <summary>Start over: a fresh thread with the model, the starter template back in the editor.</summary>
    [RelayCommand]
    private void NewChat()
    {
        ResetSession(null);
        Messages.Clear();
        Activity.Clear();
        Diagnostics.Clear();
        InputTokens = OutputTokens = 0;
        CompiledOk = false;
        Parameters = null;
        SetFiles([new StrategyFile(StrategyFile.DefaultName, TemplateSource)]);
        _filesEditedByUser = false;
        AiStatus = null;
        Status = "New conversation. Describe the strategy you want.";
    }

    // ── Compile & register ──────────────────────────────────────────────────────────────────────────

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

        var script = CurrentScript();
        StrategyCompileResult result;
        try
        {
            result = _compiler.Compile(script);
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

        CompiledOk = true;
        if (result.Option.HasParameters)
            Parameters = StrategyParametersViewModel.FromSchema(result.Option.Schema);

        // The installer is what makes this a real strategy: the backtest registry, a card in the
        // Strategies catalog (when the author wrote a descriptor + live VM + view), and a plugin written
        // to the plugins folder so it is still there after a restart. Without it (Basic/Intermediate, or
        // a test), fall back to the backtest registry alone.
        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        var caveat = warnings > 0 ? $" {warnings} capability warning(s) below." : string.Empty;

        if (_installer is null)
        {
            _registry.Register(result.Option);
            Status = $"Compiled & registered '{result.Option.DisplayName}' from {Files.Count} file(s) — DEV (unsigned).{caveat}";
            return;
        }

        var install = _installer.Install(script, result);
        Status = install.Message + caveat;
        _logger.LogInformation(
            "Authored strategy {Id} installed from {Files} file(s): catalog={InCatalog}",
            result.Option.Id, Files.Count, install.InCatalog);
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────

    private StrategyScript CurrentScript() => new(
        StrategyId.Trim(),
        DisplayName.Trim(),
        [.. Files.Select(f => new StrategyFile(f.Name, f.Content))]);

    private StrategyBuildSession EnsureSession(AiProviderChoice choice)
    {
        if (_session is not null) return _session;

        var client = ResolveClient(choice) ?? choice.Client;
        _session = _ai!.StartSession(client, StrategyId.Trim(), DisplayName.Trim());
        return _session;
    }

    /// <summary>The selected provider bound to the selected model + effort (the factory rebuilds the
    /// client — a client is immutable in both).</summary>
    private IStrategyCodegenClient? ResolveClient(AiProviderChoice choice) =>
        _ai?.WithSettings(choice.ProviderId, SelectedModel, SelectedEffort);

    private void ResetSession(string? note)
    {
        if (_session is null) return;
        _session = null;
        if (note is not null && Messages.Count > 0)
            Append(AuthoringMessage.System($"{note} The model won't remember what was said above."));
    }

    /// <summary>When the user has hand-edited the code, the model must patch what they're actually
    /// looking at — not the version it last emitted. Ship the current files with the turn.</summary>
    private string WithUserEdits(string prompt)
    {
        if (!_filesEditedByUser || Files.Count == 0) return prompt;

        var sb = new StringBuilder(prompt);
        sb.AppendLine().AppendLine().AppendLine("I edited the code. This is its CURRENT state:");
        foreach (var file in Files)
        {
            sb.AppendLine().AppendLine($"```csharp");
            sb.AppendLine($"// file: {file.Name}");
            sb.AppendLine(file.Content.TrimEnd());
            sb.AppendLine("```");
        }

        _filesEditedByUser = false;
        return sb.ToString();
    }

    private void SetFiles(IReadOnlyList<StrategyFile> files)
    {
        foreach (var existing in Files) existing.PropertyChanged -= OnFileEdited;
        Files.Clear();

        foreach (var file in files)
            Files.Add(Track(new AuthoredFile(file.Name, file.Content)));

        SelectedFile = Files.FirstOrDefault();
        _session?.SyncEditedFiles(files);
    }

    private AuthoredFile Track(AuthoredFile file)
    {
        file.PropertyChanged += OnFileEdited;
        return file;
    }

    private void OnFileEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AuthoredFile.Content) or nameof(AuthoredFile.Name))
            _filesEditedByUser = true;
    }

    private string UniqueFileName(string preferred)
    {
        if (Files.All(f => !f.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))) return preferred;

        var stem = preferred.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? preferred[..^3] : preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}{i}.cs";
            if (Files.All(f => !f.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private void Append(AuthoringMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
    }

    private void PushActivity(string step)
    {
        Activity.Add(step);
        while (Activity.Count > MaxActivityRows) Activity.RemoveAt(0);
    }

    private void PersistSelection(string providerId, string? model, CodegenEffort effort)
    {
        try
        {
            AiCodegenUserFile.SaveSelection(providerId, model, effort, _options);
        }
        catch (Exception ex)
        {
            // A read-only profile shouldn't break the builder — the choice just won't survive a restart.
            _logger.LogWarning(ex, "Could not persist the AI provider/model choice");
        }
    }

    public void Dispose()
    {
        _generateCts?.Cancel();
        _generateCts?.Dispose();
        _generateCts = null;
        foreach (var file in Files) file.PropertyChanged -= OnFileEdited;
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
        // Helpers may live in additional files (the + button on the file list).

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

/// <summary>One source file in the builder's Code tab — editable, and observed so a hand-edit is fed
/// back to the model on the next turn.</summary>
public sealed partial class AuthoredFile(string name, string content) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _content = content;
}

/// <summary>One bubble in the chat: what the user asked, what the model answered, or a note from the
/// builder itself (a provider failure, a switched provider).</summary>
public sealed partial class AuthoringMessage : ObservableObject
{
    public AuthoringMessage(CodegenRole role, string text)
    {
        Role = role;
        _text = text;
    }

    private AuthoringMessage(string text)
    {
        Role = CodegenRole.Assistant;
        IsSystem = true;
        _text = text;
    }

    /// <summary>A builder-generated note, styled apart from the model's own words.</summary>
    public static AuthoringMessage System(string? text) => new(text ?? string.Empty);

    public CodegenRole Role { get; }
    public bool IsSystem { get; }
    public bool IsUser => !IsSystem && Role == CodegenRole.User;
    public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;

    /// <summary>Observable so Phase 3's streaming can grow the bubble token by token.</summary>
    [ObservableProperty] private string _text;

    public DateTime TimestampLocal { get; } = DateTime.Now;
}

/// <summary>One row in the AI provider picker — wraps a codegen client with display + availability for
/// binding, so an unavailable provider shows disabled with a hint rather than vanishing.</summary>
public sealed class AiProviderChoice(IStrategyCodegenClient client)
{
    public IStrategyCodegenClient Client { get; } = client;
    public string ProviderId => Client.ProviderId;
    public string DisplayName => Client.DisplayName;
    public bool IsAvailable => Client.IsAvailable;
    public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
}
