# TradingTerminal.Ai.PaperLab — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/AI/TradingTerminal.Ai.PaperLab/AvaloniaUi/PaperLabAvaloniaWindow.axaml.cs
```cs
    8: public partial class PaperLabAvaloniaWindow : Window
   10: public PaperLabAvaloniaWindow() => InitializeComponent();
```

## src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabServiceCollectionExtensions.cs
```cs
   12: public static class PaperLabServiceCollectionExtensions
   16: public static IServiceCollection AddPaperLab(this IServiceCollection services)
```

## src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabView.xaml.cs
```cs
    7: public partial class PaperLabView : UserControl
    9: public PaperLabView()
```

## src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabViewModel.cs
```cs
   34: public sealed partial class PaperLabViewModel : ViewModelBase, IDisposable
   63: public bool IsBusy => IsResolving || IsSubmitting || IsRegistering;
   85: public bool HasConfidence => ConfidenceScore > 0;
   88: public ObservableCollection<ReplicationConfidenceComponentViewModel> ConfidenceComponents { get; } = new();
   97: public ObservableCollection<RepoRef> CandidateRepos { get; } = new();
   98: public ObservableCollection<ReproJobRowViewModel> Jobs { get; } = new();
  105: public PaperLabViewModel(
  151: public bool IsAvailable => _ingestClient.IsAvailable;
  160: public async Task ResolveAsync()
  232: public async Task SubmitAsync()
  283: public async Task CancelJobAsync(Guid jobId)
  298: public void CancelResolve() => _resolveCts?.Cancel();
  314: public async Task SaveAsStrategyAsync(ReproJobRowViewModel row)
  458: public void Dispose()
```

## src/linux/AI/TradingTerminal.Ai.PaperLab/ReproJobRowViewModel.cs
```cs
   12: public sealed partial class ReproJobRowViewModel : ViewModelBase
   14: public Guid JobId { get; }
   39: public bool CanSaveAsStrategy => Status == ReproStatus.Succeeded && Result is not null;
   41: public ReproJobRowViewModel(ReproJob job)
   48: public void Update(ReproJob job)
   72: public string StatusLabel => Status switch
   96: public sealed class ReplicationConfidenceComponentViewModel
   99: public string Name { get; }
  102: public double Score { get; }
  105: public string ScoreLabel { get; }
  107: public ReplicationConfidenceComponentViewModel(string name, double score)
```
