using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DaxAlgo.Sdk;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
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
    private readonly IStrategyRegistry _registry;
    private readonly IAuthoredUnitSink? _sink;

    /// <summary>Kept across runs so the estimate accumulates — an agent's record is only worth having if
    /// it survives the session that produced it.</summary>
    /// <summary>What the router learned in previous sessions. Loaded rather than constructed fresh: an
    /// estimator that resets before it can warm up is a constant with extra steps, and reward-biased
    /// routing is only worth having if the weights come from evidence.</summary>
    private readonly AgentReliability _reliability = AgentMemory.Load();

    /// <summary>Per-turn cost and outcome, so the six-agent split can be argued about with numbers.
    /// Records identifiers and figures, never the brief, the reply or the code.</summary>
    private readonly TrajectoryLog _trajectory = new(TrajectoryPath);

    private static string TrajectoryPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "agent-runs.jsonl");
    private readonly ILogger<StrategyAuthoringViewModel> _logger;
    private readonly IAiStrategyBuilder? _ai;
    private readonly AiCodegenOptions _options;
    private readonly AuthoredStrategyInstaller? _installer;
    private readonly ICliWorkspaceLauncher? _cliLauncher;

    private CancellationTokenSource? _generateCts;
    private StrategyBuildSession? _session;
    private bool _filesEditedByUser;

    /// <summary>
    /// Where the multi-agent run stands, across user turns.
    ///
    /// <para>Both of these used to be built fresh inside every call, so an interview could never end:
    /// each turn re-entered with <c>new RoutingState()</c> (HasSpec false, so the prior returned
    /// Only(Interviewer)) and <c>new AgentContext(latest message)</c> (so the Interviewer was handed
    /// "approved, now start building" with nothing it referred to). The user's saved session shows the
    /// result exactly: six briefs, six interviews, no code, ever.</para>
    ///
    /// <para>Kept beside <see cref="_session"/> because they are the agent path's equivalent of it, and
    /// cleared everywhere it is.</para>
    /// </summary>
    private RoutingState _agentState = new();
    private AgentContext? _agentContext;

    /// <summary>The model thread restored from disk, handed to the next session so a resumed conversation
    /// still remembers what it wrote. Cleared once used.</summary>
    private IReadOnlyList<CodegenMessage>? _restoredThread;
    private CodegenUsage? _restoredUsage;

    /// <summary>True while a saved session is being loaded — suppresses the auto-save and the
    /// "switched provider" notes that the restore itself would otherwise trigger.</summary>
    private bool _restoring;

    /// <summary>Set once the constructor's own property assignments are done, so seeding the pickers
    /// doesn't write the user-config file back with the defaults it just read.</summary>
    private bool _ready;

    private readonly IAiProviderSettingsLauncher? _providerSettings;

    private readonly IAuthoredUnitStore? _units;

    public StrategyAuthoringViewModel(
        IStrategyCompiler compiler,
        IStrategyRegistry registry,
        ILogger<StrategyAuthoringViewModel> logger,
        IAiStrategyBuilder? ai = null,
        IOptions<AiCodegenOptions>? options = null,
        AuthoredStrategyInstaller? installer = null,
        ICliWorkspaceLauncher? cliLauncher = null,
        IAuthoredUnitSink? sink = null,
        IAiProviderSettingsLauncher? providerSettings = null,
        IAuthoredUnitStore? units = null)
    {
        _compiler = compiler;
        _registry = registry;
        _logger = logger;
        _ai = ai;
        _options = options?.Value ?? new AiCodegenOptions();
        _installer = installer;
        _cliLauncher = cliLauncher;
        // Optional: an edition that registers none simply has no Manage button in the provider footer.
        _providerSettings = providerSettings;
        // Optional: an edition whose units arrive as sealed server-compiled artifacts keeps no local
        // unit folder, and the composer says the unit will not survive a restart rather than pretending.
        _units = units;
        // Optional: an edition without one still compiles, verifies and previews — it just says it
        // cannot put the result in a catalog, rather than throwing at construction.
        _sink = sink;

        Diagnostics = [];
        Messages = [];
        Activity = [];
        Files = [];
        Tasks = [];

        // The hero empty state ↔ transcript switch watches the count; the VM owns the collection,
        // so the self-subscription cannot outlive it.
        Messages.CollectionChanged += OnMessagesCollectionChanged;

        // The status bar reads "Build failed" off the diagnostics, so it has to hear about them. Same
        // ownership argument as above: the VM owns the collection, so the handler cannot outlive it.
        Diagnostics.CollectionChanged += (_, _) => RefreshState();

        // Backing field, not the property — the change handler resets sessions and persists, neither of
        // which applies to seeding the ctor's own default from config.
        _buildEffort = StrategyBuildEfforts.Parse(_options.BuildEffort);

        // The unified picker's rows — built BEFORE the provider selection below, so the initial
        // provider/model choice can sync into it.
        AllModels = new ObservableCollection<AiModelChoice>(_ai?.AllModels() ?? []);

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

        // A strategy is several sittings' work. Bring back the last one the user was on, and offer the
        // rest in the picker — a chat that dies with the process is no use for anything serious.
        RefreshSavedSessions();
        if (SavedSessions.FirstOrDefault() is { } latest) Restore(latest);
    }

    /// <summary>True when the AI builder is wired at all — drives the chat pane's visibility. When wired
    /// but nothing is usable, the pane shows setup guidance instead.</summary>
    public bool AiEnabled => _ai is not null;
    public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);

    /// <summary>False until the first message lands — the canvas shows the hero empty state (brand
    /// mark, tagline, suggestion briefs) instead of an empty transcript.</summary>
    public bool HasConversation => Messages.Count > 0;

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasConversation));

        // The cost readout appears with the first turn and disappears when the session is cleared, so
        // it has to follow the transcript rather than only the token counts.
        RefreshUsage();
        RefreshState();
    }

    /// <summary>Canned first briefs for the empty state, seeded from strategy families the terminal
    /// already ships — one click puts a real, well-formed brief in the composer to edit or send.</summary>
    public IReadOnlyList<string> SuggestionBriefs { get; } =
    [
        "Fade liquidity sweeps at the prior day's low: enter when a stop-run through the level reverses within 3 bars on tape absorption, exit at VWAP with a stop below the sweep extreme.",
        "Momentum breakout on 5-minute bars: enter on a close above the last 20-bar high with a volume surge of at least 1.5× average, trail an ATR(14) stop.",
        "Cumulative-delta divergence reversal: when price prints a new session low but cumulative delta holds above its own low, fade the move with a fixed 1.5R target.",
    ];

    [RelayCommand]
    private void UseSuggestion(string? brief)
    {
        if (!string.IsNullOrWhiteSpace(brief)) Composer = brief;
    }

    /// <summary>Collapses the session rail to an icon strip — the workspace's only chrome toggle.</summary>
    [ObservableProperty] private bool _railCollapsed;

    [RelayCommand]
    private void ToggleRail() => RailCollapsed = !RailCollapsed;

    /// <summary>
    /// Whether the workbench panel is showing.
    ///
    /// <para>False on a fresh pane. It used to be a permanent 390px column, so an untouched session
    /// devoted a third of the window to an empty preview, an empty file list and an empty activity log
    /// while the middle invited you to type. It opens ITSELF the moment there is something in it -- see
    /// <see cref="OpenWorkbench"/> -- which is the behaviour that makes closing it by default safe:
    /// nobody has to know the panel exists to be shown their first compiled unit.</para>
    /// </summary>
    [ObservableProperty] private bool _isWorkbenchOpen;

    /// <summary>Shows the workbench because something worth seeing just landed in it. Never closes it:
    /// a panel that shut itself while the user was reading would be its own defect.</summary>
    private void OpenWorkbench() => IsWorkbenchOpen = true;

    /// <summary>Toggles the workbench from the keyboard (Ctrl+J), matching the rail's Ctrl+B. Both
    /// panels are chrome around a text box, and reaching for the mouse to get a bigger text box is
    /// exactly the friction the people this is for left other tools to avoid.</summary>
    [RelayCommand]
    private void ToggleWorkbench() => IsWorkbenchOpen = !IsWorkbenchOpen;

    /// <summary>The workbench's tabs, as indices. Named because three call sites and a XAML
    /// <c>SelectedIndex</c> have to agree on them, and they did not: the doc comment here still said
    /// "0 Code · 1 Parameters · 2 Activity" long after Parameters and Activity had been removed and
    /// Preview had taken slot 0, so <see cref="FocusFile"/> — whose entire job is to open the file a
    /// chat chip names — was sending every click to the PREVIEW tab.</summary>
    public const int WorkbenchTabPreview = 0;
    public const int WorkbenchTabCode = 1;
    public const int WorkbenchTabActivity = 2;

    /// <summary>Selected workbench tab; see the <c>WorkbenchTab*</c> constants.</summary>
    [ObservableProperty] private int _workbenchTab;

    [RelayCommand]
    private void FocusFile(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            SelectedFile = file;
            OpenWorkbench();
            WorkbenchTab = WorkbenchTabCode;
        }
    }

    [ObservableProperty] private string _strategyId = "myStrategy";
    [ObservableProperty] private string _displayName = "My custom strategy";

    private const string DefaultStrategyId = "myStrategy";
    private const string DefaultDisplayName = "My custom strategy";

    /// <summary>True once this strategy has been registered (this session, or per the saved snapshot) —
    /// drives the DRAFT/REGISTERED chip and the rail's status line.</summary>
    [ObservableProperty] private bool _isRegistered;

    /// <summary>
    /// What this session is building. Held here rather than inferred from the prompt so the choice
    /// survives a restart with the rest of the session, and so the builder can be told the target
    /// kind explicitly instead of guessing from wording.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuthoringVisualizer))]
    [NotifyPropertyChangedFor(nameof(AuthoringKindNotice))]
    private AuthoringKind _authoringKind = AuthoringKind.Strategy;

    /// <summary>Two-way binding surface for the Strategy/Visualizer switch.</summary>
    public bool IsAuthoringVisualizer
    {
        get => AuthoringKind == AuthoringKind.Visualizer;
        set => AuthoringKind = value ? AuthoringKind.Visualizer : AuthoringKind.Strategy;
    }

    /// <summary>
    /// What the chosen kind means, in one line. It used to say visualizer generation was not wired up,
    /// which was true and is no longer: the kind now shapes the system prompt and a unit that comes back
    /// as the wrong contract is sent round the fix loop like any other error.
    /// </summary>
    public string AuthoringKindNotice => AuthoringKind == AuthoringKind.Visualizer
        ? "Draws only — a visualizer has no virtual book and cannot take a position."
        : string.Empty;

    [ObservableProperty] private string? _status = "Describe a strategy in the chat, or write one yourself, then press Compile & Register.";
    [ObservableProperty] private bool _compiledOk;

    // ── Live preview ────────────────────────────────────────────────────────────────────────────
    // Reading generated C# is a poor way to review a strategy. Most people cannot, and the ones who can
    // still cannot tell from the source whether the axes are sensible or whether it drew anything at
    // all. The render contract exists so a picture can be produced without a window; this is that
    // pointed at the authoring pane, driven over the same series the verifier used — so what is on
    // screen and what the ladder judged cannot disagree.

    /// <summary>The compiled unit's own frame callback, handed straight to a render surface.</summary>
    [ObservableProperty] private Action<IRenderSurface>? _previewDraw;

    /// <summary>What is on screen, or why nothing is. Never left blank: an empty rectangle with no
    /// explanation reads as a broken application rather than a strategy that draws nothing.</summary>
    [ObservableProperty] private string _previewSummary = string.Empty;

    /// <summary>True once there is a frame to show.</summary>
    /// <summary>
    /// The questions the model just asked, as buttons. Empty when it asked in prose or asked nothing —
    /// both of which stay valid, and both of which fall back to typing an answer in the composer.
    /// </summary>
    public ObservableCollection<AuthoringQuestionViewModel> Questions { get; } = [];

    /// <summary>One-click replies, shown whenever the builder is waiting. Independent of
    /// <see cref="Questions"/>: a model that asks in prose still gets buttons.</summary>
    public ObservableCollection<AuthoringAction> Actions { get; } = [];

    [ObservableProperty] private bool _hasActions;

    private void SetActions(IReadOnlyList<AuthoringAction> actions)
    {
        Actions.Clear();
        foreach (var action in actions) Actions.Add(action);
        HasActions = Actions.Count > 0;
    }

    /// <summary>
    /// Sends a canned reply, or hands the composer back when the action carries none.
    ///
    /// <para>An empty reply deliberately does not send anything. "I want changes" means the user has
    /// something specific to say, and inventing a sentence for them would put words in their mouth and
    /// cost a turn on a provider where a turn is minutes.</para>
    /// </summary>
    [RelayCommand]
    private async Task ChooseAsync(AuthoringAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Reply))
        {
            SetActions([]);
            ComposerFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetActions([]);
        SetQuestions([]);
        Composer = action.Reply;
        await SendAsync();
    }

    /// <summary>Raised when an action wants the user to type rather than answering for them.</summary>
    public event EventHandler? ComposerFocusRequested;

    [ObservableProperty] private bool _hasQuestions;

    /// <summary>True once at least one question has an answer, so Submit cannot send an empty reply.</summary>
    public bool CanSubmitAnswers => HasQuestions && Questions.Any(q => q.IsAnswered);

    private IReadOnlyList<AuthoringQuestion> _askedQuestions = [];

    private void SetQuestions(IReadOnlyList<AuthoringQuestion> asked)
    {
        foreach (var existing in Questions) existing.PropertyChanged -= OnQuestionAnswered;
        Questions.Clear();

        _askedQuestions = asked;
        foreach (var question in asked)
        {
            var vm = new AuthoringQuestionViewModel(question);
            vm.PropertyChanged += OnQuestionAnswered;
            Questions.Add(vm);
        }

        HasQuestions = Questions.Count > 0;
        OnPropertyChanged(nameof(CanSubmitAnswers));
        SubmitAnswersCommand.NotifyCanExecuteChanged();
    }

    private void OnQuestionAnswered(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AuthoringQuestionViewModel.IsAnswered)
            or nameof(AuthoringQuestionViewModel.Answer))) return;

        OnPropertyChanged(nameof(CanSubmitAnswers));
        SubmitAnswersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Turns the picked options into the next user message and sends it.
    ///
    /// <para>The composed text goes through the ordinary send path, so the transcript reads as the user
    /// having answered in words — which is what the model sees on the next turn, and what someone
    /// reading the thread later needs in order to follow it.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSubmitAnswers))]
    private async Task SubmitAnswersAsync()
    {
        var answers = Questions
            .Where(q => q.IsAnswered)
            .ToDictionary(q => q.Model.Id, q => q.Answer, StringComparer.Ordinal);

        var composed = AuthoringQuestions.ComposeAnswer(_askedQuestions, answers);
        if (string.IsNullOrWhiteSpace(composed)) return;

        SetQuestions([]);
        SetActions([]);
        Composer = composed;
        await SendAsync();
    }

    [ObservableProperty] private bool _hasPreview;

    /// <summary>The panel arrangement the preview renders — the same tree the terminal
    /// will build for this unit.</summary>
    [ObservableProperty] private DaxAlgo.Sdk.Layout.UnitLayout _previewLayout =
        DaxAlgo.Sdk.Layout.UnitLayout.Single;

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
        if (value is null)
        {
            SyncModelChoice();
            return;
        }

        foreach (var model in _ai?.ModelsFor(value.ProviderId) ?? []) Models.Add(model);
        SelectedModel = Models.FirstOrDefault();
        SelectedEffort = value.Client.Effort;
        SyncModelChoice();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        ResetSession("Switched model.");
        Persist();
        SyncModelChoice();
        OnPropertyChanged(nameof(ModelPillText));
    }

    /// <summary>What the composer's model pill reads: the unified row's label, a hand-typed id, or the
    /// setup nudge when nothing is selectable yet.</summary>
    public string ModelPillText =>
        SelectedModelChoice?.Display
        ?? (string.IsNullOrEmpty(SelectedModel)
            ? (SelectedAiProvider?.DisplayName ?? "choose a model")
            : SelectedModel!);

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

    // ── Unified model picker ────────────────────────────────────────────────────────────────────────

    /// <summary>Every provider × its known models, flattened into one list ("claude-opus-4-8 · Claude
    /// Code (installed CLI)") — a single dropdown over the provider/model machinery underneath.
    /// Unavailable providers' rows are included, tagged via <see cref="AiModelChoice.IsAvailable"/>.</summary>
    public ObservableCollection<AiModelChoice> AllModels { get; }

    /// <summary>The unified picker's selection. Setting it drives <see cref="SelectedAiProvider"/> +
    /// <see cref="SelectedModel"/>; changing those (the classic pickers, a restore) points it back at
    /// the matching row, or null for a hand-typed model id with no row.</summary>
    [ObservableProperty] private AiModelChoice? _selectedModelChoice;

    /// <summary>Guards the two-way sync between the unified picker and the provider/model pair, so
    /// neither setter can re-trigger the other.</summary>
    private bool _syncingModelChoice;

    partial void OnSelectedModelChoiceChanged(AiModelChoice? value)
    {
        if (_syncingModelChoice || value is null) return;

        _syncingModelChoice = true;
        try
        {
            if (SelectedAiProvider?.ProviderId != value.ProviderId &&
                AiProviders.FirstOrDefault(p => p.ProviderId == value.ProviderId) is { } provider)
            {
                SelectedAiProvider = provider;   // repopulates Models and re-seeds SelectedModel/effort
            }

            if (value.ModelId.Length == 0)
            {
                // The "vendor default" row (a CLI with no pinned model): whatever the provider offers.
                SelectedModel = Models.FirstOrDefault();
            }
            else
            {
                if (!Models.Contains(value.ModelId, StringComparer.OrdinalIgnoreCase))
                    Models.Insert(0, value.ModelId);
                SelectedModel = value.ModelId;
            }
        }
        finally
        {
            _syncingModelChoice = false;
        }
    }

    /// <summary>The reverse sync: after the provider/model pair moves (classic pickers, restore, model
    /// refresh), point the unified picker at the row that matches — or null when none does.</summary>
    private void SyncModelChoice()
    {
        if (_syncingModelChoice) return;

        _syncingModelChoice = true;
        try
        {
            SelectedModelChoice = AllModels.FirstOrDefault(c =>
                c.ProviderId == SelectedAiProvider?.ProviderId &&
                (string.IsNullOrEmpty(SelectedModel)
                    ? c.ModelId.Length == 0
                    : c.ModelId.Equals(SelectedModel, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _syncingModelChoice = false;
        }

        OnPropertyChanged(nameof(ModelPillText));
    }

    // ── Build effort (the pipeline dial — separate from the model's reasoning effort) ───────────────

    /// <summary>The four pipeline efforts, for the picker.</summary>
    public IReadOnlyList<StrategyBuildEffort> BuildEfforts { get; } =
        [StrategyBuildEffort.Quick, StrategyBuildEffort.Standard, StrategyBuildEffort.Deep, StrategyBuildEffort.Max];

    /// <summary>How hard the BUILD works — skill budget, auto-fix retries, and whether the self-review /
    /// backtest-smoke passes run (<see cref="StrategyBuildProfile.For"/>). Orthogonal to
    /// <see cref="SelectedEffort"/>, which is how hard the model thinks inside one generation.</summary>
    [ObservableProperty] private StrategyBuildEffort _buildEffort = StrategyBuildEffort.Standard;

    partial void OnAuthoringKindChanged(AuthoringKind value)
    {
        // Same rule as the effort dials: the kind is baked into the cached system prompt, so a new choice
        // needs a new session rather than a prompt that contradicts the thread above it.
        ResetSession(value == AuthoringKind.Visualizer ? "Switched to a visualizer." : "Switched to a strategy.");
        Persist();
    }

    partial void OnBuildEffortChanged(StrategyBuildEffort value)
    {
        // One dial. Reasoning used to be a second dropdown, which meant a user could ask for Max and be
        // quietly given the cheap setting on the other control — so the build effort now carries it.
        SelectedEffort = StrategyBuildProfile.For(value).Reasoning;

        // The profile is fixed at session creation (its skill budget shapes the cached system prompt),
        // so a new effort needs a new session — the same rule as switching the model's own effort.
        ResetSession("Switched build effort.");
        Persist();
    }

    // ── Agent CLI hand-off ──────────────────────────────────────────────────────────────────────────

    /// <summary>The installed agent CLIs the workspace launcher can open. Empty when none are on PATH,
    /// or when the launcher isn't wired — either way the UI hides the hand-off.</summary>
    public IReadOnlyList<AgentCliAdapter> AvailableClis => _cliLauncher?.AvailableClis() ?? [];

    /// <summary>Scaffolds this strategy's Hyperion workspace (context pack, skills, starter project)
    /// and opens the CLI there in a real terminal — interactive, never headless.</summary>
    [RelayCommand]
    private void LaunchCli(AgentCliAdapter? adapter)
    {
        if (_cliLauncher is null || adapter is null) return;
        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            Status = "Give the strategy an id first — it names the workspace folder.";
            return;
        }

        try
        {
            var result = _cliLauncher.Launch(adapter, StrategyId.Trim(), DisplayName.Trim(), BuildEffort);
            Status = result.Message;
            _logger.LogInformation(
                "CLI workspace launch for {Id} via {Cli}: success={Success} at {Path}",
                StrategyId, adapter.DisplayName, result.Success, result.WorkspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI workspace launch threw for {Id}", StrategyId);
            Status = $"Couldn't launch {adapter.DisplayName}: {ex.Message}";
        }
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

    /// <summary>
    /// The turn's pipeline as a structured checklist (Understand brief → Load skills → Generate →
    /// Compile → Auto-fix → Self-review → Backtest smoke, the last two only at Deep/Max build effort) —
    /// the right panel's "Tasks" row. Re-seeded at the start of every Send turn and advanced from the
    /// same activity stream that feeds <see cref="Activity"/>; bounded by construction (one row per
    /// step, at most seven).
    /// </summary>
    public ObservableCollection<BuildTask> Tasks { get; }

    private BuildTask? _taskBrief, _taskSkills, _taskGenerate, _taskCompile, _taskAutoFix, _taskReview, _taskSmoke;

    /// <summary>One-shot guards so a repeated activity string can't append the same tool card twice
    /// within a turn. Reset by <see cref="SeedTasks"/>.</summary>
    private bool _reviewCardEmitted, _smokeCardEmitted;

    /// <summary>Fresh checklist for a new turn — the optional passes appear only when the profile buys them.</summary>
    private void SeedTasks(StrategyBuildProfile profile)
    {
        Tasks.Clear();
        Tasks.Add(_taskBrief = new BuildTask("Understand brief"));
        Tasks.Add(_taskSkills = new BuildTask("Load skills"));
        Tasks.Add(_taskGenerate = new BuildTask("Generate"));
        Tasks.Add(_taskCompile = new BuildTask("Compile"));
        Tasks.Add(_taskAutoFix = new BuildTask("Auto-fix"));
        _taskReview = profile.SelfReview ? new BuildTask("Self-review") : null;
        if (_taskReview is not null) Tasks.Add(_taskReview);
        _taskSmoke = profile.Verify ? new BuildTask("Verification") : null;
        if (_taskSmoke is not null) Tasks.Add(_taskSmoke);

        _taskBrief!.State = BuildTaskState.Running;
        _reviewCardEmitted = _smokeCardEmitted = false;
        RefreshWorkStatus();
    }

    /// <summary>Maps the session's activity strings onto the checklist. Prefix matching against the
    /// strings <see cref="StrategyBuildSession"/> reports — cosmetic by design: an unrecognized step
    /// just doesn't advance the strip, it never breaks a turn.</summary>
    private void AdvanceTasks(string step)
    {
        if (step.StartsWith("Loaded reference", StringComparison.Ordinal))
        {
            Done(_taskBrief);
            Done(_taskSkills);
        }
        else if (step.StartsWith("Asking", StringComparison.Ordinal))
        {
            Done(_taskBrief);
            Done(_taskSkills);
            if (step.Contains("to fix", StringComparison.Ordinal)) Run(_taskAutoFix);
            Run(_taskGenerate);
        }
        else if (step.StartsWith("Compiling", StringComparison.Ordinal))
        {
            Done(_taskGenerate);
            Run(_taskCompile);
        }
        else if (step.StartsWith("Compiled", StringComparison.Ordinal))
        {
            Done(_taskCompile);
            Done(_taskAutoFix);   // ran and won, or was never needed — either way it isn't outstanding
        }
        else if (step.StartsWith("Self-review", StringComparison.Ordinal) ||
                 step.StartsWith("The self-review", StringComparison.Ordinal))
        {
            if (step.StartsWith("Self-review pass", StringComparison.Ordinal))
            {
                Run(_taskReview);
            }
            else
            {
                Done(_taskReview);
                if (!_reviewCardEmitted)
                {
                    _reviewCardEmitted = true;
                    Append(AuthoringMessage.Tool("Ok", "Self-review", step));
                }
            }
        }
        else if (step.StartsWith("Backtest smoke", StringComparison.Ordinal))
        {
            if (step.Contains("passed", StringComparison.Ordinal))
            {
                Done(_taskSmoke);
                EmitSmokeCard("Ok", step);
            }
            else if (step.Contains("failed", StringComparison.Ordinal))
            {
                Fail(_taskSmoke);
                EmitSmokeCard("Fail", step);
            }
            else
            {
                Run(_taskSmoke);
            }
        }
        else if (step.StartsWith("Still", StringComparison.Ordinal))
        {
            Fail(_taskCompile);
            Fail(_taskAutoFix);
        }
        else if (step.Contains("has a question", StringComparison.Ordinal))
        {
            Done(_taskGenerate);
        }
        else if (step.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            Fail(_taskGenerate);
        }

        RefreshWorkStatus();
    }

    private void EmitSmokeCard(string state, string step)
    {
        if (_smokeCardEmitted) return;
        _smokeCardEmitted = true;
        Append(AuthoringMessage.Tool(state, "Backtest smoke", step));
    }

    /// <summary>Settles the checklist when the turn ends: a compiled turn closes everything that didn't
    /// fail; a question leaves the not-yet-applicable steps pending; anything running on a failure is
    /// marked failed.</summary>
    /// <summary>
    /// Builds the live preview from a compiled unit, or explains why there is none.
    ///
    /// <para>Never throws: this runs while the user is looking at a result, and a preview that takes the
    /// pane down with it would turn a strategy that merely draws oddly into a lost session.</para>
    /// </summary>
    private void ShowPreview(AuthoredUnit? unit)
    {
        if (unit is null)
        {
            PreviewDraw = null;
            PreviewLayout = DaxAlgo.Sdk.Layout.UnitLayout.Single;
            HasPreview = false;
            PreviewSummary = "No preview — nothing was resolved from the compiled code.";
            return;
        }

        OpenWorkbench();

        try
        {
            var preview = AuthoredUnitPreview.Create(unit);
            PreviewDraw = preview.Draw;

            // The pane must show the same panel arrangement the terminal will. A preview that laid
            // panels out differently would be worse than none, because the author designs against it.
            PreviewLayout = preview.Layout;
            HasPreview = preview.IsDrawable;
            PreviewSummary = preview.Summary;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Preview failed for {Id}.", StrategyId);
            PreviewDraw = null;
            PreviewLayout = DaxAlgo.Sdk.Layout.UnitLayout.Single;
            HasPreview = false;
            PreviewSummary = $"Preview unavailable: {ex.Message}";
        }
    }

    /// <summary>Drops the picture. A preview that outlives the code that produced it is worse than
    /// none, because it is the one thing on screen a user will trust without re-reading.</summary>
    /// <summary>The most recent unit that compiled, so a turn which does not produce one can still show
    /// something true. Null until the first clean compile.</summary>
    private AuthoredUnit? _lastGoodUnit;

    /// <summary>
    /// Brings the preview up to date at the end of <b>every</b> turn, not only a compiling one.
    ///
    /// <para>Refreshing only on <see cref="BuildTurnKind.Compiled"/> meant a clarifying question wiped
    /// the preview — the model asks "which timeframe?", and the panel the user was looking at goes
    /// blank for a turn that changed no code at all. It also meant a failed turn left the panel empty
    /// while the user fixed errors, throwing away the last thing that worked.</para>
    ///
    /// <para><b>A stale preview is only shown if it says it is stale.</b> After a failed compile the
    /// last good render stays up, captioned as being from before the change that broke. Showing an old
    /// picture as though it were current would be the same silent-wrong failure this codebase keeps
    /// running into; showing it labelled is just keeping the user's place.</para>
    /// </summary>
    private void RefreshPreview(StrategyBuildTurn turn)
    {
        if (turn.Compile?.Unit is { } compiled)
        {
            _lastGoodUnit = compiled;
            ShowPreview(compiled);
            return;
        }

        if (_lastGoodUnit is null)
        {
            ClearPreview();
            PreviewSummary = turn.Kind == BuildTurnKind.Question
                ? "No preview yet — answer the question above and the build will produce one."
                : "No preview — nothing has compiled yet.";
            return;
        }

        ShowPreview(_lastGoodUnit);

        // A question changed no code, so the preview is still current and needs no caveat. A failed
        // compile did change code, so it does.
        if (turn.Kind != BuildTurnKind.Question)
        {
            PreviewSummary = $"{PreviewSummary} (from before the last change, which did not compile)"
                .TrimStart();
        }
    }

    private void ClearPreview()
    {
        PreviewDraw = null;
        PreviewLayout = DaxAlgo.Sdk.Layout.UnitLayout.Single;
        HasPreview = false;
        PreviewSummary = string.Empty;
    }

    private void FinishTasks(BuildTurnKind kind)
    {
        var success = kind is BuildTurnKind.Compiled or BuildTurnKind.Question;
        foreach (var task in Tasks)
        {
            if (task.State == BuildTaskState.Running)
                task.State = success ? BuildTaskState.Done : BuildTaskState.Failed;
            else if (kind == BuildTurnKind.Compiled && task.State == BuildTaskState.Pending)
                task.State = BuildTaskState.Done;
        }

        RefreshWorkStatus();
    }

    /// <summary>A stopped/crashed turn: whatever was in flight didn't finish.</summary>
    private void FailRunningTasks()
    {
        foreach (var task in Tasks)
            if (task.State == BuildTaskState.Running) task.State = BuildTaskState.Failed;

        RefreshWorkStatus();
    }

    private static void Run(BuildTask? task)
    {
        if (task is not null && task.State != BuildTaskState.Failed) task.State = BuildTaskState.Running;
    }

    private static void Done(BuildTask? task)
    {
        if (task is not null && task.State != BuildTaskState.Failed) task.State = BuildTaskState.Done;
    }

    private static void Fail(BuildTask? task)
    {
        if (task is not null) task.State = BuildTaskState.Failed;
    }

    /// <summary>The chat composer. Multi-line: Enter adds a newline, Ctrl+Enter sends.</summary>
    [ObservableProperty] private string _composer = string.Empty;

    [ObservableProperty] private string? _aiStatus;
    [ObservableProperty] private bool _isGenerating;

    /// <summary>"1m 20s elapsed…" while a turn runs. A detailed brief at a high effort is a multi-minute
    /// request; without a clock ticking, a working generation is indistinguishable from a hang.</summary>
    [ObservableProperty] private string? _elapsedText;

    /// <summary>"2:41" — the session header's compact clock while a turn runs.</summary>
    [ObservableProperty] private string? _elapsedCompact;

    /// <summary>The shimmering status verb ("Writing the strategy…") — the current pipeline step,
    /// phrased as what the agent is doing rather than as a checklist label.</summary>
    [ObservableProperty] private string? _workingVerb;

    /// <summary>"step 3 of 6" next to the verb.</summary>
    [ObservableProperty] private string? _stepText;

    /// <summary>Re-derives the verb + step counter from the checklist. Called whenever a task state
    /// moves; null when nothing is running (which stops the shimmer).</summary>
    private void RefreshWorkStatus()
    {
        var running = Tasks.FirstOrDefault(t => t.State == BuildTaskState.Running);
        if (running is null)
        {
            WorkingVerb = null;
            StepText = null;
            return;
        }

        StepText = $"step {Tasks.IndexOf(running) + 1} of {Tasks.Count}";
        WorkingVerb = running.Title switch
        {
            "Understand brief" => "Reading the brief…",
            "Load skills" => "Loading skills…",
            "Generate" => "Writing the strategy…",
            "Compile" => "Compiling…",
            "Auto-fix" => "Fixing compile errors…",
            "Self-review" => "Self-reviewing the code…",
            "Backtest smoke" => "Running the backtest smoke…",
            _ => running.Title + "…",
        };
    }

    /// <summary>The model asked a question instead of writing code, and is waiting for the answer. It is
    /// a normal turn — the strategy is under-specified and it wants to know, rather than guess.</summary>
    [ObservableProperty] private bool _awaitingAnswer;

    /// <summary>The assistant bubble currently being streamed into, or null between turns.</summary>
    private AuthoringMessage? _streamingReply;

    /// <summary>This turn's thinking block, or null before the model has thought anything (and for
    /// every model that has no reasoning channel at all).</summary>
    private AuthoringMessage? _thinking;

    /// <summary>Agent turns started in the current run — the status bar's "turn 3 of 12".</summary>
    private int _agentTurnsSeen;

    [ObservableProperty] private int _inputTokens;
    [ObservableProperty] private int _outputTokens;
    [ObservableProperty] private int _cachedTokens;

    /// <summary>
    /// What the status bar says when there is no meter to draw.
    ///
    /// <para>Empty before the first turn — the string-to-visibility converter hides it, rather than
    /// showing a zero that means nothing. After a turn whose provider reported no usage (an agent CLI,
    /// typically) it says so IN WORDS: unknown is not the same as free, and a bar drawn at zero would
    /// claim the turn cost nothing.</para>
    ///
    /// <para>A property rather than a trigger in the view, because the view had this rule spelled out
    /// as a two-condition MultiDataTrigger in markup — a second copy of a view-model decision, in the
    /// one place no test can reach it.</para>
    /// </summary>
    public string UsageFallbackText =>
        !HasConversation || HasUsage ? string.Empty : "tokens not reported";

    /// <summary>Tokens billed this session, spelled out. The cached share is called out because it is
    /// the difference between a long conversation costing a little and costing a lot — and because a
    /// session where it stays at zero is one paying full price to re-read the same context every
    /// turn.</summary>
    public string UsageText => !HasConversation
        ? string.Empty
        : InputTokens + OutputTokens == 0
        ? "tokens: not reported"
        : CachedTokens > 0
            ? $"tokens: {InputTokens:N0} in ({CachedTokens:N0} cached) · {Approx}{OutputTokens:N0} out"
            : $"tokens: {InputTokens:N0} in · {Approx}{OutputTokens:N0} out";

    /// <summary>"~" while the output count is estimated, nothing once it is measured.</summary>
    private string Approx => IsUsageEstimated ? "~" : string.Empty;

    // ── The status bar's token meter ────────────────────────────────────────────────────────────────
    //
    // UsageText above is the sentence; this is the picture beside it. The numbers were a 9.5pt grey
    // string wedged into the corner of a panel that is closed by default, which is not an indicator —
    // a figure the user is billed for cannot live somewhere they have to go looking for it. It sits in
    // the status bar now, permanently, next to the model that is spending it.
    //
    // NO CONTEXT-WINDOW GAUGE. The obvious design is a "42% of context used" bar, and it would be a
    // fabrication: nothing in the catalogue knows any model's window, the app talks to a dozen
    // providers plus arbitrary custom model ids, and a limit invented here would be wrong silently and
    // stale permanently. The meter shows the split we actually measure — fresh input, cached input,
    // output — as three proportional segments of one bar.

    /// <summary>Total tokens billed this session.</summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>True once a turn has reported real usage, which is what puts the meter on screen. An
    /// agent CLI that reports nothing leaves this false, and the readout says "not reported" rather
    /// than drawing an empty bar that would read as "free".</summary>
    public bool HasUsage => HasConversation && TotalTokens > 0;

    /// <summary>"18.6k" — the total, at a glance, in the status bar.</summary>
    public string TotalTokensText => TotalTokens >= 1000
        ? $"{TotalTokens / 1000.0:0.#}k"
        : TotalTokens.ToString("N0");

    /// <summary>The meter track's inside width in device-independent pixels. Kept in step with
    /// <c>HYP.MeterTrack</c>'s 86px width less its 1px border on each side.</summary>
    private const double MeterWidth = 84;

    /// <summary>Width of the fresh (uncached, full-price) input segment.</summary>
    public double MeterInputWidth => Segment(Math.Max(0, InputTokens - CachedTokens));

    /// <summary>Width of the cached-input segment — the cheap share of the prompt.</summary>
    public double MeterCachedWidth => Segment(CachedTokens);

    /// <summary>Width of the output segment.</summary>
    public double MeterOutputWidth => Segment(OutputTokens);

    private double Segment(int tokens)
    {
        // Cached input is a SUBSET of input, so the denominator is input + output — counting it
        // separately would make a well-cached session's bar overflow its own track.
        var total = (double)TotalTokens;
        return total <= 0 ? 0 : Math.Round(MeterWidth * (tokens / total), 2);
    }

    /// <summary>The meter's tooltip: every figure, spelled out, plus what the cached share means. A bar
    /// nobody can read the exact numbers out of is decoration.</summary>
    public string UsageDetail => !HasUsage
        ? "No usage reported yet for this session."
        : $"""
           Session usage — charged by your own AI provider.

           Input      {InputTokens:N0}
             cached   {CachedTokens:N0}   (billed at a fraction of the full rate)
             fresh    {Math.Max(0, InputTokens - CachedTokens):N0}
           Output     {Approx}{OutputTokens:N0}
           Total      {TotalTokens:N0}

           A session whose cached figure stays at zero is paying full price to re-read the same
           context on every turn.
           """;

    private void RefreshUsage()
    {
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(UsageFallbackText));
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(TotalTokensText));
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageDetail));
        OnPropertyChanged(nameof(MeterInputWidth));
        OnPropertyChanged(nameof(MeterCachedWidth));
        OnPropertyChanged(nameof(MeterOutputWidth));
    }

    // ── The status bar's state ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the workspace is doing, in one word, for the status bar's left end.
    ///
    /// <para>Working beats everything: while a turn is in flight that is the only fact worth reading.
    /// Then the question-back, because it is the one state where nothing happens until the USER moves —
    /// the failure mode this exists to stop is a generation that quietly ended in a question the user
    /// never noticed, leaving them to conclude the builder hung.</para>
    /// </summary>
    public string StateText =>
        IsGenerating ? "Working"
        : AwaitingAnswer || HasQuestions ? "Waiting for you"
        : Diagnostics.Any(d => d.Severity == StrategyDiagnosticSeverity.Error) ? "Build failed"
        : IsRegistered ? "Registered"
        : HasConversation ? "Ready"
        : "Idle";

    /// <summary>Which colour the state dot takes: Busy · Ask · Error · Idle. Derived from the same
    /// expression as <see cref="StateText"/> so the word and the colour can never disagree.</summary>
    public string StateKind =>
        IsGenerating ? "Busy"
        : AwaitingAnswer || HasQuestions ? "Ask"
        : Diagnostics.Any(d => d.Severity == StrategyDiagnosticSeverity.Error) ? "Error"
        : "Idle";

    private void RefreshState()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateKind));
    }

    /// <summary>Characters streamed in the current generation, for the in-flight token estimate.</summary>
    private int _streamedCharacters;

    /// <summary>True while the output count is an estimate rather than the provider's own number. Shown
    /// as a "~" so nobody quotes an approximation as a measurement.</summary>
    [ObservableProperty] private bool _isUsageEstimated;

    partial void OnIsUsageEstimatedChanged(bool value) => RefreshUsage();

    /// <summary>
    /// Roughly four characters per token — the usual English-and-code approximation.
    ///
    /// <para>Deliberately not a tokenizer. The number exists so a running generation shows movement
    /// rather than a frozen zero, and it is replaced by the provider's exact figure a moment later;
    /// carrying a real tokenizer to be briefly less wrong about a number nobody bills from would be a
    /// poor trade.</para>
    /// </summary>
    private static int EstimateTokens(int characters) => characters / 4;

    partial void OnInputTokensChanged(int value) => RefreshUsage();
    partial void OnOutputTokensChanged(int value) => RefreshUsage();
    partial void OnCachedTokensChanged(int value) => RefreshUsage();

    partial void OnIsGeneratingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RefreshState();
    }

    partial void OnAwaitingAnswerChanged(bool value) => RefreshState();
    partial void OnIsRegisteredChanged(bool value) => RefreshState();
    partial void OnHasQuestionsChanged(bool value) => RefreshState();

    partial void OnComposerChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private bool CanSend => !IsGenerating && !string.IsNullOrWhiteSpace(Composer);

    /// <summary>
    /// One turn: send what the user typed (plus their hand-edits, if any), let the session generate →
    /// compile → auto-fix, and land the result in the chat, the file list and the diagnostics. It does
    /// NOT register — the user reviews the code and presses Compile &amp; Register, which is the consent for
    /// running model-authored code (it's already scan-gated, so a strategy that P/Invokes never compiles).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        // The command's own CanExecute already blocks a second click, but `ChooseAsync` and
        // `SubmitAnswersAsync` call this method DIRECTLY — a one-click reply and an answered question
        // are still sends — and neither goes through CanExecute. Two overlapping turns would share one
        // `_generateCts`, so the second would cancel the first's token and then wait on its ticker.
        if (IsGenerating) return;

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

        // First brief on an untouched identity: name the strategy after what it does, not "myStrategy".
        if (Messages.Count == 0) DeriveIdentityFrom(prompt);

        Composer = string.Empty;
        Append(new AuthoringMessage(CodegenRole.User, prompt));
        Activity.Clear();
        Diagnostics.Clear();
        CompiledOk = false;

        // The preview is deliberately NOT cleared here. Blanking it at the start of a turn means the
        // panel is empty for however long generation takes, and empty again afterwards if the turn was
        // a question. RefreshPreview() at the end of the turn owns it, so the last good render stays up
        // while the model works.
        AwaitingAnswer = false;

        // The previous turn's verdict, cleared before this one starts.
        //
        // It used to be left up for the whole generation, so answering "the agent is waiting — pick an
        // option above" left that sentence on screen while the answer was being processed. Nothing was
        // wrong, but the one line of text the user was watching had not changed, so the obvious reading
        // was that their answer had not registered.
        AiStatus = null;
        IsGenerating = true;

        // ── Everything from here is inside ONE guard, and that is the whole point of this shape ──
        //
        // `IsGenerating` gates the Send button (CanSend) and swaps it for Stop. It used to be raised
        // here and lowered in a `finally` that began several statements later, so anything that threw
        // in the gap left it true FOREVER: Send permanently disabled, Stop permanently showing, and
        // the only way out was to restart the terminal.
        //
        // The gap was not hypothetical. `EnsureSession` builds a `StrategyBuildSession`, whose
        // constructor loads the context pack — and that throws by design when
        // `sdk/ai-context/generated/sdk-surface.md` is missing. So a build shipped without the
        // generated pack bricked the composer on the user's FIRST prompt. `SeedTasks`, `Append` and
        // `SyncEditedFiles` were in the same gap.
        //
        // The agent branch had the second half of the problem: a `finally` that lowered the flag but
        // no `catch`, so an agent fault escaped into the dispatcher instead of the transcript.
        //
        // One try/catch/finally now covers both paths. A failed turn is a message; it is never a
        // window the user has to close.
        var ticking = Task.CompletedTask;

        try
        {
            // The pipeline's dial for this turn. SeedTasks still runs: the checklist drives the working
            // verb and the step counter in the status bar ("Writing the strategy… step 3 of 6"), which
            // is the honest live signal.
            //
            // WHAT IT NO LONGER DOES IS PIN A PLAN CARD INTO THE TRANSCRIPT. Every turn opened with a
            // six-row checklist of OUR pipeline stages — not the model's plan, not anything the user
            // asked for — so a conversation read as a stack of project-management cards with the actual
            // exchange threaded between them. Other coding harnesses do the plain thing: you type, it
            // replies, it asks, it writes the code. The checklist belongs in the status bar, where a
            // progress indicator belongs, and it is there.
            var profile = StrategyBuildProfile.For(BuildEffort);
            SeedTasks(profile);

            _generateCts?.Cancel();
            _generateCts?.Dispose();
            _generateCts = new CancellationTokenSource();

            ticking = TickElapsedAsync(_generateCts.Token);

            // Deep and Max buy the agents. Quick and Standard keep the single conversation below,
            // which is cheaper and right for a brief that does not need a committee.
            if (profile.UseAgents)
            {
                await RunAgentsAsync(choice, prompt, profile, _generateCts.Token);
                return;
            }

            var session = EnsureSession(choice, profile);
            var tokensBefore = session.TotalUsage;
            _streamingReply = null;
            _thinking = null;

            // The editor is the truth: hand-edits and all. The session ships exactly one copy of it
            // with the turn, so the model always works from the code that is actually there.
            session.SyncEditedFiles([.. Files.Select(f => new StrategyFile(f.Name, f.Content))]);
            _filesEditedByUser = false;

            var turn = await session.SendAsync(
                prompt,
                new Progress<string>(step => PushActivity(step)),
                _generateCts.Token,
                new Progress<CodegenEvent>(evt => OnStreamed(evt, tokensBefore)));

            // The session's running total is authoritative WHEN THERE IS ONE. A provider that reports
            // no usage at all — NVIDIA NIM does not, and agent CLIs do not — leaves the total at zero,
            // and assigning that unconditionally wiped the estimate accumulated while the reply
            // streamed. The counter then read "not reported" after a generation that plainly produced
            // thousands of tokens, which is worse than the approximation it replaced.
            if (session.TotalUsage.InputTokens > 0 || session.TotalUsage.OutputTokens > 0)
            {
                InputTokens = session.TotalUsage.InputTokens;
                OutputTokens = session.TotalUsage.OutputTokens;
                CachedTokens = session.TotalUsage.CachedInputTokens;
                IsUsageEstimated = false;
            }

            FinishTasks(turn.Kind);

            if (turn.Kind == BuildTurnKind.ProviderError)
            {
                AiStatus = $"{choice.DisplayName} failed: {turn.Error}";
                Append(AuthoringMessage.Tool("Fail", $"{choice.DisplayName} failed", turn.Error ?? "The provider returned an error."));
                return;
            }

            // The reply was streamed into a bubble as it arrived; settle it on the final text (the
            // provider's own assembled version). Nothing streamed ⇒ the provider doesn't stream, so the
            // bubble appears now, whole.
            // Structured questions are lifted out first, so the bubble shows the model's prose and not
            // the JSON it used to offer the buttons.
            var asked = turn.Kind == BuildTurnKind.Question
                ? AuthoringQuestions.Parse(turn.AssistantText)
                : [];
            var visibleText = asked.Count > 0
                ? AuthoringQuestions.StripBlock(turn.AssistantText)
                : turn.AssistantText;

            if (_streamingReply is null) Append(new AuthoringMessage(CodegenRole.Assistant, visibleText));
            else _streamingReply.Text = visibleText;

            SetQuestions(asked);
            AwaitingAnswer = turn.Kind == BuildTurnKind.Question;

            // Unconditional: a turn that stops without code is usually a specification awaiting
            // approval rather than a question with options, and that case has nothing to enumerate.
            // Without this the user is left with a paragraph and an empty composer, which is exactly
            // what they were before any of the question work.
            //
            // Shaped by whether the model asked, though. Offering "looks right, build it" beside
            // "which instrument?" answers a question nobody asked; an interview needs a way out
            // instead, which is the other half of telling the model to ask as many as the job needs.
            SetActions(AwaitingAnswer ? AuthoringAction.For(asked.Count > 0) : []);

            if (turn.Files.Count > 0)
            {
                var prior = Files.ToDictionary(f => f.Name, f => f.Content, StringComparer.OrdinalIgnoreCase);
                SetFiles(turn.Files);
                _filesEditedByUser = false;
                AppendFileChanges(prior, turn.Files);
            }

            foreach (var diagnostic in turn.Compile?.Diagnostics ?? [])
                Diagnostics.Add(diagnostic);

            // The turn's compile verdict as a card — the numbers the user actually wants at a glance.
            if (turn.Kind == BuildTurnKind.Compiled)
            {
                var warnings = turn.Compile?.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning) ?? 0;
                Append(AuthoringMessage.Tool(
                    "Ok", "Compiled",
                    $"{turn.Files.Count} file(s) · {turn.Generations} generation(s)" +
                    (warnings > 0 ? $" · {warnings} warning(s)" : string.Empty)));

            }
            else if (turn.Kind != BuildTurnKind.Question)
            {
                Append(AuthoringMessage.Tool(
                    "Fail", "Compile failed",
                    $"{turn.Compile?.Errors.Count() ?? 0} error(s) after {turn.Generations} generation(s) — see Diagnostics"));
            }

            RefreshPreview(turn);

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
            FailRunningTasks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI builder turn threw for {Id}", StrategyId);
            AiStatus = $"Generation error: {ex.Message}";
            Append(AuthoringMessage.System(AiStatus));
            FailRunningTasks();
        }
        finally
        {
            // Unconditional, and the reason the try starts where it does. Whatever happened, the
            // composer goes back to accepting a prompt.
            IsGenerating = false;
            _streamingReply = null;
            _thinking = null;
            _generateCts?.Cancel();   // stops the elapsed ticker

            // Awaited inside its own guard: the ticker is a courtesy, and a fault in it must not be
            // the thing that stops IsGenerating being lowered.
            try { await ticking; } catch (OperationCanceledException) { }

            ElapsedText = null;
            ElapsedCompact = null;
            Save();   // a turn is expensive — never lose one to a crash or a restart
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

                // An OpenAI-compatible provider reports usage only in its FINAL chunk, so without this
                // the counter reads zero for the whole generation and then jumps — on a slow provider
                // that is minutes of a number that looks broken. Estimated from the text so far and
                // replaced by the provider's own figure the moment it arrives.
                _streamedCharacters += delta.Text.Length;
                OutputTokens = tokensBefore.OutputTokens + EstimateTokens(_streamedCharacters);
                IsUsageEstimated = true;
                break;

            case CodegenEvent.ReasoningDelta thought:
                // Into its own collapsed block, one per turn, ABOVE the reply it precedes — which is
                // also the order it happens in. A reasoning model on a hard brief thinks for minutes
                // before it writes a word, and this is the only thing on screen during that time that
                // is actually the model rather than a spinner we drew.
                if (_thinking is null)
                {
                    _thinking = AuthoringMessage.Thinking(thought.Text);
                    Append(_thinking);
                }
                else
                {
                    _thinking.Text += thought.Text;
                }

                // Thinking is billed as output. Counting it keeps the meter honest during the long
                // silence before any reply text exists — without it a five-minute think reads as a
                // turn that cost nothing.
                _streamedCharacters += thought.Text.Length;
                OutputTokens = tokensBefore.OutputTokens + EstimateTokens(_streamedCharacters);
                IsUsageEstimated = true;
                break;

            case CodegenEvent.UsageUpdate update:
                // Authoritative. Everything estimated above is discarded rather than blended: a count
                // that is partly measured and partly guessed is worse than either.
                IsUsageEstimated = false;
                _streamedCharacters = 0;
                // The update is absolute for the CURRENT generation, so add it to what the session had
                // banked before this turn. The exact total is set from the session when the turn ends.
                InputTokens = tokensBefore.InputTokens + update.Usage.InputTokens;
                OutputTokens = tokensBefore.OutputTokens + update.Usage.OutputTokens;
                CachedTokens = tokensBefore.CachedInputTokens + update.Usage.CachedInputTokens;
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
                ElapsedCompact = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
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

    /// <summary>
    /// Cancels the running turn.
    ///
    /// <para>It also lowers <see cref="IsGenerating"/> itself rather than leaving that to the turn's
    /// own <c>finally</c>. Cancellation is cooperative — a provider that ignores its token, or a
    /// subprocess that has stopped reading it, can leave the await outstanding indefinitely — and Stop
    /// is the control a user presses precisely when something is not responding. A Stop that needs the
    /// thing it is stopping to cooperate is not an escape hatch. The turn's own finally sets the same
    /// flag again, harmlessly.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _generateCts?.Cancel();
        IsGenerating = false;
        AiStatus = "Stopped.";
    }

    /// <summary>Start over: a fresh thread with the model, the starter template back in the editor. The
    /// previous chat is NOT deleted — it stays in the picker under its own strategy id, so "new chat" can
    /// never cost the user a conversation. Give the new one a new id before sending, or it will overwrite
    /// the old one's file on the first turn.</summary>
    [RelayCommand]
    private void NewChat()
    {
        Save();   // bank the outgoing conversation before abandoning it

        _restoring = true;
        try
        {
            ResetSession(null);
            _restoredThread = null;
            _restoredUsage = null;
            Messages.Clear();
            Activity.Clear();
            Tasks.Clear();
            Diagnostics.Clear();
            InputTokens = OutputTokens = 0;
            CompiledOk = false;
            ClearPreview();
            _lastGoodUnit = null;
            SetQuestions([]);
            SetActions([]);
            AwaitingAnswer = false;
            IsRegistered = false;
            CloseReview();
            _registeredBaseline.Clear();
            RefreshWorkStatus();
            Parameters = null;
            SetFiles([new StrategyFile(StrategyFile.DefaultName, TemplateSource)]);
            _filesEditedByUser = false;
            AiStatus = null;

            // BACK TO THE DEFAULT IDENTITY, and this is the line whose absence deleted people's work.
            //
            // New chat cleared the messages, the files and the preview, and left StrategyId and
            // DisplayName pointing at the conversation it had just abandoned. Two things followed, and
            // the second is the bad one:
            //
            //   * DeriveIdentityFrom returns immediately unless the identity is still the default, so
            //     the new brief could never name its own session. Every conversation after the first
            //     kept the first one's name.
            //   * Save() writes to a file named after StrategyId. So the first turn of the NEW
            //     conversation saved itself OVER the old session. Pressing New Strategy and typing
            //     anything destroyed the conversation you pressed it to keep -- which is exactly what
            //     the Save() on the first line of this method exists to prevent.
            StrategyId = DefaultStrategyId;
            DisplayName = DefaultDisplayName;

            Status = "New conversation. Describe what you want — it will name itself from your first message.";
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>
    /// Replaces the editable draft with source produced outside the local provider loop. This
    /// provider-neutral seam imports text only: it resets model and compile state, tracks later edits,
    /// and never compiles, registers, installs, or executes the draft.
    /// </summary>
    public string ImportDraft(StrategyScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (IsGenerating)
            throw new InvalidOperationException("Stop the active local generation before importing another draft.");
        if (string.IsNullOrWhiteSpace(script.Id) || string.IsNullOrWhiteSpace(script.DisplayName) ||
            script.Files is null || script.Files.Count == 0 ||
            script.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Name) || file.Content is null) ||
            script.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != script.Files.Count)
        {
            throw new ArgumentException(
                "An imported strategy draft requires an id, name, and uniquely named source files.",
                nameof(script));
        }

        NewChat();
        var requestedId = script.Id.Trim();
        var importedId = FindAvailableImportId(requestedId);
        StrategyId = importedId;
        DisplayName = script.DisplayName.Trim();
        SetFiles(script.Files);
        _filesEditedByUser = true;
        // An import IS source arriving, so land on the code, not on a preview of something that has
        // not been compiled yet.
        IsWorkbenchOpen = true;
        WorkbenchTab = WorkbenchTabCode;
        Status = importedId == requestedId
            ? $"Imported '{DisplayName}' as editable source. Compile and review it before registration."
            : $"Imported '{DisplayName}' as '{importedId}' so the existing '{requestedId}' session was preserved. " +
              "Compile and review it before registration.";
        Save();
        return importedId;
    }

    private static string FindAvailableImportId(string requestedId)
    {
        if (AuthoringSessionStore.Load(requestedId) is null) return requestedId;

        for (var suffix = 2; suffix <= 999; suffix++)
        {
            var candidate = $"{requestedId}-imported-{suffix}";
            if (AuthoringSessionStore.Load(candidate) is null) return candidate;
        }

        throw new InvalidOperationException(
            $"No unused local authoring id is available for the imported '{requestedId}' draft.");
    }

    // ── Saved sessions ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Every strategy the user has an authoring chat for, newest first. The rail binds
    /// <see cref="VisibleSessions"/> instead — see the search box below.</summary>
    public ObservableCollection<AuthoringSessionSnapshot> SavedSessions { get; } = [];

    /// <summary>
    /// The rows the rail actually shows: <see cref="SavedSessions"/> filtered by
    /// <see cref="SessionQuery"/>, still newest-first.
    ///
    /// <para>A separate collection rather than an <c>ICollectionView</c> filter because this assembly
    /// is deliberately WPF-free and has no <c>System.Windows.Data</c> to reach for; the view groups
    /// these rows by <c>Group</c> with its own CollectionViewSource.</para>
    /// </summary>
    public ObservableCollection<AuthoringSessionSnapshot> VisibleSessions { get; } = [];

    /// <summary>The rail's search box. Sessions accumulate — that is the point of saving them — and a
    /// history you can only scroll is one you stop using at about thirty entries.</summary>
    [ObservableProperty] private string _sessionQuery = string.Empty;

    partial void OnSessionQueryChanged(string value) => ApplySessionFilter();

    /// <summary>True when there is any saved chat at all — the rail shows its empty invitation
    /// otherwise.</summary>
    public bool HasSavedSessions => SavedSessions.Count > 0;

    /// <summary>True when a search is active and matched nothing — distinct from having no sessions,
    /// and the two need different words on screen.</summary>
    public bool HasNoSessionMatches => HasSavedSessions && VisibleSessions.Count == 0;

    private void ApplySessionFilter()
    {
        // Repopulating re-fires the selection binding, exactly as RefreshSavedSessions does; without
        // the guard, narrowing the search to something that excludes the OPEN session would null the
        // selection and restore a different conversation out from under the user.
        var wasRestoring = _restoring;
        _restoring = true;
        try
        {
            VisibleSessions.Clear();
            foreach (var session in SavedSessions.Where(s => s.Matches(SessionQuery)))
                VisibleSessions.Add(session);

            SelectedSavedSession = VisibleSessions.FirstOrDefault(s => s.StrategyId == StrategyId);
        }
        finally
        {
            _restoring = wasRestoring;
        }

        OnPropertyChanged(nameof(HasSavedSessions));
        OnPropertyChanged(nameof(HasNoSessionMatches));
    }

    /// <summary>Clears the search box — the ✕ inside the field.</summary>
    [RelayCommand]
    private void ClearSessionQuery() => SessionQuery = string.Empty;

    [ObservableProperty] private AuthoringSessionSnapshot? _selectedSavedSession;

    partial void OnSelectedSavedSessionChanged(AuthoringSessionSnapshot? value)
    {
        if (_restoring || value is null || value.StrategyId == StrategyId) return;
        Restore(value);
    }

    /// <summary>Forget a strategy's chat. The strategy itself (if it was registered) is untouched — this
    /// deletes the conversation, not the plugin.</summary>
    [RelayCommand]
    private void DeleteSavedSession(AuthoringSessionSnapshot? session)
    {
        if (session is null) return;

        AuthoringSessionStore.Delete(session.StrategyId);
        RefreshSavedSessions();
        Status = $"Deleted the chat for '{session.DisplayName}'. The strategy itself is untouched.";
    }

    private void RefreshSavedSessions()
    {
        var saved = AuthoringSessionStore.List();

        _restoring = true;   // repopulating the list re-fires the selection binding
        try
        {
            SavedSessions.Clear();
            foreach (var session in saved) SavedSessions.Add(session);
        }
        finally
        {
            _restoring = false;
        }

        // Sets the selection (through its own guard) and republishes the two emptiness flags.
        ApplySessionFilter();
    }

    /// <summary>Loads a saved session back into the pane — the chat, the files, the provider setup, the
    /// token total, AND the model's own thread, so a follow-up like "now tighten the stop" still works.</summary>
    private void Restore(AuthoringSessionSnapshot session)
    {
        _restoring = true;
        try
        {
            _session = null;
            _agentState = new RoutingState();
            _agentContext = null;
            _restoredThread = session.Thread;
            _restoredUsage = new CodegenUsage(session.InputTokens, session.OutputTokens);

            StrategyId = session.StrategyId;
            DisplayName = session.DisplayName;

            // Provider-independent: the pipeline effort comes back even when the provider doesn't.
            // Absent on a pre-build-effort snapshot ⇒ Standard.
            BuildEffort = StrategyBuildEfforts.Parse(session.BuildEffort);

            if (session.ProviderId is { Length: > 0 } providerId &&
                AiProviders.FirstOrDefault(p => p.ProviderId == providerId) is { } provider)
            {
                SelectedAiProvider = provider;
                if (session.Model is { Length: > 0 } model)
                {
                    if (!Models.Contains(model)) Models.Insert(0, model);
                    SelectedModel = model;
                }
                SelectedEffort = CodegenEfforts.Parse(session.Effort);
            }

            Messages.Clear();
            foreach (var entry in session.Chat)
                Append(FromChatEntry(entry));

            if (session.Files.Count > 0) SetFiles(session.Files);

            InputTokens = session.InputTokens;
            OutputTokens = session.OutputTokens;
            Diagnostics.Clear();
            CompiledOk = false;
            ClearPreview();
        ClearPreview();
            AwaitingAnswer = false;
            IsRegistered = session.Registered;
            CloseReview();
            _registeredBaseline.Clear();   // the diff baseline is per-process; a restored review starts from "all new"
            _filesEditedByUser = false;

            SelectedSavedSession = SavedSessions.FirstOrDefault(s => s.StrategyId == session.StrategyId);
            Status = Messages.Count > 0
                ? $"Restored the chat for '{session.DisplayName}' ({session.Age}). Carry on where you left off."
                : "Describe a strategy in the chat, or write one yourself, then press Compile & Register.";
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Writes the current session out. Called after anything worth not losing: a turn, a compile,
    /// an edit. Cheap — a chat is a few KB of JSON.</summary>
    private void Save()
    {
        if (_restoring || !_ready || string.IsNullOrWhiteSpace(StrategyId)) return;
        if (Messages.Count == 0 && !_filesEditedByUser) return;   // nothing worth a file yet

        var snapshot = new AuthoringSessionSnapshot(
            StrategyId: StrategyId.Trim(),
            DisplayName: DisplayName.Trim(),
            // Thinking is deliberately excluded. A reasoning model emits tens of thousands of characters
            // of it per turn, so persisting it would multiply the size of every session file for
            // something nobody reopens a conversation to re-read — the same argument that already drops
            // the expandable tool output.
            Chat: [.. Messages.Where(m => m.Kind != AuthoringMessage.KindThinking).Select(ToChatEntry)],
            // The MODEL's thread, not the chat: it also carries the compiler's auto-fix prompts, which are
            // what let a resumed conversation pick up mid-repair.
            Thread: _session?.Transcript ?? _restoredThread ?? [],
            Files: [.. Files.Select(f => new StrategyFile(f.Name, f.Content))],
            ProviderId: SelectedAiProvider?.ProviderId,
            Model: SelectedModel,
            Effort: SelectedEffort.Wire(),
            BuildEffort: BuildEffort.Wire(),
            InputTokens: InputTokens,
            OutputTokens: OutputTokens,
            Registered: IsRegistered);

        if (!AuthoringSessionStore.Save(snapshot))
        {
            _logger.LogWarning("Could not save the authoring chat for {Id}", StrategyId);
            return;
        }

        RefreshSavedSessions();
    }

    // ── Compile & register (the review gate) ────────────────────────────────────────────────────────

    /// <summary>What the review overlay shows per file. The diff baseline is the last content this
    /// process registered for that file (empty ⇒ everything reads as added — honest for new code).</summary>
    public ObservableCollection<ReviewFileEntry> ReviewFiles { get; } = [];

    [ObservableProperty] private ReviewFileEntry? _selectedReviewFile;
    [ObservableProperty] private bool _reviewOpen;
    [ObservableProperty] private string? _reviewSummary;

    /// <summary>Held between a clean compile and the Register click, so registering never re-compiles
    /// different code than the user just reviewed.</summary>
    private StrategyCompileResult? _pendingCompile;
    private StrategyScript? _pendingScript;

    /// <summary>File contents as of the last successful register (per process). Keys are file names.</summary>
    private readonly Dictionary<string, string> _registeredBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Step 1 of consent: compile everything and, if clean, open the review overlay — per-file diffs
    /// against what was last registered, plus the diagnostics. Registration itself only happens from
    /// <see cref="ConfirmRegisterCommand"/> inside the overlay; there is no path around the review.
    /// </summary>
    [RelayCommand]
    private void Compile()
    {
        Diagnostics.Clear();
        CompiledOk = false;
        ClearPreview();
        Parameters = null;
        CloseReview();

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

        if (!result.Success)
        {
            // A policy-scan Block comes back as an error diagnostic, so a strategy that reaches for
            // P/Invoke / Process / the registry fails here with a clear reason, just like a plugin.
            Status = $"Compile failed — {result.Errors.Count()} error(s).";
            return;
        }

        // A sandbox unit has no Option: that type is the retired contract's registration currency and a
        // kernel never produces one. Treating a null Option as a failure told authors following the
        // current guidance that their correct code did not compile.
        if (result.Unit is null && result.Option is null)
        {
            Status = "Compiled, but nothing hostable was found — define one IStrategyKernel or IVisualizer.";
            return;
        }

        CompiledOk = true;
        if (result.Option is { HasParameters: true })
            Parameters = StrategyParametersViewModel.FromSchema(result.Option.Schema);

        ReviewFiles.Clear();
        foreach (var file in script.Files)
        {
            var baseline = _registeredBaseline.GetValueOrDefault(file.Name, string.Empty);
            ReviewFiles.Add(new ReviewFileEntry(file.Name, LineDiff.Build(baseline, file.Content)));
        }

        SelectedReviewFile = ReviewFiles.FirstOrDefault();

        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        ReviewSummary =
            $"{script.Files.Count} file(s), compiled clean" +
            (warnings > 0 ? $" with {warnings} warning(s)" : string.Empty) +
            ". It runs in-process once registered — read it first.";

        _pendingCompile = result;
        _pendingScript = script;
        ReviewOpen = true;
        Status = "Compiled clean — review the code, then press Register.";
    }

    /// <summary>Step 2 of consent: the actual registration, only reachable from the review overlay.
    /// The installer makes this a real strategy (backtest registry, catalog card, plugin on disk);
    /// without one (Basic, tests) it falls back to the backtest registry alone.</summary>
    [RelayCommand]
    private void ConfirmRegister()
    {
        if (_pendingCompile is not { } result || _pendingScript is not { } script)
        {
            CloseReview();
            return;
        }

        var warnings = result.Diagnostics.Count(d => d.Severity == StrategyDiagnosticSeverity.Warning);
        var caveat = warnings > 0 ? $" {warnings} capability warning(s) in Diagnostics." : string.Empty;

        // A sandbox unit goes into the registry that can actually run it. The legacy path below is for
        // IOrderRoutedStrategy, which the catalog reaches through StrategyCatalogEntry — a kernel has
        // no such entry and never will.
        if (result.Unit is { UsesRetiredContract: false } unit)
        {
            Status = RegisterSandboxUnit(unit, script) + caveat;
        }
        else if (_installer is null)
        {
            _registry.Register(result.Option!);
            Status = $"Registered '{result.Option!.DisplayName}' from {script.Files.Count} file(s) — DEV (unsigned).{caveat}";
        }
        else
        {
            var install = _installer.Install(script, result);
            Status = install.Message + caveat;
            _logger.LogInformation(
                "Authored strategy {Id} installed from {Files} file(s): catalog={InCatalog}",
                result.Option!.Id, script.Files.Count, install.InCatalog);
        }

        // The file. Registration put the unit in this session's catalog; the artifact is what makes it
        // survive the session at all — something to back up, send to somebody, or install on a second
        // machine. Written after registration and never allowed to undo it: a packaging failure is worth
        // saying out loud, and is not worth losing a unit that compiled, verified and registered.
        ArtifactPath = null;
        var artifact = AuthoredArtifact.Write(script, result);
        if (artifact.Success)
        {
            ArtifactPath = artifact.Path;
            Status += $" {artifact.Message}";
            _logger.LogInformation("Authored unit {Id} packaged to {Path}", script.Id, artifact.Path);
            Status += " " + Keep(artifact.Path!);
        }
        else
        {
            Status += $" It is registered for this session, but could not be packaged: {artifact.Message}";
            _logger.LogWarning("Authored unit {Id} could not be packaged: {Reason}", script.Id, artifact.Message);
        }

        _registeredBaseline.Clear();
        foreach (var file in script.Files) _registeredBaseline[file.Name] = file.Content;

        IsRegistered = true;
        Append(AuthoringMessage.Tool("Ok", "Registered", Status ?? "The strategy is registered."));
        CloseReview();
        Save();
    }

    /// <summary>
    /// Installs the artifact into the authored-units root so the unit is still there next time.
    ///
    /// <para>The install runs here rather than inside <c>AuthoredArtifact.Write</c>, and the difference
    /// matters. Writing a file is not a decision; putting code where the host will load it at every
    /// future start is. That decision was already made — this only runs after the review gate, where the
    /// user read the diff and pressed Register — and it still goes through the ordinary installer, so
    /// the configured trust policy has its say. A host running Curated refuses an unsigned local build,
    /// which is correct: persistence is not a reason to stop checking.</para>
    /// </summary>
    private string Keep(string artifactPath)
    {
        if (_units is null) return "It will not survive a restart — this edition keeps no unit folder.";

        var root = AuthoredUnitsRoot.Ensure();
        if (root is null) return "It will not survive a restart — the units folder could not be created.";

        var install = _units.Install(artifactPath, root);
        if (!install.Success)
        {
            _logger.LogWarning("Authored unit could not be kept: {Reason}", install.Message);
            return $"It will not survive a restart: {install.Message}";
        }

        return "It will be there after a restart.";
    }

    /// <summary>Where the last registration's artifact was written, or null. Drives the "Show file"
    /// affordance — a file the user is not told about is a file that does not exist to them.</summary>
    [ObservableProperty] private string? _artifactPath;

    /// <summary>Opens the folder holding the artifact, with the file selected.</summary>
    [RelayCommand]
    private void ShowArtifact()
    {
        if (string.IsNullOrWhiteSpace(ArtifactPath) || !File.Exists(ArtifactPath)) return;

        try
        {
            // The shell rather than a path in a status bar: the point is that the user can pick the file
            // up and do something with it.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ArtifactPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reveal {Path}", ArtifactPath);
            Status = $"The artifact is at {ArtifactPath}.";
        }
    }

    /// <summary>
    /// Puts a compiled sandbox unit in the catalog.
    ///
    /// <para>Registration is what turns a verified strategy into a delivered one. Everything upstream —
    /// compile, policy scan, the four verification rungs, the live preview — was true of a unit nobody
    /// could open.</para>
    ///
    /// <para>The two kinds go to their own registries rather than a shared one, because the host runs
    /// them differently: a strategy gets a virtual book and a visualizer does not, and collapsing that
    /// distinction is how a visualizer ends up with a route to trading.</para>
    /// </summary>
    private string RegisterSandboxUnit(AuthoredUnit unit, StrategyScript script)
    {
        var id = string.IsNullOrWhiteSpace(StrategyId) ? unit.Type.Name : StrategyId!;
        var name = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName;

        if (_sink is null)
            return "Compiled and verified, but this edition has nowhere to register it.";

        var message = _sink.Register(unit, id, name);
        _logger.LogInformation(
            "Authored {Kind} {Id} registered from {Files} file(s).", unit.Kind, id, script.Files.Count);
        return message;
    }

    /// <summary>
    /// The multi-agent path: route a turn, show who took it, judge what came back, repeat.
    ///
    /// <para>Every turn is surfaced as it happens rather than at the end. A ten-turn run takes minutes,
    /// and a pane that shows nothing meanwhile reads as a hang — the agents being visible is what makes
    /// the wait legible, and what justifies its cost to the person paying for it.</para>
    /// </summary>
    private async Task RunAgentsAsync(
        AiProviderChoice choice,
        string brief,
        StrategyBuildProfile profile,
        CancellationToken ct)
    {
        if (_compiler is null)
        {
            AiStatus = "This edition has no compiler, so the agents have nothing to verify against.";
            return;
        }

        // The user pressing "Just build it" ends the interview, whatever the model would have done next.
        // Otherwise the escape is only a suggestion, and a model that keeps asking keeps winning.
        if (AuthoringAction.EndsTheInterview(brief))
            _agentState = _agentState with { HasSpec = true };

        var judge = new AuthoringJudge(
            _compiler,
            StrategyId ?? "authored",
            string.IsNullOrWhiteSpace(DisplayName) ? StrategyId ?? "Authored" : DisplayName!,
            _agentState);

        // The session composes this run's system prompt AND owns the provider bound to the picked model
        // and reasoning effort. Both used to be taken raw here: the shared context was the uncomposed
        // pack, and the client was `choice.Client` rather than the one `ResolveClient` rebinds — so the
        // model and the effort the user chose were dropped on exactly the two settings that mean
        // "correctness over cost".
        var session = EnsureSession(choice, profile);

        var loop = new AgentLoop(session.Provider, judge.Judge, _reliability, _trajectory);

        var report = new Progress<AgentTurn>(turn =>
        {
            // The role is the headline. A user watching six agents should be able to see which one is
            // spending their money and on what.
            Append(AuthoringMessage.Tool(
                turn.Reward > 0.5d ? "Ok" : "Info",
                turn.Role.ToString(),
                turn.Files.Count > 0
                    ? $"{turn.Files.Count} file(s) · scored {turn.Reward:0.00}"
                    : "answered without code",

                // Stripped, exactly as on the non-agent path. Without this the agent transcript shows
                // the raw JSON the model used to offer its options, which is the plumbing rather than
                // the answer.
                AuthoringQuestions.StripBlock(turn.Reply) is { Length: > 0 } prose
                    ? prose
                    : turn.Reply));

            // The reply itself, as a message. The chip above says WHO spoke and what it cost; this is
            // what it actually said. Without it an agent run rendered as a column of grey
            // "Interviewer - answered without code" chips with the prose folded away inside each one,
            // so the user could not read the question they were being asked and answered blind. The
            // single-conversation path has always appended the reply; this path never did.
            if (AuthoringQuestions.StripBlock(turn.Reply) is { Length: > 0 } spoken)
                Append(new AuthoringMessage(CodegenRole.Assistant, spoken));

            PushActivity($"{turn.Role}: {(turn.Files.Count > 0 ? "wrote code" : "replied")}");

            // Preview on every compile, half-finished included: the picture arriving is informative, and
            // the preview says so itself when there is nothing yet to show.
            if (judge.Latest?.Unit is { } unit) ShowPreview(unit);
        });

        // The SAME composition the single-conversation path uses, from the same object.
        //
        // This used to be `StrategyContextPack.Load().SystemPrompt` — the generated surface and the
        // conventions, raw. Deep and Max are the two efforts that route here, so the two efforts that
        // buy the largest skill budget (5 packs and 8) were loading none; the model was never told
        // whether it was writing a strategy or a visualizer, making that switch decoration again at
        // the top two settings; it never saw a worked exemplar; and it was never taught the
        // `questions` block that the very next lines of this method parse and render as buttons.
        // The reader had been wired onto this path and the writer had not.
        // WHAT IT IS DOING RIGHT NOW, on the path that had no answer to that question.
        //
        // The checklist the status bar reads is seeded from StrategyBuildSession's activity strings,
        // and this path emits none of them — so a Deep or Max run sat on "Understand brief" from the
        // first second to the last however many agents it went through. The live signal is the ROLE,
        // and only the loop knows it, so the loop reports it as each turn starts.
        var starting = new Progress<AgentRole>(role =>
        {
            WorkingVerb = role switch
            {
                AgentRole.Interviewer => "Working out what to build",
                AgentRole.Quant => "Working out the maths",
                AgentRole.Coder => "Writing the code",
                AgentRole.Painter => "Drawing the panel",
                AgentRole.Fixer => "Fixing the build",
                AgentRole.Reviewer => "Reviewing it",
                _ => role.ToString(),
            };
            StepText = $"turn {_agentTurnsSeen + 1} of {profile.MaxAgentTurns}";
            _agentTurnsSeen++;
        });

        _agentTurnsSeen = 0;

        var run = await loop.RunAsync(
            brief,
            session.PrepareFor(brief),
            _agentState,
            profile.MaxAgentTurns,
            report,
            ct,
            resume: _agentContext,
            starting: starting);

        // What this turn established, so the next one continues it rather than starting over. The spec
        // an Interviewer wrote is the expensive half of a run; discarding it made every answer the user
        // typed the opening line of a fresh interview.
        _agentState = run.FinalState;
        _agentContext = run.Context ?? _agentContext;

        // The code the run produced, into the editor.
        //
        // The agent path never did this. A Coder could write a unit, the judge could compile it, the
        // ladder could pass it and the preview could render it — and the Code tab still showed the
        // empty scaffold, because SetFiles was only ever called on the single-conversation branch. So
        // at Deep and Max there was no way to READ what had been built, let alone edit it, and
        // CurrentScript() would have registered the scaffold instead of the strategy.
        //
        // Taken from the run rather than reported from the progress callback: Progress<T> posts, so a
        // turn that lands after the await would apply out of order or not at all.
        if (run.Turns.LastOrDefault(turn => turn.Files.Count > 0) is { } wrote)
        {
            var prior = Files.ToDictionary(f => f.Name, f => f.Content, StringComparer.OrdinalIgnoreCase);
            SetFiles(wrote.Files);
            _filesEditedByUser = false;
            AppendFileChanges(prior, wrote.Files);
        }

        foreach (var diagnostic in judge.Latest?.Diagnostics ?? [])
            Diagnostics.Add(diagnostic);

        // Keep what this run taught the router. Saved after every run rather than at shutdown, because a
        // terminal is closed by closing it and a crash is exactly the session worth having learned from.
        AgentMemory.Save(_reliability);

        AiStatus = run.Outcome switch
        {
            AgentRunOutcome.Delivered => $"Delivered after {run.Turns.Count} turn(s). Review the preview, then Compile & Register.",
            AgentRunOutcome.AwaitingUser => "The agent is waiting — pick an option above, or write your own reply.",
            AgentRunOutcome.ProviderFailed => $"Provider failed: {run.Error}",

            // Named rather than folded into the budget message, because the two mean opposite things to
            // whoever is paying: the budget running out says "it needed more room", and this says "more
            // room would have bought nothing". Reporting a wall as a budget invites another spend.
            AgentRunOutcome.Stalled =>
                $"Stopped after {run.Turns.Count} turn(s): the last {AgentLoop.StallLimit} repairs got no "
                + $"further up the ladder. Furthest it got: "
                + $"{(judge.State.Compiles ? "it compiles" : "it does not compile")}. "
                + "Read the diagnostics, then tell it what to change — repeating the same turn will not.",

            // Honest rather than encouraging. A brief that could not be satisfied should say what was
            // built and what did not work, not invite another spend on the same wall.
            _ => $"Stopped at the {profile.MaxAgentTurns}-turn budget. "
               + $"Furthest it got: {(judge.State.Compiles ? "it compiles" : "it does not compile yet")}.",
        };

        CompiledOk = judge.Latest is { Success: true };
        AwaitingAnswer = run.Outcome == AgentRunOutcome.AwaitingUser;

        // The agent loop is a SECOND path to "waiting on the user", and it had none of this. Everything
        // built for questions — parsing the options, stripping the block, offering the buttons — lived
        // on the simple-session branch only, so at any effort that routes through agents (Deep and Max
        // both do) a model that emitted a perfect questions block still rendered as raw text with an
        // empty composer underneath. Two paths to one state, and only one of them was finished.
        if (AwaitingAnswer)
        {
            var lastReply = run.Turns.Count > 0 ? run.Turns[^1].Reply : string.Empty;
            var asked = AuthoringQuestions.Parse(lastReply);
            SetQuestions(asked);
            SetActions(AuthoringAction.For(asked.Count > 0));
        }
        else
        {
            SetQuestions([]);
            SetActions([]);
        }

        if (CompiledOk) _pendingCompile = judge.Latest;
    }

    /// <summary>
    /// Compiles and shows the picture, and registers nothing.
    ///
    /// <para>Separate from Compile &amp; Register on purpose. Registration puts a card in the catalog and
    /// is a decision the user consents to after reading a diff; seeing what the thing looks like is not
    /// a decision at all, and should not cost one. Iterating on a picture through a consent dialogue is
    /// how people stop looking at the picture.</para>
    /// </summary>
    [RelayCommand]
    private void CompilePreview()
    {
        Diagnostics.Clear();
        ClearPreview();

        if (string.IsNullOrWhiteSpace(StrategyId))
        {
            Status = "Give the strategy an id before compiling.";
            return;
        }

        StrategyCompileResult result;
        try
        {
            result = _compiler.Compile(CurrentScript());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preview compile threw for {Id}", StrategyId);
            Status = $"Compiler error: {ex.Message}";
            return;
        }

        foreach (var diagnostic in result.Diagnostics) Diagnostics.Add(diagnostic);

        if (!result.Success || result.Unit is null)
        {
            Status = $"Nothing to preview — {result.Errors.Count()} error(s).";
            return;
        }

        ShowPreview(result.Unit);
        Status = PreviewSummary;
    }

    /// <summary>
    /// Opens provider setup, then rebuilds the picker from what setup left behind.
    ///
    /// <para>The rebuild is the part that matters. Provider clients capture their key and endpoint when
    /// they are constructed, so a picker left alone after a key is saved goes on showing "not set up" for
    /// a provider that now works — which reads as the setup window having failed.</para>
    /// </summary>
    [RelayCommand]
    private void OpenProviderSettings()
    {
        if (_providerSettings is null) return;

        _providerSettings.Open();
        RefreshProviders();
    }

    /// <summary>Rebuilds the provider rows from the live client list, keeping the current selection when
    /// it survived.</summary>
    private void RefreshProviders()
    {
        if (_ai is null) return;

        var selectedId = SelectedAiProvider?.ProviderId;

        AiProviders.Clear();
        foreach (var provider in _ai.Providers) AiProviders.Add(new AiProviderChoice(provider));

        SelectedAiProvider = AiProviders.FirstOrDefault(p => p.ProviderId == selectedId)
            ?? AiProviders.FirstOrDefault(p => p.IsAvailable)
            ?? AiProviders.FirstOrDefault();

        OnPropertyChanged(nameof(AiHasProvider));
    }

    /// <summary>Backs out of the review — nothing was registered, the compile result is discarded.</summary>
    [RelayCommand]
    private void CancelReview()
    {
        CloseReview();
        Status = "Review dismissed — the strategy was NOT registered.";
    }

    private void CloseReview()
    {
        ReviewOpen = false;
        ReviewFiles.Clear();
        SelectedReviewFile = null;
        ReviewSummary = null;
        _pendingCompile = null;
        _pendingScript = null;
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────

    private StrategyScript CurrentScript() => new(
        StrategyId.Trim(),
        DisplayName.Trim(),
        [.. Files.Select(f => new StrategyFile(f.Name, f.Content))]);

    private StrategyBuildSession EnsureSession(AiProviderChoice choice, StrategyBuildProfile profile)
    {
        if (_session is not null) return _session;

        var client = ResolveClient(choice) ?? choice.Client;

        // Resume the restored thread exactly once: the model gets back everything it said, so a follow-up
        // ("now tighten the stop") lands on the code it actually wrote rather than on an empty context.
        // The kind is fixed for the session's life, because it shapes the system prompt — switching it
        // mid-thread would leave the model's own earlier replies disagreeing with its instructions.
        // OnAuthoringKindChanged resets the session for exactly that reason.
        _session = _ai!.StartSession(
            client, StrategyId.Trim(), DisplayName.Trim(), _restoredThread, _restoredUsage, profile,
            AuthoringKind);
        _restoredThread = null;
        _restoredUsage = null;
        return _session;
    }

    /// <summary>The selected provider bound to the selected model + effort (the factory rebuilds the
    /// client — a client is immutable in both).</summary>
    private IStrategyCodegenClient? ResolveClient(AiProviderChoice choice) =>
        _ai?.WithSettings(choice.ProviderId, SelectedModel, SelectedEffort);

    private void ResetSession(string? note)
    {
        // The agent run is reset even when there is no _session yet: the two are separate halves of the
        // same conversation, and leaving a stale spec behind is how a new brief inherits an old one.
        _agentState = new RoutingState();
        _agentContext = null;

        if (_session is null) return;
        _session = null;
        if (note is not null && Messages.Count > 0)
            Append(AuthoringMessage.System($"{note} The model won't remember what was said above."));
    }

    private void SetFiles(IReadOnlyList<StrategyFile> files)
    {
        // Code arriving is the other thing worth opening the panel for -- a turn can write files that
        // do not compile, and those are exactly the ones the user needs to see.
        if (files.Count > 0) OpenWorkbench();

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
        AdvanceTasks(step);
    }

    /// <summary>Per-turn "what changed" chips: line counts for every file the model wrote, against what
    /// was in the editor before the turn. Skipped when nothing actually changed (a pure-question turn
    /// re-sending identical files).</summary>
    private void AppendFileChanges(IReadOnlyDictionary<string, string> prior, IReadOnlyList<StrategyFile> files)
    {
        var changes = new List<FileChangeSummary>(files.Count);
        foreach (var file in files)
        {
            var (added, removed) = LineDiff.Count(prior.GetValueOrDefault(file.Name, string.Empty), file.Content);
            if (added > 0 || removed > 0)
                changes.Add(new FileChangeSummary(file.Name, added, removed));
        }

        if (changes.Count > 0) Append(AuthoringMessage.FilesChanged(changes));
    }

    /// <summary>Names an untouched strategy after its first brief: "Fade liquidity sweeps on ES…" ⇒
    /// id <c>fadeLiquiditySweeps</c>, display name = the brief's first clause. Never fires once the
    /// user has typed their own id or name.</summary>
    /// <summary>
    /// Names the session from the user's first message.
    ///
    /// <para>Only while the identity is untouched, so a name the user typed is never overwritten. New
    /// Strategy restores the defaults, which is what lets the SECOND conversation name itself too — it
    /// did not, and the resulting shared id made each new chat save over the last one.</para>
    /// </summary>
    private void DeriveIdentityFrom(string brief)
    {
        if (StrategyId != DefaultStrategyId || DisplayName != DefaultDisplayName) return;

        // The first clause that says something. A brief opens with the REQUEST -- "create me a
        // strategy," -- and naming the session after that produced "createMeStrategy" / "Create me a
        // strategy" for every strategy anyone asked for: three sessions in a row named after the act of
        // asking rather than the thing asked for. Skip those and take the first clause with content.
        var clause = Clauses(brief).FirstOrDefault(c => !IsRequestFiller(c));
        if (string.IsNullOrWhiteSpace(clause)) return;

        if (clause.Length > 60)
        {
            var cut = clause.LastIndexOf(' ', 60);
            clause = clause[..(cut > 20 ? cut : 60)].TrimEnd() + "…";
        }

        var words = clause
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 1 && !IdStopWords.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        if (words.Length == 0) return;

        StrategyId = string.Concat(words.Select((w, i) => i == 0
            ? w.ToLowerInvariant()
            : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

        DisplayName = char.ToUpperInvariant(clause[0]) + clause[1..];
    }

    private static IEnumerable<string> Clauses(string brief) => brief
        .ReplaceLineEndings("\n")
        .Split(['\n', '.', ';', ':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(c => c.Length > 0);

    /// <summary>
    /// True for a clause that is only the act of asking — "create me a strategy", "build a visualizer",
    /// "can you make me an indicator". Nothing in one of these describes what is being built.
    /// </summary>
    private static bool IsRequestFiller(string clause)
    {
        var words = clause
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToArray();

        return words.Length > 0
            && words.Length <= 6
            && words.All(w => FillerWords.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    private static readonly string[] FillerWords =
    [
        "please", "can", "could", "you", "i", "we", "me", "us", "my", "a", "an", "the",
        "create", "build", "make", "write", "generate", "give", "design", "code", "develop",
        "want", "need", "would", "like", "help", "new", "some",
        "strategy", "visualizer", "visualiser", "indicator", "tool", "window", "unit", "panel",
    ];

    /// <summary>
    /// Words that carry no identity. Beyond articles and prepositions this drops pronouns and the
    /// generic verbs a brief opens with, so the id lands on the nouns that distinguish one strategy
    /// from another rather than on "we take" or "it shows".
    /// </summary>
    private static readonly string[] IdStopWords =
    [
        "a", "an", "the", "on", "in", "at", "of", "to", "for", "with", "and", "or", "that", "when",
        "using", "from", "by", "as", "into", "over", "per", "its", "our",
        "i", "we", "you", "it", "me", "us", "my", "this", "these", "those", "there",
        "take", "takes", "use", "uses", "show", "shows", "build", "builds", "make", "makes",
        "create", "creates", "add", "adds", "get", "gets", "put", "puts", "want", "need",
        "strategy", "visualizer", "visualiser", "indicator",
    ];

    /// <summary>Chat → snapshot. Rich kinds flatten into the entry's optional fields; the expandable
    /// tool output is intentionally dropped (summaries restore, transcripts don't bloat).</summary>
    private static AuthoringChatEntry ToChatEntry(AuthoringMessage m) => m.Kind switch
    {
        AuthoringMessage.KindTool => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.ToolTitle ?? string.Empty, m.TimestampLocal,
            Kind: m.Kind, State: m.ToolState, Detail: m.ToolDetail),
        AuthoringMessage.KindPlan or AuthoringMessage.KindPlanText => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.PlanSnapshotText(), m.TimestampLocal, Kind: AuthoringMessage.KindPlanText),
        AuthoringMessage.KindFiles => new AuthoringChatEntry(
            AuthoringChatEntry.System, m.Text, m.TimestampLocal,
            Kind: m.Kind, Detail: FileChangeSummary.Pack(m.FileChanges ?? [])),
        _ => new AuthoringChatEntry(
            m.IsSystem ? AuthoringChatEntry.System
                : m.IsUser ? AuthoringChatEntry.User : AuthoringChatEntry.Assistant,
            m.Text, m.TimestampLocal),
    };

    /// <summary>Snapshot → chat. Entries from pre-redesign files carry no Kind and restore exactly as
    /// they always did.</summary>
    private static AuthoringMessage FromChatEntry(AuthoringChatEntry entry) => entry.Kind switch
    {
        AuthoringMessage.KindTool => AuthoringMessage.Tool(entry.State ?? "Info", entry.Text, entry.Detail ?? string.Empty),
        AuthoringMessage.KindPlanText => AuthoringMessage.PlanText(entry.Text),
        AuthoringMessage.KindFiles when FileChangeSummary.Unpack(entry.Detail) is { Count: > 0 } changes =>
            AuthoringMessage.FilesChanged(changes),
        AuthoringMessage.KindFiles => AuthoringMessage.System(entry.Text),
        _ => entry.Role == AuthoringChatEntry.System
            ? AuthoringMessage.System(entry.Text)
            : new AuthoringMessage(
                entry.Role == AuthoringChatEntry.User ? CodegenRole.User : CodegenRole.Assistant,
                entry.Text),
    };

    private void PersistSelection(string providerId, string? model, CodegenEffort effort)
    {
        try
        {
            AiCodegenUserFile.SaveSelection(providerId, model, effort, _options, BuildEffort.Wire());
        }
        catch (Exception ex)
        {
            // A read-only profile shouldn't break the builder — the choice just won't survive a restart.
            _logger.LogWarning(ex, "Could not persist the AI provider/model choice");
        }
    }

    public void Dispose()
    {
        // Hand-edits in the Code tab aren't saved per keystroke; catch them on the way out.
        Save();

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
        // Rules: define exactly ONE public class implementing IOrderRoutedStrategy with a
        // public (Contract) constructor. Optionally add a static Schema and a static
        // Create(Contract, StrategyParameters) to expose tunable parameters in the UI.
        // Helpers may live in additional files (the + button on the file list).

        public sealed class MyStrategy : IOrderRoutedStrategy
        {
            public static StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
                StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1));

            public static IOrderRoutedStrategy Create(Contract contract, StrategyParameters p) =>
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

/// <summary>
/// One element of the agent-workspace transcript. <see cref="Kind"/> is a string (not an enum) on
/// purpose: the shared XAML templates live in TradingTerminal.UI, which cannot reference this
/// assembly, so every template trigger is duck-typed against these values:
/// <c>User</c> / <c>Assistant</c> / <c>Note</c> (a builder aside) / <c>Tool</c> (a one-line action
/// card) / <c>Thinking</c> (the model's reasoning channel, collapsed) / <c>Plan</c> and
/// <c>PlanText</c> (the retired turn checklist — kept so saved sessions still load) /
/// <c>Files</c> (per-file change chips).
/// </summary>
public sealed partial class AuthoringMessage : ObservableObject
{
    public const string KindUser = "User";
    public const string KindAssistant = "Assistant";
    public const string KindNote = "Note";
    public const string KindTool = "Tool";
    public const string KindThinking = "Thinking";

    /// <summary>The turn checklist, retired. Nothing produces these any more — the transcript is a
    /// conversation, not a project plan — but the constants and their templates stay so a session saved
    /// before the change still deserializes and renders instead of throwing.</summary>
    public const string KindPlan = "Plan";
    public const string KindPlanText = "PlanText";
    public const string KindFiles = "Files";

    public AuthoringMessage(CodegenRole role, string text)
    {
        Role = role;
        Kind = role == CodegenRole.User ? KindUser : KindAssistant;
        _text = text;
    }

    private AuthoringMessage(string kind, string text)
    {
        Role = CodegenRole.Assistant;
        IsSystem = kind is not (KindUser or KindAssistant);
        Kind = kind;
        _text = text;
    }

    /// <summary>A builder-generated note, styled apart from the model's own words.</summary>
    public static AuthoringMessage System(string? text) => new(KindNote, text ?? string.Empty);

    /// <summary>An action card: <paramref name="state"/> is "Ok" / "Fail" / "Run" / "Info" (duck-typed
    /// by the templates), <paramref name="detail"/> the numbers worth reading at a glance,
    /// <paramref name="more"/> the expandable full output.</summary>
    public static AuthoringMessage Tool(string state, string title, string detail, string? more = null) =>
        new(KindTool, title)
        {
            ToolState = state,
            ToolTitle = title,
            ToolDetail = detail,
            ToolMore = string.IsNullOrWhiteSpace(more) ? null : more,
        };

    /// <summary>
    /// The model's thinking for this turn, as one growing block.
    ///
    /// <para>One message per turn rather than one per fragment: a reasoning model emits thousands of
    /// tiny deltas, and appending each as its own transcript row would bury the conversation under its
    /// own footnotes. The view renders it collapsed, so it costs a single line until somebody opens
    /// it.</para>
    /// </summary>
    public static AuthoringMessage Thinking(string text) => new(KindThinking, text);

    /// <summary>The turn's live checklist — retired; see <see cref="KindPlan"/>. Kept so a restored
    /// session can still rebuild one.</summary>
    public static AuthoringMessage Plan(IReadOnlyList<BuildTask> tasks) =>
        new(KindPlan, string.Empty) { PlanTasks = tasks };

    /// <summary>A plan restored from disk — glyph lines, no live states.</summary>
    public static AuthoringMessage PlanText(string text) => new(KindPlanText, text);

    public static AuthoringMessage FilesChanged(IReadOnlyList<FileChangeSummary> changes) =>
        new(KindFiles, string.Join(" · ", changes.Select(c => $"{c.Name} {c.Counts}")))
        {
            FileChanges = changes,
        };

    public CodegenRole Role { get; }
    public bool IsSystem { get; }
    public string Kind { get; }
    public bool IsUser => !IsSystem && Role == CodegenRole.User;
    public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;

    public string? ToolState { get; private init; }
    public string? ToolTitle { get; private init; }
    public string? ToolDetail { get; private init; }
    public string? ToolMore { get; private init; }
    public bool HasMore => !string.IsNullOrEmpty(ToolMore);

    public IReadOnlyList<BuildTask>? PlanTasks { get; private init; }
    public IReadOnlyList<FileChangeSummary>? FileChanges { get; private init; }

    /// <summary>The live plan flattened to glyph lines for persistence (and for a restored render).</summary>
    public string PlanSnapshotText() => PlanTasks is null
        ? Text
        : string.Join("\n", PlanTasks.Select(t => t.State switch
        {
            BuildTaskState.Done => $"✓ {t.Title}",
            BuildTaskState.Failed => $"✕ {t.Title}",
            BuildTaskState.Running => $"◐ {t.Title}",
            _ => $"○ {t.Title}",
        }));

    /// <summary>Observable so streaming can grow the bubble token by token.</summary>
    [ObservableProperty] private string _text;

    public DateTime TimestampLocal { get; } = DateTime.Now;
}

/// <summary>One file's change counts for the per-turn chips ("SweepDetector.cs +64 −8").</summary>
public sealed record FileChangeSummary(string Name, int Added, int Removed)
{
    public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";

    /// <summary>Machine form for the session snapshot ("name|added|removed;…").</summary>
    public static string Pack(IReadOnlyList<FileChangeSummary> changes) =>
        string.Join(";", changes.Select(c => $"{c.Name}|{c.Added}|{c.Removed}"));

    public static IReadOnlyList<FileChangeSummary>? Unpack(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed)) return null;

        var changes = new List<FileChangeSummary>();
        foreach (var part in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split('|');
            if (fields.Length == 3 && int.TryParse(fields[1], out var added) && int.TryParse(fields[2], out var removed))
                changes.Add(new FileChangeSummary(fields[0], added, removed));
        }

        return changes.Count > 0 ? changes : null;
    }
}

/// <summary>One file in the review overlay: its full diff against the last registered content, plus
/// the +/− counts for the file strip.</summary>
public sealed class ReviewFileEntry(string name, IReadOnlyList<DiffLine> lines)
{
    public string Name { get; } = name;
    public IReadOnlyList<DiffLine> Lines { get; } = lines;
    public int Added { get; } = lines.Count(l => l.Kind == "add");
    public int Removed { get; } = lines.Count(l => l.Kind == "del");
    public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
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

/// <summary>Where one step of the build pipeline stands.</summary>
public enum BuildTaskState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>One row of the builder's Tasks strip — a pipeline step ("Generate", "Compile", "Backtest
/// smoke") whose <see cref="State"/> advances live as the turn's activity stream arrives.</summary>
public sealed partial class BuildTask(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty] private BuildTaskState _state = BuildTaskState.Pending;
}
