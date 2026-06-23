using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
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
        ActivityLog.Append("Avalonia", "INFO", "Cross-platform shell started on the portable core.");
        ActivityLog.Append("Avalonia", "INFO", $"{Brokers.Count} broker kinds discovered from TradingTerminal.Core.");
        ActivityLog.Append("Avalonia", "INFO", "InMemoryLogSink (shared with the WPF shell) is live.");
    }

    public string Greeting => "DaxAlgo Terminal — Avalonia shell (Linux port, Phase 1)";

    public string RuntimeInfo =>
        $"{RuntimeInformation.OSDescription}  ·  {RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    public IReadOnlyList<string> Brokers { get; } = Enum.GetNames<BrokerKind>();

    public string CoreStatus => $"Headless core loaded — {Brokers.Count} broker kinds available.";

    /// <summary>The universal Activity Log — the same WPF-free sink the WPF shell binds to.</summary>
    public InMemoryLogSink ActivityLog { get; }
}
