using System.Windows.Controls;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Strategy authoring pane: a C# editor, a Compile &amp; Register button, a diagnostics
/// list, and the auto-generated parameter editor. No code-behind logic — all behaviour is
/// in <see cref="StrategyAuthoringViewModel"/>.
/// </summary>
public partial class StrategyAuthoringView : UserControl
{
    public StrategyAuthoringView()
    {
        InitializeComponent();
    }
}
