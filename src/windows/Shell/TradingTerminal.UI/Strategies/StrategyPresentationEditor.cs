using System.Windows;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Shows the modal presentation editor for a catalog item; on Save it persists the overrides to
/// <see cref="StrategyPresentationStore"/> and applies them to the item so the card refreshes in place.
/// Returns true if the user saved. Shared by every edition shell's catalog right-click menu.
/// </summary>
public static class StrategyPresentationEditor
{
    public static bool ShowDialog(Window? owner, StrategyCatalogItemViewModel item)
    {
        var vm = new StrategyPresentationEditorViewModel(item);
        var view = new StrategyPresentationEditorView { DataContext = vm, Owner = owner };
        if (view.ShowDialog() != true) return false;

        var presentation = vm.Build();
        StrategyPresentationStore.Save(item.Id, presentation);
        item.Apply(presentation);
        return true;
    }
}
