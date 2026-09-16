using System.Text.Json;
using TradingTerminal.UI.Controls.Render;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// What the settings page is told and what it is allowed to do — the whole of the panel except the
/// browser it is drawn in.
///
/// <para>Separated from <see cref="SettingsPanelView"/> so the rules can be tested without a WebView2 and
/// an STA window: the state a page receives, and the four things a page may ask for. It holds no state
/// of its own; the <see cref="AuthoredUnitPresenter"/> is the model, and every edit is written to the
/// same rows the WPF expander edits.</para>
/// </summary>
internal sealed class SettingsBridge(AuthoredUnitPresenter presenter)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AuthoredUnitPresenter _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));

    /// <summary>The whole state, as the page receives it.</summary>
    public string StateJson() => JsonSerializer.Serialize(State(), Json);

    private object State() => new
    {
        canEdit = _presenter.CanEditParameters,
        isApplying = _presenter.IsApplying,
        isDirty = _presenter.HasPendingChanges,
        status = _presenter.ParameterStatus,
        parameters = _presenter.Parameters.Select(p => new
        {
            key = p.Key,
            label = p.Label,
            kind = p.Kind.ToString(),
            value = p.Value,
            error = p.Error,
            unit = p.Unit,
            description = p.Description,
            rangeHint = p.RangeHint,
            choices = p.IsChoice ? p.Choices.ToArray() : [],
            search = p.InstrumentSearchText,

            // What the picker offers is what the presenter already narrowed and capped, so the page never
            // holds the whole registry and both surfaces show the same rows.
            instruments = p.IsInstrument
                ? p.VisibleInstruments.Select(visible =>
                {
                    var row = p.Instruments.FirstOrDefault(i => ReferenceEquals(i.Instrument, visible));
                    return new
                    {
                        id = row?.IdText ?? string.Empty,
                        name = visible.DisplayName,
                        category = visible.Category,
                        broker = row?.Broker.ToString() ?? string.Empty,
                    };
                }).Where(i => i.id.Length > 0).ToArray()
                : [],
        }).ToArray(),
        actions = _presenter.Actions
            .Select(a => new { id = a.Id, label = a.Label, detail = a.Detail ?? string.Empty })
            .ToArray(),
    };

    /// <summary>
    /// Handles one message from the page.
    ///
    /// <para>The page is the terminal's own and is still held to what the expander can do: edit a row,
    /// filter a picker, apply, reset, run a declared action. A topic it does not know is ignored rather
    /// than faulted — a page one version ahead must not break the panel.</para>
    /// </summary>
    public void Handle(string topic, string payload)
    {
        switch (topic)
        {
            case "set":
                if (Read(payload) is { Key.Length: > 0 } edit && Row(edit.Key!) is { } row)
                    row.Value = edit.Value ?? string.Empty;
                break;

            case "search":
                if (Read(payload) is { Key.Length: > 0 } search && Row(search.Key!) is { } picker)
                    picker.InstrumentSearchText = search.Value ?? string.Empty;
                break;

            case "apply":
                _presenter.ApplyParametersCommand.Execute(null);
                break;

            case "reset":
                _presenter.ResetParametersCommand.Execute(null);
                break;

            case "action":
                if (Read(payload)?.Id is { Length: > 0 } id
                    && _presenter.Actions.FirstOrDefault(a => a.Id == id) is { } action)
                    _presenter.InvokeActionCommand.Execute(action);
                break;
        }
    }

    private AuthoredUnitParameter? Row(string key) =>
        _presenter.Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    private static Edit? Read(string payload)
    {
        try { return JsonSerializer.Deserialize<Edit>(payload, Json); }
        catch (JsonException) { return null; }
    }

    private sealed record Edit(string? Key, string? Value, string? Id);
}
