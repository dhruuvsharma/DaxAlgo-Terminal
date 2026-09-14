using TradingTerminal.Core.Domain;

namespace DaxAlgo.Blocks;

/// <summary>Looks up instruments and their contract details.</summary>
[BlockCard(
    "instruments",
    "find instruments and read tick size, multiplier, currency",
    Does = "Searches the terminal's instrument catalog and returns contract details for an InstrumentId.",
    Needs = "A search text or an InstrumentId.",
    Limits = "Only instruments the terminal knows about; an external market (e.g. a prediction market) is fetched with the network block instead.",
    Types = [typeof(Instrument), typeof(AssetClass)],
    Order = 40)]
public interface IInstruments
{
    /// <summary>Contract details for one instrument, or null when it is unknown.</summary>
    Instrument? Find(InstrumentId instrument);

    /// <summary>Instruments whose symbol or name matches <paramref name="text"/>, best match first.</summary>
    IReadOnlyList<Instrument> Search(string text, int max = 20);
}
