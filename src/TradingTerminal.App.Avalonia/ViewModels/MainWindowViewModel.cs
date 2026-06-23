using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Avalonia.ViewModels;

/// <summary>
/// Proof-of-foundation VM for the cross-platform shell. It is a plain
/// <see cref="ObservableObject"/> (same MVVM toolkit as the WPF VMs) and pulls a real type
/// (<see cref="BrokerKind"/>) from the portable headless core — demonstrating that the Avalonia
/// UI runs on top of TradingTerminal.Core on Linux/Windows/Pi. Later iterations replace this with
/// the ported shell VMs (strategy catalog + Activity Log) reused from the WPF side.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public string Greeting => "DaxAlgo Terminal — Avalonia shell (Linux port, Phase 1)";

    public string RuntimeInfo =>
        $"{RuntimeInformation.OSDescription}  ·  {RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    public IReadOnlyList<string> Brokers { get; } = Enum.GetNames<BrokerKind>();

    public string CoreStatus => $"Headless core loaded — {Brokers.Count} broker kinds available.";
}
