using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Avalonia.Login;

/// <summary>
/// Avalonia sign-in VM. Lists the brokers registered in the headless DI graph (via the portable
/// <see cref="IBrokerSelector"/>) and connects the chosen one through the real connection path —
/// the same seam the WPF login uses. The Simulated broker (and the keyless crypto feeds) connect
/// with no credentials, which is the offline Linux path; brokers needing API keys read them from
/// configuration (a cross-platform credential-entry UI is the next step). Raises
/// <see cref="Connected"/> once a broker reaches <see cref="ConnectionState.Connected"/>.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IBrokerSelector _selector;

    public event Action<BrokerKind>? Connected;

    public LoginViewModel(IBrokerSelector selector)
    {
        _selector = selector;
        Brokers = new ObservableCollection<BrokerOption>(
            selector.AvailableKinds.Select(k =>
            {
                var mode = selector.ModeOf(k);
                return new BrokerOption(k, mode.DisplayName, mode.Description, mode.IsLive);
            }));
        SelectedBroker = Brokers.FirstOrDefault(b => b.Kind == BrokerKind.Simulated) ?? Brokers.FirstOrDefault();
    }

    public ObservableCollection<BrokerOption> Brokers { get; }

    [ObservableProperty] private BrokerOption? _selectedBroker;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _status = "Select a data source and connect.";

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedBroker is not { } opt || IsConnecting) return;
        IsConnecting = true;
        Status = $"Connecting to {opt.DisplayName}…";
        try
        {
            await _selector.ConnectAsync(opt.Kind);

            // Wait (bounded) for the connection state to flip to Connected.
            for (int i = 0; i < 100 && !_selector.IsConnected(opt.Kind); i++)
                await Task.Delay(50);

            if (_selector.IsConnected(opt.Kind))
            {
                Status = $"Connected to {opt.DisplayName}.";
                Connected?.Invoke(opt.Kind);
            }
            else
            {
                Status = $"{opt.DisplayName} did not connect — it may need credentials in configuration.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Connect failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }
}

/// <summary>One selectable broker row on the login screen.</summary>
public sealed record BrokerOption(BrokerKind Kind, string DisplayName, string Description, bool IsLive);
