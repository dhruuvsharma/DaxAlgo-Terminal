namespace TradingTerminal.Sandbox.Runtime.Tests;

public sealed class RecordingVirtualBookTests
{
    [Fact]
    public void RecordsLatestIntentPerDeclaredInstrumentAndResetsInPlace()
    {
        var instrument = new InstrumentId(7);
        var book = new RecordingVirtualBook(new HashSet<InstrumentId> { instrument });
        IVirtualBook writer = book;

        writer.SetTargetPosition(instrument, 1d);
        writer.SetTargetPosition(instrument, 2d, 95d, 110d);
        writer.SetTargetPosition(new InstrumentId(8), 99d);

        var recorded = Assert.Single(book.RecordedIntents);
        Assert.Equal(instrument, recorded.Instrument);
        Assert.Equal(2d, recorded.TargetUnits);
        Assert.Equal(95d, recorded.ProtectiveStopPrice);
        Assert.Equal(110d, recorded.ProfitTargetPrice);

        book.Reset();

        Assert.Empty(book.RecordedIntents);
    }
}
