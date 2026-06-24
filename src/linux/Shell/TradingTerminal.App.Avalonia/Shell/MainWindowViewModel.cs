using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI.Catalog;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>
/// Root VM for the cross-platform shell. Plain <see cref="ObservableObject"/> (same MVVM toolkit as
/// the WPF VMs), now resolved from DI (see <c>Composition.ServiceConfiguration</c>) with the shared
/// portable types injected: the universal Activity Log (<see cref="InMemoryLogSink"/>) and the
/// strategy catalog VM — both reused unchanged from the WPF side. A parameterless ctor remains so
/// the XAML designer can still instantiate it.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(StrategyCatalogViewModel catalog, InMemoryLogSink activityLog)
    {
        Catalog = catalog;
        ActivityLog = activityLog;

        ActivityLog.Append("Avalonia", "INFO", "Cross-platform shell started on the portable core (DI).");
        ActivityLog.Append("Avalonia", "INFO", $"{Brokers.Count} broker kinds discovered from TradingTerminal.Core.");
        ActivityLog.Append("Avalonia", "INFO", $"{Catalog.Count} strategies loaded from the headless catalog.");
    }

    /// <summary>Design-time ctor: builds a self-contained graph so the previewer has data.</summary>
    public MainWindowViewModel()
        : this(
            new StrategyCatalogViewModel(TradingTerminal.Infrastructure.Backtest.BacktestStrategyCatalog.All),
            new InMemoryLogSink())
    {
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
