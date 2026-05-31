using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// View-model behind the auto-generated parameter panel. Wraps a live
/// <see cref="StrategyParameters"/> bag and exposes one <see cref="ParameterEditorItem"/>
/// per declared tunable. Bind an <c>ItemsControl</c> to <see cref="Items"/> with the
/// <c>ParameterTemplateSelector</c>; the panel renders itself from the schema, so no
/// strategy ever hand-writes a parameter editor again.
///
/// Hand the resulting <see cref="Parameters"/> bag to
/// <c>BacktestStrategyOption.Create(contract, parameters)</c> to launch a run/live host
/// with the user's chosen settings.
/// </summary>
public sealed partial class StrategyParametersViewModel : ObservableObject
{
    /// <summary>Builds an editor panel from a schema, seeded with defaults.</summary>
    public static StrategyParametersViewModel FromSchema(StrategyParameterSchema schema) =>
        new(schema.CreateDefaults());

    public StrategyParametersViewModel(StrategyParameters parameters)
    {
        Parameters = parameters;
        Items = new ObservableCollection<ParameterEditorItem>(
            parameters.Schema.Parameters.Select(p => new ParameterEditorItem(parameters, p)));
    }

    /// <summary>The live value bag — pass to <c>BacktestStrategyOption.Create</c>.</summary>
    public StrategyParameters Parameters { get; }

    public ObservableCollection<ParameterEditorItem> Items { get; }

    public bool HasParameters => Items.Count > 0;

    /// <summary>Restores every parameter to its schema default and refreshes the editors.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        foreach (var p in Parameters.Schema.Parameters)
            Parameters.Set(p.Key, p.Default);
        foreach (var item in Items)
            item.Refresh();
    }
}
