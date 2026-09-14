namespace DaxAlgo.Blocks;

/// <summary>
/// Every block a unit can use, handed to <see cref="IUnit.StartAsync"/>.
///
/// <para>Each property is an independent block with its own card. A unit uses only the ones it needs;
/// using <see cref="Orders"/> is what makes it a strategy.</para>
/// </summary>
public interface IUnitContext
{
    /// <summary>The <c>settings</c> block.</summary>
    ISettings Settings { get; }

    /// <summary>The <c>market</c> block.</summary>
    IMarketData Market { get; }

    /// <summary>The <c>history</c> block.</summary>
    IHistory History { get; }

    /// <summary>The <c>instruments</c> block.</summary>
    IInstruments Instruments { get; }

    /// <summary>The <c>clock</c> block.</summary>
    IUnitClock Clock { get; }

    /// <summary>The <c>schedule</c> block.</summary>
    ISchedule Schedule { get; }

    /// <summary>The <c>orders</c> block.</summary>
    IOrders Orders { get; }

    /// <summary>The <c>portfolio</c> block.</summary>
    IPortfolio Portfolio { get; }

    /// <summary>The <c>alerts</c> block.</summary>
    IAlerts Alerts { get; }

    /// <summary>The <c>log</c> block.</summary>
    ILog Log { get; }

    /// <summary>The <c>state</c> block.</summary>
    IState State { get; }

    /// <summary>The <c>export</c> block.</summary>
    IExport Export { get; }

    /// <summary>The <c>ui</c> block.</summary>
    IUiBridge Ui { get; }
}
