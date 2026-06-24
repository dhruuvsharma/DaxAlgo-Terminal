using System.Windows;
using System.Windows.Controls;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Chooses the editor <see cref="DataTemplate"/> for a <see cref="ParameterEditorItem"/>
/// by its <see cref="ParameterKind"/>. The four templates are supplied from XAML so the
/// look stays themeable; the integer kind reuses the number template (both bind
/// <c>NumberValue</c>).
/// </summary>
public sealed class ParameterTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NumberTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is not ParameterEditorItem p
            ? base.SelectTemplate(item, container)
            : p.Kind switch
            {
                ParameterKind.Integer or ParameterKind.Number => NumberTemplate,
                ParameterKind.Boolean => BooleanTemplate,
                ParameterKind.Choice => ChoiceTemplate,
                ParameterKind.Text => TextTemplate,
                _ => base.SelectTemplate(item, container),
            };
}
