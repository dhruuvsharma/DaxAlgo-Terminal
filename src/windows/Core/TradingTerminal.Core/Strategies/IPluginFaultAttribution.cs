namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Marker for an exception that can name the runtime plugin responsible for it even when that
/// runtime does not execute from a collectible plugin assembly load context.
/// </summary>
public interface IPluginFaultAttribution
{
    string PluginName { get; }
}
