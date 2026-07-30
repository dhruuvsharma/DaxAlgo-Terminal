using MahApps.Metro.Controls;

namespace TradingTerminal.Accounts;

public partial class AccountGateWindow : MetroWindow
{
    private readonly AccountGateViewModel _viewModel;

    internal AccountGateWindow(AccountGateViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;
        Closed += OnClosed;
    }

    private void OnCompleted(bool granted) => DialogResult = granted;

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Completed -= OnCompleted;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
