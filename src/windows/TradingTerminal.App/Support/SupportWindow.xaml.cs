using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.App.Support;

/// <summary>
/// The "Support the developer" dialog. Code-behind does nothing but bridge the view-model's
/// <see cref="SupportViewModel.CloseRequested"/> event to <see cref="Window.Close"/> — all behaviour
/// lives in the VM (strict MVVM).
/// </summary>
public partial class SupportWindow : MetroWindow
{
    private SupportViewModel? _vm;

    public SupportWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.CloseRequested -= OnCloseRequested;
        _vm = e.NewValue as SupportViewModel;
        if (_vm is not null) _vm.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
