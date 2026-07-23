# TradingTerminal.Settings — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/UI/TradingTerminal.Settings/Archive/ArchiveActivityViewModel.cs
```cs
   15: public sealed partial class ArchiveActivityViewModel : ViewModelBase
   20: public ArchiveActivityViewModel(
   31: public ObservableCollection<ArchiveRow> Rows { get; }
   34: public ObservableCollection<CoverageRow> Coverage { get; }
   43: public bool HasPending => PendingCount > 0;
   47: public async Task RefreshAsync()
   78: public async Task InstantOffloadAsync()
  126: public sealed class ArchiveRow
  128: public required ArchiveManifestEntry Entry { get; init; }
  130: public long Id => Entry.Id;
  131: public string PeriodLabel => Entry.PeriodLabel;
  132: public string Range => $"{Entry.FromUtc:yyyy-MM-dd} → {Entry.ToUtc:yyyy-MM-dd}";
  133: public int Parts => Entry.Parts.Count;
  134: public string TotalBytesPretty => Fmt(Entry.TotalBytes);
  135: public string Target => Entry.Target.IsSavedMessages ? "Saved Messages" : (Entry.Target.ChatRef ?? "(unknown)");
  136: public long RowsQuotes => Entry.RowsQuotes;
  137: public long RowsBars => Entry.RowsBars;
  138: public string Uploaded => Entry.UploadedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
  139: public string LocalDeleted => Entry.DeletedLocal ? "yes" : "no";
  141: public static ArchiveRow From(ArchiveManifestEntry e) => new() { Entry = e };
  152: public sealed class CoverageRow
  155: public CoverageRow(ArchiveCoverageWindow w) => _w = w;
  157: public string PeriodLabel => _w.PeriodLabel;
  158: public string Range => $"{_w.FromUtc:yyyy-MM-dd} → {_w.ToUtc:yyyy-MM-dd}";
  159: public bool Offloaded => _w.Offloaded;
  160: public string Status => _w.Offloaded ? "Offloaded" : "Pending";
  161: public string ArchiveRef => _w.ArchiveId is { } id ? $"#{id}" : "—";
```

## src/windows/UI/TradingTerminal.Settings/Archive/ArchiveSettingsViewModel.cs
```cs
   18: public sealed partial class ArchiveSettingsViewModel : ViewModelBase
   26: public ArchiveSettingsViewModel(
   68: public bool DefaultTargetIsChat => string.Equals(DefaultTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
   76: public bool ManualTargetIsChat => string.Equals(ManualTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
   83: public IReadOnlyList<string> PeriodOptions { get; } = new[] { "Weekly", "Monthly" };
   84: public IReadOnlyList<string> TargetKindOptions { get; } = new[] { "saved", "chat" };
```

## src/windows/UI/TradingTerminal.Settings/Archive/ArchiveUserFile.cs
```cs
   14: public static class ArchiveUserFile
   16: public static string Path { get; } = System.IO.Path.Combine(
   20: public static void Save(ArchiveOptions archive, TelegramArchiveOptions telegram)
```

## src/windows/UI/TradingTerminal.Settings/Archive/TelegramArchiveCredentialProtection.cs
```cs
   14: public static class TelegramArchiveCredentialProtection
   16: public static string? Encrypt(string? plaintext)
   31: public static string? Decrypt(string? cipherBase64)
```

## src/windows/UI/TradingTerminal.Settings/Authoring/AiCodegenUserFile.cs
```cs
   15: public static class AiCodegenUserFile
   19: public static string Path { get; } = System.IO.Path.Combine(
   31: public static void SaveSelection(
```

## src/windows/UI/TradingTerminal.Settings/Authoring/AiProvidersSettingsViewModel.cs
```cs
   16: public sealed partial class AiProvidersSettingsViewModel : ViewModelBase
   20: public AiProvidersSettingsViewModel(IAiStrategyBuilder? builder = null, IAiKeyStore? keys = null)
   33: public ObservableCollection<AiProviderRow> Providers { get; }
   69: public sealed partial class AiProviderRow : ObservableObject
   73: public AiProviderRow(IStrategyCodegenClient client, IAiKeyStore? keys)
   81: public string ProviderId => _client.ProviderId;
   82: public string DisplayName => _client.DisplayName;
   83: public bool IsAvailable => _client.IsAvailable;
   84: public bool NeedsKey { get; }
   91: public string StatusText => IsAvailable
   95: public void MarkStored(bool stored)
```

## src/windows/UI/TradingTerminal.Settings/Authoring/AuthoringSessionStore.cs
```cs
   11: public sealed record AuthoringChatEntry(
   21: public const string User = "user";
   22: public const string Assistant = "assistant";
   23: public const string System = "system";
   31: public sealed record AuthoringSessionSnapshot(
   47: public string Age
   59: public string Label => $"{DisplayName} ({StrategyId}) · {Age}";
   72: public static class AuthoringSessionStore
   80: public static string Directory { get; } = Path.Combine(
   87: public static bool Save(AuthoringSessionSnapshot session)
  108: public static IReadOnlyList<AuthoringSessionSnapshot> List()
  121: public static AuthoringSessionSnapshot? Load(string strategyId) =>
  124: public static void Delete(string strategyId)
```

## src/windows/UI/TradingTerminal.Settings/Authoring/LineDiff.cs
```cs
    5: public sealed record DiffLine(string Kind, string Text);
   14: public static class LineDiff
   19: public static (int Added, int Removed) Count(string before, string after)
   34: public static IReadOnlyList<DiffLine> Build(string before, string after)
```

## src/windows/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs
```cs
   35: public sealed partial class StrategyAuthoringViewModel : ViewModelBase, IDisposable
   66: public StrategyAuthoringViewModel(
  122: public bool AiEnabled => _ai is not null;
  123: public bool AiHasProvider => AiProviders.Any(p => p.IsAvailable);
  127: public bool HasConversation => Messages.Count > 0;
  134: public IReadOnlyList<string> SuggestionBriefs { get; } =
  186: public ObservableCollection<StrategyDiagnostic> Diagnostics { get; }
  201: public ObservableCollection<AuthoredFile> Files { get; }
  228: public ObservableCollection<AiProviderChoice> AiProviders { get; }
  234: public ObservableCollection<string> Models { get; } = [];
  241: public IReadOnlyList<CodegenEffort> Efforts { get; } =
  248: public bool EffortSupported => SelectedAiProvider is { } choice && AiModelCatalog.SupportsEffort(choice.ProviderId);
  278: public string ModelPillText =>
  302: public ObservableCollection<AiModelChoice> AllModels { get; }
  370: public IReadOnlyList<StrategyBuildEffort> BuildEfforts { get; } =
  390: public IReadOnlyList<AgentCliAdapter> AvailableClis => _cliLauncher?.AvailableClis() ?? [];
  459: public ObservableCollection<AuthoringMessage> Messages { get; }
  463: public ObservableCollection<string> Activity { get; }
  472: public ObservableCollection<BuildTask> Tasks { get; }
  685: public string UsageText => InputTokens + OutputTokens == 0
  961: public ObservableCollection<AuthoringSessionSnapshot> SavedSessions { get; } = [];
 1093: public ObservableCollection<ReviewFileEntry> ReviewFiles { get; } = [];
 1415: public void Dispose()
 1439: public sealed class MyStrategy : IBacktestStrategy
 1441: public static StrategyParameterSchema Schema { get; } = new(
 1445: public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
 1452: public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }
 1454: public MyStrategy(Contract contract, int lookback, double threshold)
 1461: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 1464: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
 1472: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
 1474: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 1482: public sealed partial class AuthoredFile(string name, string content) : ObservableObject
 1496: public sealed partial class AuthoringMessage : ObservableObject
 1498: public const string KindUser = "User";
 1499: public const string KindAssistant = "Assistant";
 1500: public const string KindNote = "Note";
 1501: public const string KindTool = "Tool";
 1502: public const string KindPlan = "Plan";
 1503: public const string KindPlanText = "PlanText";
 1504: public const string KindFiles = "Files";
 1506: public AuthoringMessage(CodegenRole role, string text)
 1522: public static AuthoringMessage System(string? text) => new(KindNote, text ?? string.Empty);
 1527: public static AuthoringMessage Tool(string state, string title, string detail, string? more = null) =>
 1538: public static AuthoringMessage Plan(IReadOnlyList<BuildTask> tasks) =>
 1542: public static AuthoringMessage PlanText(string text) => new(KindPlanText, text);
 1544: public static AuthoringMessage FilesChanged(IReadOnlyList<FileChangeSummary> changes) =>
 1550: public CodegenRole Role { get; }
 1551: public bool IsSystem { get; }
 1552: public string Kind { get; }
 1553: public bool IsUser => !IsSystem && Role == CodegenRole.User;
 1554: public bool IsAssistant => !IsSystem && Role == CodegenRole.Assistant;
 1556: public string? ToolState { get; private init; }
 1557: public string? ToolTitle { get; private init; }
 1558: public string? ToolDetail { get; private init; }
 1559: public string? ToolMore { get; private init; }
 1560: public bool HasMore => !string.IsNullOrEmpty(ToolMore);
 1562: public IReadOnlyList<BuildTask>? PlanTasks { get; private init; }
 1563: public IReadOnlyList<FileChangeSummary>? FileChanges { get; private init; }
 1566: public string PlanSnapshotText() => PlanTasks is null
 1579: public DateTime TimestampLocal { get; } = DateTime.Now;
 1583: public sealed record FileChangeSummary(string Name, int Added, int Removed)
 1585: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 1588: public static string Pack(IReadOnlyList<FileChangeSummary> changes) =>
 1591: public static IReadOnlyList<FileChangeSummary>? Unpack(string? packed)
 1609: public sealed class ReviewFileEntry(string name, IReadOnlyList<DiffLine> lines)
 1611: public string Name { get; } = name;
 1612: public IReadOnlyList<DiffLine> Lines { get; } = lines;
 1613: public int Added { get; } = lines.Count(l => l.Kind == "add");
 1614: public int Removed { get; } = lines.Count(l => l.Kind == "del");
 1615: public string Counts => Removed > 0 ? $"+{Added} −{Removed}" : $"+{Added}";
 1620: public sealed class AiProviderChoice(IStrategyCodegenClient client)
 1622: public IStrategyCodegenClient Client { get; } = client;
 1623: public string ProviderId => Client.ProviderId;
 1624: public string DisplayName => Client.DisplayName;
 1625: public bool IsAvailable => Client.IsAvailable;
 1626: public string Label => IsAvailable ? DisplayName : $"{DisplayName} — not set up";
 1630: public enum BuildTaskState
 1640: public sealed partial class BuildTask(string title) : ObservableObject
 1642: public string Title { get; } = title;
```

## src/windows/UI/TradingTerminal.Settings/Notifications/NotificationsSettingsViewModel.cs
```cs
   17: public sealed partial class NotificationsSettingsViewModel : ViewModelBase
   23: public NotificationsSettingsViewModel(
   61: public IReadOnlyList<string> AiAnalystProviders { get; } =
```

## src/windows/UI/TradingTerminal.Settings/Notifications/NotificationsUserFile.cs
```cs
   13: public static class NotificationsUserFile
   19: public static string Path { get; } = System.IO.Path.Combine(
   25: public static void Save(NotificationsOptions options)
```

## src/windows/UI/TradingTerminal.Settings/Research/ResearchSettingsViewModel.cs
```cs
   17: public sealed partial class ResearchSettingsViewModel : ViewModelBase
   23: public ResearchSettingsViewModel(
```

## src/windows/UI/TradingTerminal.Settings/Research/ResearchUserFile.cs
```cs
   14: public static class ResearchUserFile
   18: public static string Path { get; } = System.IO.Path.Combine(
   25: public static void Save(ResearchReproOptions options, bool autoLaunchSidecar, int sidecarPort)
```

## src/windows/UI/TradingTerminal.Settings/Support/SupportInfo.cs
```cs
   15: public const string DeveloperEmail = "dhruvsha.info@gmail.com";
   17: public const string ProductName = "DaxAlgo Terminal";
   19: public const string GitHubUrl = "https://github.com/dhruuvsharma/DaxAlgo-Terminal";
   23: public static string DisplayVersion
```

## src/windows/UI/TradingTerminal.Settings/Support/SupportViewModel.cs
```cs
   19: public sealed partial class SupportViewModel : ViewModelBase
   23: public SupportViewModel(ILogger<SupportViewModel> logger)
   28: public string ProductName => SupportInfo.ProductName;
   30: public string Version => SupportInfo.DisplayVersion;
   32: public string DeveloperEmail => SupportInfo.DeveloperEmail;
   34: public string ThankYouMessage =>
   39: public string DonateMessage =>
   53: public event EventHandler? CloseRequested;
```
