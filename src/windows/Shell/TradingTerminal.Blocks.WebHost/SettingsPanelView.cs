using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using TradingTerminal.UI.Controls.Render;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// The terminal's settings panel for a Blocks unit, drawn as a page (<see cref="SettingsPage"/>) instead
/// of the WPF expander.
///
/// <para><b>One model, two surfaces.</b> It binds the same <see cref="AuthoredUnitPresenter"/> the
/// expander binds: rows are pushed to the page as state, edits come back as messages and are written to
/// the same <see cref="AuthoredUnitParameter"/> rows, and Apply runs the same command — so parsing,
/// range-clamping, the all-or-nothing validation and the restart-on-apply have exactly one
/// implementation, whichever surface the user is looking at.</para>
///
/// <para>The panel is the terminal's, so a unit cannot forget its settings, lose its instrument picker or
/// draw a picker that does not pick. The unit's page is still the author's, and the two are separate
/// WebView2 views, so a page that throws cannot take the settings with it.</para>
/// </summary>
public sealed class SettingsPanelView : ContentControl, IDisposable
{
    private readonly AuthoredUnitPresenter _presenter;
    private readonly SettingsBridge _bridge;
    private readonly WebUnitView _view;
    private readonly List<AuthoredUnitParameter> _watched = [];
    private bool _pushQueued;
    private int _disposed;

    public SettingsPanelView(AuthoredUnitPresenter presenter, WebUnitViewOptions? options = null)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _bridge = new SettingsBridge(_presenter);
        _view = new WebUnitView(options);
        Content = _view;

        _view.MessageReceived += OnMessage;
        _view.Opened += Push;

        _presenter.PropertyChanged += OnPresenterChanged;
        foreach (var parameter in _presenter.Parameters) Watch(parameter);
        _presenter.Parameters.CollectionChanged += OnParametersChanged;
    }

    /// <summary>True while the page is open and listening.</summary>
    public bool IsOpen => _view.IsOpen;

    /// <summary>Writes the panel's page and loads it. Call once the view is in a shown window.</summary>
    public async Task LoadAsync(string root, string unitId, CancellationToken ct = default)
    {
        var folder = UnitPageFolder.Write(
            root, unitId + ".settings", [new Core.Strategies.Authoring.StrategyFile("ui/index.html", SettingsPage.Html)]);

        await _view.LoadAsync(folder, ct);
    }

    private void OnParametersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var parameter in _presenter.Parameters) Watch(parameter);
        Push();
    }

    private void Watch(AuthoredUnitParameter parameter)
    {
        if (_watched.Contains(parameter)) return;
        _watched.Add(parameter);
        parameter.PropertyChanged += OnParameterChanged;
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e) => Push();

    private void OnPresenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AuthoredUnitPresenter.ParameterStatus)
            or nameof(AuthoredUnitPresenter.IsApplying)
            or nameof(AuthoredUnitPresenter.CanEditParameters)
            or nameof(AuthoredUnitPresenter.HasPendingChanges)
            or null)
        {
            Push();
        }
    }

    /// <summary>
    /// Sends the whole state, once per dispatcher turn.
    ///
    /// <para>Coalesced because one keystroke raises several notifications — the row's value, its error,
    /// the presenter's pending-changes flag — and each would otherwise be a message and a re-render.</para>
    /// </summary>
    private void Push()
    {
        if (_disposed != 0 || _pushQueued) return;
        _pushQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pushQueued = false;
            if (_disposed != 0 || !_view.IsOpen) return;

            _view.Post("settings", _bridge.StateJson());
        });
    }

    /// <summary>What the page asks for, handled by the bridge and written to the presenter's own rows.</summary>
    private void OnMessage(string topic, string payload) => _bridge.Handle(topic, payload);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _view.MessageReceived -= OnMessage;
        _view.Opened -= Push;
        _presenter.PropertyChanged -= OnPresenterChanged;
        _presenter.Parameters.CollectionChanged -= OnParametersChanged;
        foreach (var parameter in _watched) parameter.PropertyChanged -= OnParameterChanged;
        _watched.Clear();
        _view.Dispose();
    }
}
