using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.ExecutionUi;

public readonly record struct ExecutionTypedConfirmationResult(
    bool IsConfirmed,
    string EnteredText)
{
    public static ExecutionTypedConfirmationResult Cancelled => new(false, string.Empty);
}

public interface IExecutionConfirmationService
{
    ValueTask<bool> ConfirmAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
        string title,
        string message,
        string requiredText,
        CancellationToken cancellationToken = default);
}

internal sealed class WpfExecutionConfirmationService : IExecutionConfirmationService
{
    public ValueTask<bool> ConfirmAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = MessageBox.Show(
            ResolveOwner(),
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return ValueTask.FromResult(result == MessageBoxResult.Yes);
    }

    public ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
        string title,
        string message,
        string requiredText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredText);

        var dialog = new TypedExecutionConfirmationWindow(title, message, requiredText);
        if (ResolveOwner() is { } owner)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

        var confirmed = dialog.ShowDialog() == true;
        return ValueTask.FromResult(confirmed
            ? new ExecutionTypedConfirmationResult(true, dialog.ConfirmedText)
            : ExecutionTypedConfirmationResult.Cancelled);
    }

    private static Window? ResolveOwner()
    {
        var application = Application.Current;
        return application?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive) ?? application?.MainWindow;
    }

    private sealed class TypedExecutionConfirmationWindow : Window
    {
        private readonly TextBox _entry;
        private readonly Button _confirm;
        private readonly string _requiredText;

        internal TypedExecutionConfirmationWindow(
            string title,
            string message,
            string requiredText)
        {
            _requiredText = requiredText;
            Title = title;
            Width = 520;
            SizeToContent = System.Windows.SizeToContent.Height;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var explanation = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 14),
            };
            Grid.SetRow(explanation, 0);
            root.Children.Add(explanation);

            var instruction = new TextBlock
            {
                Text = $"Type {requiredText} exactly to continue.",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
            };
            Grid.SetRow(instruction, 1);
            root.Children.Add(instruction);

            _entry = new TextBox
            {
                MinWidth = 360,
                Margin = new Thickness(0, 0, 0, 18),
                Padding = new Thickness(8, 5, 8, 5),
            };
            Grid.SetRow(_entry, 2);
            root.Children.Add(_entry);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var cancel = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 90,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
            };
            _confirm = new Button
            {
                Content = "Confirm",
                IsDefault = true,
                IsEnabled = false,
                MinWidth = 90,
                Padding = new Thickness(12, 5, 12, 5),
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(_confirm);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            _entry.TextChanged += OnEntryTextChanged;
            _confirm.Click += OnConfirmClicked;
            Closed += OnClosed;
            Content = root;
        }

        internal string ConfirmedText { get; private set; } = string.Empty;

        private void OnEntryTextChanged(object sender, TextChangedEventArgs e) =>
            _confirm.IsEnabled = string.Equals(_entry.Text, _requiredText, StringComparison.Ordinal);

        private void OnConfirmClicked(object sender, RoutedEventArgs e)
        {
            if (!string.Equals(_entry.Text, _requiredText, StringComparison.Ordinal))
                return;

            ConfirmedText = _entry.Text;
            DialogResult = true;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _entry.TextChanged -= OnEntryTextChanged;
            _confirm.Click -= OnConfirmClicked;
            Closed -= OnClosed;
            _entry.Clear();
        }
    }
}
