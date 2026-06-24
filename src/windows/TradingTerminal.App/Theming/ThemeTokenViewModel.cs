using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.UI.Theming;

namespace TradingTerminal.App.Theming;

/// <summary>
/// One editable row in the Theme Studio. Wraps a <see cref="ThemeToken"/> and pushes every edit
/// straight into <see cref="IThemeManager"/> so the whole app re-skins live. Solid tokens expose a
/// hex string plus A/R/G/B bytes (kept in sync); gradient tokens expose a list of editable stops.
/// </summary>
public sealed class ThemeTokenViewModel : ObservableObject
{
    private readonly IThemeManager _manager;
    private bool _suppress;

    public ThemeTokenViewModel(IThemeManager manager, ThemeToken token)
    {
        _manager = manager;
        DisplayName = token.DisplayName;
        PrimaryKey = token.PrimaryKey;
        LinkedColorKey = token.LinkedColorKey;
        IsGradient = token.Kind == ThemeTokenKind.Gradient;

        if (IsGradient)
        {
            Stops = new ObservableCollection<GradientStopViewModel>(
                token.GradientStops.Select((c, i) => new GradientStopViewModel(i, c, ApplyGradient)));
        }
        else
        {
            Stops = new ObservableCollection<GradientStopViewModel>();
            _color = token.SolidValue;
        }
    }

    public string DisplayName { get; }
    public string PrimaryKey { get; }
    public string? LinkedColorKey { get; }
    public bool IsGradient { get; }
    public bool IsSolid => !IsGradient;

    public ObservableCollection<GradientStopViewModel> Stops { get; }

    // ── Solid ───────────────────────────────────────────────────────────────────────────────────

    private Color _color;
    public Color Color => _color;

    /// <summary>Preview swatch fill, kept current with the colour.</summary>
    public Brush Swatch => new SolidColorBrush(_color);

    public string Hex
    {
        get => ThemeColorUtil.ToHex(_color);
        set
        {
            if (_suppress || !ThemeColorUtil.TryParse(value, out var c) || c == _color) return;
            SetColor(c, syncHex: false);
        }
    }

    public byte A { get => _color.A; set => SetChannel(value, (c, v) => Color.FromArgb(v, c.R, c.G, c.B)); }
    public byte R { get => _color.R; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, v, c.G, c.B)); }
    public byte G { get => _color.G; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, v, c.B)); }
    public byte B { get => _color.B; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, c.G, v)); }

    private void SetChannel(byte value, Func<Color, byte, Color> build)
    {
        if (_suppress) return;
        var next = build(_color, value);
        if (next == _color) return;
        SetColor(next, syncHex: true);
    }

    private void SetColor(Color c, bool syncHex)
    {
        _color = c;
        _manager.SetColorOverride(PrimaryKey, c);
        if (LinkedColorKey is not null) _manager.SetColorOverride(LinkedColorKey, c);

        _suppress = true;
        if (syncHex) OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(A));
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Swatch));
        _suppress = false;
    }

    // ── Gradient ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Preview brush for a gradient row — a left→right sample of the current stops.</summary>
    public Brush GradientPreview
    {
        get
        {
            var brush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            var n = Stops.Count;
            for (var i = 0; i < n; i++)
                brush.GradientStops.Add(new GradientStop(Stops[i].Color, n <= 1 ? 0 : (double)i / (n - 1)));
            return brush;
        }
    }

    private void ApplyGradient()
    {
        _manager.SetGradientOverride(PrimaryKey, Stops.Select(s => s.Color).ToList());
        OnPropertyChanged(nameof(GradientPreview));
    }
}

/// <summary>One colour stop inside a gradient token. Same hex/RGBA surface as a solid token.</summary>
public sealed class GradientStopViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;
    private Color _color;

    public GradientStopViewModel(int index, Color color, Action onChanged)
    {
        Index = index;
        _color = color;
        _onChanged = onChanged;
    }

    public int Index { get; }
    public string Label => $"Stop {Index + 1}";
    public Color Color => _color;
    public Brush Swatch => new SolidColorBrush(_color);

    public string Hex
    {
        get => ThemeColorUtil.ToHex(_color);
        set
        {
            if (_suppress || !ThemeColorUtil.TryParse(value, out var c) || c == _color) return;
            SetColor(c, syncHex: false);
        }
    }

    public byte A { get => _color.A; set => SetChannel(value, (c, v) => Color.FromArgb(v, c.R, c.G, c.B)); }
    public byte R { get => _color.R; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, v, c.G, c.B)); }
    public byte G { get => _color.G; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, v, c.B)); }
    public byte B { get => _color.B; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, c.G, v)); }

    private void SetChannel(byte value, Func<Color, byte, Color> build)
    {
        if (_suppress) return;
        var next = build(_color, value);
        if (next == _color) return;
        SetColor(next, syncHex: true);
    }

    private void SetColor(Color c, bool syncHex)
    {
        _color = c;
        _suppress = true;
        if (syncHex) OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(A));
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Swatch));
        _suppress = false;
        _onChanged();
    }
}

/// <summary>Hex ⇄ <see cref="Color"/> helpers shared by the Theme Studio view-models.</summary>
internal static class ThemeColorUtil
{
    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? s, out Color c)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s) && ColorConverter.ConvertFromString(s) is Color parsed)
            {
                c = parsed;
                return true;
            }
        }
        catch { /* invalid hex while typing — ignore */ }
        c = default;
        return false;
    }
}
