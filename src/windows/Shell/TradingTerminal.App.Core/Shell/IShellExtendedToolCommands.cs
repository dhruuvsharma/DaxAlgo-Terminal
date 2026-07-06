namespace TradingTerminal.App.Shell;

/// <summary>
/// Marker seam for a shell edition's tier-exclusive launch commands. The Professional shell registers
/// an implementation (holding the Pro-only <c>Open…Command</c>s — LSE backtester, ML windows, AI panels,
/// QuantConnect, Surface Lab, experimental bubble chart); Basic/Intermediate register none, so
/// <c>MainWindowViewModel.ExtendedTools</c> resolves to <c>null</c> and those menu items simply don't bind.
/// Implementations reuse <see cref="IShellWindowHost"/> for single-instance window behaviour.
/// </summary>
public interface IShellExtendedToolCommands
{
}
