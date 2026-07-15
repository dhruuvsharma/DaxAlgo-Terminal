namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Builds the <b>default live window body</b> for an authored strategy whose author (usually a code
/// model) wrote a descriptor and a live view-model but <b>no view</b>. The composer reads the
/// descriptor's <see cref="ITradingStrategy.DataRequirement"/> and assembles the window out of the
/// embeddable chart panels — depth gets the order-book ladder + heatmap, the trade tape gets the volume
/// footprint, bars get the price chart — all with their Embedded (ML-off, chrome-off) presets, plus the
/// shared strategy chrome (setup form, start/stop, signal feed).
/// <para>
/// The contract lives in Core (UI-free, so the SDK's plugin bootstrap can name it) and returns
/// <see cref="object"/> — concretely a WPF <c>UserControl</c> — for the same reason
/// <c>StrategyFactoryRegistration.ViewFactory</c> does. The WPF implementation lives in
/// <c>TradingTerminal.StrategyComposer</c> and is registered by the shells; headless hosts (the backtest
/// CLI) simply don't register one, and never open windows anyway.
/// </para>
/// </summary>
public interface IAuthoredStrategyViewComposer
{
    /// <summary>Composes the default live view for <paramref name="descriptor"/>. The returned control
    /// receives the authored view-model as its <c>DataContext</c> from the strategy factory, exactly
    /// like a hand-written view.</summary>
    object ComposeView(ITradingStrategy descriptor);
}
