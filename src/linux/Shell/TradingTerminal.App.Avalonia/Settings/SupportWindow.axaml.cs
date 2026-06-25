using Avalonia.Controls;
using TradingTerminal.App.Support;

namespace TradingTerminal.App.Avalonia.Settings;

public partial class SupportWindow : Window
{
    public SupportWindow()
    {
        InitializeComponent();
        // The VM raises CloseRequested (Close command) without referencing the view — close here.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SupportViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
    }
}
