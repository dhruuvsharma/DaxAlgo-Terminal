using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Quant.Surfaces;

namespace TradingTerminal.SurfaceLab;

/// <summary>
/// Configuration for one surface axis (X, Y, Z, or Color/W): a searchable, category-grouped
/// option dropdown, Min/Max/Step range inputs (shown only when the option's range is editable —
/// parameter sweeps and linear bins), and, for the Z/Color axes, a custom formula bar that
/// overrides the picked metric (variables are registry metric ids, e.g. <c>avgret / (1 + vol)</c>).
/// The parent VM repopulates the options whenever the surface mode changes.
/// </summary>
public sealed partial class AxisConfigViewModel : ObservableObject
{
    private IReadOnlyList<SurfaceAxisOption> _allOptions = Array.Empty<SurfaceAxisOption>();

    public AxisConfigViewModel(SurfaceAxisRole role, string title)
    {
        Role = role;
        Title = title;
        SupportsFormula = role is SurfaceAxisRole.Z or SurfaceAxisRole.Color;
    }

    public SurfaceAxisRole Role { get; }
    public string Title { get; }

    /// <summary>True for Z/Color — shows the custom-formula bar under the dropdown.</summary>
    public bool SupportsFormula { get; }

    /// <summary>Options surviving the search filter, in registry (grouped) order. The view's
    /// ComboBox groups these by <see cref="SurfaceAxisOption.Category"/>.</summary>
    public ObservableCollection<SurfaceAxisOption> Options { get; } = new();

    [ObservableProperty] private SurfaceAxisOption? _selectedOption;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max;
    [ObservableProperty] private double _step;
    [ObservableProperty] private bool _isRangeEditable;
    [ObservableProperty] private string _customFormula = string.Empty;

    /// <summary>True when a non-empty formula is present — the formula then overrides the metric.</summary>
    public bool UsesFormula => SupportsFormula && !string.IsNullOrWhiteSpace(CustomFormula);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedOptionChanged(SurfaceAxisOption? value)
    {
        if (value is null) return;
        IsRangeEditable = value.RangeEditable;
        Min = value.DefaultMin;
        Max = value.DefaultMax;
        Step = value.DefaultStep;
    }

    partial void OnCustomFormulaChanged(string value) => OnPropertyChanged(nameof(UsesFormula));

    /// <summary>Repopulates the dropdown for a new surface mode, keeping the previous pick when
    /// it is still legal. <paramref name="preferredIndex"/> seeds X vs Y with different defaults.</summary>
    public void SetOptions(IReadOnlyList<SurfaceAxisOption> options, int preferredIndex = 0)
    {
        _allOptions = options;
        var keepId = SelectedOption?.Id;
        SearchText = string.Empty;
        ApplyFilter();
        SelectedOption = options.FirstOrDefault(o => o.Id == keepId)
                         ?? options.ElementAtOrDefault(Math.Min(preferredIndex, options.Count - 1))
                         ?? options.FirstOrDefault();
    }

    /// <summary>Validates and converts to the engine spec; null + error when misconfigured.</summary>
    public SurfaceAxisSpec? ToSpec(out string? error)
    {
        error = null;
        if (UsesFormula)
        {
            if (SurfaceFormula.TryParse(CustomFormula, out var formulaError) is null)
            {
                error = $"{Title}: {formulaError}";
                return null;
            }
            return new SurfaceAxisSpec(SelectedOption?.Id ?? string.Empty, Min, Max, Step, CustomFormula);
        }

        if (SelectedOption is null)
        {
            error = $"{Title}: pick a variable.";
            return null;
        }
        if (IsRangeEditable && (Step <= 0 || Max <= Min))
        {
            error = $"{Title}: needs Min < Max and Step > 0.";
            return null;
        }
        return new SurfaceAxisSpec(SelectedOption.Id, Min, Max, Step);
    }

    private void ApplyFilter()
    {
        var term = SearchText?.Trim() ?? string.Empty;
        var keep = SelectedOption;
        Options.Clear();
        foreach (var o in _allOptions)
        {
            if (term.Length == 0
                || o.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || o.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                Options.Add(o);
            }
        }
        if (keep is not null && Options.Contains(keep))
            SelectedOption = keep;
    }
}
