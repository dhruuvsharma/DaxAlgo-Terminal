using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingTerminal.App;

/// <summary>One entry in the View → Theme menu — the theme id + display name, plus an observable
/// <see cref="IsCurrent"/> the menu binds to for the radio checkmark.</summary>
public sealed partial class ThemeMenuOption : ObservableObject
{
    public ThemeMenuOption(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isCurrent;
}
