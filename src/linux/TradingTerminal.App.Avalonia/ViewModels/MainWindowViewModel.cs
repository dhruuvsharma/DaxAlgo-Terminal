using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI.Catalog;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia.ViewModels;

/// <summary>
/// Proof-of-foundation VM for the cross-platform shell. It is a plain
/// <see cref="ObservableObject"/> (same MVVM toolkit as the WPF VMs) and reuses real shared types
/// from the portable core: <see cref="BrokerKind"/> (TradingTerminal.Core) and
/// <see cref="InMemoryLogSink"/> — the very same universal Activity Log the WPF shell uses, now
/// running unchanged on Avalonia. Later iterations replace this with the ported shell VMs
/// (strategy catalog + Activity Log pane) reused from the WPF side.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        ActivityLog = new InMemoryLogSink();

        // Real strategy catalog from the headless layer (Infrastructure), driven by the portable
        // StrategyCatalogViewModel shared with the WPF shell. Selection is routed to the Activity Log.
        Catalog = new StrategyCatalogViewModel(
            BacktestStrategyCatalog.All,
            msg => ActivityLog.Append("Catalog", "INFO", msg));

        ActivityLog.Append("Avalonia", "INFO", "Cross-platform shell started on the portable core.");
        ActivityLog.Append("Avalonia", "INFO", $"{Brokers.Count} broker kinds discovered from TradingTerminal.Core.");
        ActivityLog.Append("Avalonia", "INFO", $"{Catalog.Count} strategies loaded from the headless catalog.");
    }

    public string Greeting => "DaxAlgo Terminal — Avalonia shell (Linux port, Phase 1)";

    public string RuntimeInfo =>
        $"{RuntimeInformation.OSDescription}  ·  {RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    public IReadOnlyList<string> Brokers { get; } = Enum.GetNames<BrokerKind>();

    public string CoreStatus => $"Headless core loaded — {Brokers.Count} broker kinds available.";

    /// <summary>Strategy catalog VM, shared (portable) with the WPF shell.</summary>
    public StrategyCatalogViewModel Catalog { get; }

    /// <summary>The universal Activity Log — the same WPF-free sink the WPF shell binds to.</summary>
    public InMemoryLogSink ActivityLog { get; }
}
