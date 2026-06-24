using System.Windows;

namespace TradingTerminal.App.Support;

/// <summary>
/// Shows the "Support the developer" window. Centralises the once-per-launch nag policy and the
/// single-instance window handling so both the auto-popup (composition root) and the Help menu
/// (main-window VM) route through one place.
/// </summary>
public interface ISupportPrompt
{
    /// <summary>Called once after the main window appears. Honours the once-per-launch + random gate
    /// and shows the window after a short randomised delay. A no-op if it has already fired this
    /// launch or the random roll declines.</summary>
    void MaybeShowOnLaunch(Window owner);

    /// <summary>Unconditionally shows (or re-activates) the window — used by the Help menu.</summary>
    void Show(Window owner);
}
