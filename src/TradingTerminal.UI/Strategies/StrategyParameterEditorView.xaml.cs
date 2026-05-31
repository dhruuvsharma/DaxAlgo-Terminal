using System.Windows.Controls;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Auto-generated parameter editor. Set its <c>DataContext</c> to a
/// <see cref="StrategyParametersViewModel"/> and it renders one control per declared
/// tunable. The kind→template mapping is wired here in code-behind (rather than as a XAML
/// resource) so the view references no same-assembly custom types in markup — keeping the
/// WPF markup compiler happy regardless of build ordering.
/// </summary>
public partial class StrategyParameterEditorView : UserControl
{
    public StrategyParameterEditorView()
    {
        InitializeComponent();

        ItemsHost.ItemTemplateSelector = new ParameterTemplateSelector
        {
            NumberTemplate = (System.Windows.DataTemplate)Resources["NumberParameterTemplate"],
            BooleanTemplate = (System.Windows.DataTemplate)Resources["BooleanParameterTemplate"],
            ChoiceTemplate = (System.Windows.DataTemplate)Resources["ChoiceParameterTemplate"],
            TextTemplate = (System.Windows.DataTemplate)Resources["TextParameterTemplate"],
        };
    }
}
