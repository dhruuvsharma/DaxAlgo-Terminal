using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Blocks;

/// <summary>Reads and changes the values the user configures for this unit.</summary>
[BlockCard(
    "settings",
    "values the user configures",
    Does = "Reads the current value of each setting declared in UnitInfo.Settings, tells you when one changes, and lets the unit's own page change them.",
    Needs = "Each setting declared in UnitInfo.Settings with a StrategyParameter factory; read it back with the same key.",
    Limits = "Reading a key that was never declared throws. Set converts the value to the setting's kind and clamps numbers into the declared min/max; a value that cannot be converted becomes the default. OnChanged handlers run after the current handler returns.",
    Types = [typeof(StrategyParameter)],
    Order = 10)]
public interface ISettings
{
    /// <summary>The value of an Int setting.</summary>
    int Int(string key);

    /// <summary>The value of a Number setting.</summary>
    double Number(string key);

    /// <summary>The value of a Bool setting.</summary>
    bool Bool(string key);

    /// <summary>The value of a Text, Choice or Enum setting.</summary>
    string Text(string key);

    /// <summary>The instrument chosen for an Instrument setting.</summary>
    InstrumentId Instrument(string key);

    /// <summary>Changes a setting, e.g. from a control on the unit's own page.</summary>
    void Set(string key, object value);

    /// <summary>Calls <paramref name="handler"/> with the key whenever a setting changes; dispose to stop.</summary>
    IDisposable OnChanged(Action<string> handler);
}
