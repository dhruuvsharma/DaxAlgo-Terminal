using TradingTerminal.ExecutionUi;
using Xunit;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// The broker symbol a book's instrument entry hands its order route: the entry without its <c>|label</c> —
/// except that an Upstox instrument key is itself <c>SEGMENT|ISIN</c>, and cutting it at the bar left the route
/// a bare segment name.
/// </summary>
public sealed class BrokerSymbolTests
{
    [Theory]
    [InlineData("BTCUSDT", "BTCUSDT")]
    [InlineData("ESZ6|E-mini S&P 500 Dec 26", "ESZ6")]
    [InlineData("$SPX|S&P 500 index", "$SPX")]
    [InlineData("NSE_EQ:2885|RELIANCE", "NSE_EQ:2885")]
    [InlineData("NSE:2885|RELIANCE", "NSE:2885")]
    [InlineData("NSE_EQ|INE002A01018", "NSE_EQ|INE002A01018")]
    [InlineData("NSE_EQ|INE002A01018|Reliance", "NSE_EQ|INE002A01018")]
    [InlineData(" AAPL ", "AAPL")]
    public void An_entry_loses_its_label_and_nothing_else(string entry, string symbol) =>
        Assert.Equal(symbol, InProcessExecutionClient.BrokerSymbol(entry));
}
