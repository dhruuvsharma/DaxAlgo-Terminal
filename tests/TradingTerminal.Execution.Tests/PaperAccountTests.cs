using TradingTerminal.Core.Execution;
using TradingTerminal.Execution.Oms;
using Xunit;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The terminal's own paper account.
///
/// <para>Paper trading used to mean "connect to your broker's paper account" — an Interactive Brokers
/// paper port, an Alpaca paper key. That meant a separate account to open and keep straight at every
/// broker, no way to paper-trade at all against a venue with no paper environment, and an app-wide
/// Paper switch that decided nothing because the broker decided instead.</para>
///
/// <para>And the in-process book it fell back to opened with <b>zero</b> money in a currency called
/// <c>SIM</c> — so it could accept an order and then have nothing to settle it with, which is not what
/// paper trading is supposed to mean.</para>
/// </summary>
public sealed class PaperAccountTests
{
    [Fact]
    public void TheAccountOpensWithAHundredThousand()
    {
        Assert.Equal(100_000m, new PaperAccountOptions().StartingBalance);
    }

    [Fact]
    public void TheCurrencyIsARealOneNotAPlaceholder()
    {
        // The simulated adapter's own default was "SIM", which is not a currency and cannot be
        // reconciled against anything.
        Assert.Equal("USD", new PaperAccountOptions().Currency);
    }

    [Fact]
    public void OpeningCashCarriesTheConfiguredBalance()
    {
        var cash = PaperAccount.OpeningCash(new PaperAccountOptions(), DateTime.UnixEpoch);

        Assert.Equal("USD", cash.Currency);
        Assert.Equal(PaperAccount.Money(100_000m), cash.Total);
    }

    [Fact]
    public void EverythingIsAvailableAtOpen()
    {
        // Nothing is committed to a working order yet, so available and total are the same. A book that
        // opened with total funds and zero available would reject its first order.
        var cash = PaperAccount.OpeningCash(new PaperAccountOptions(), DateTime.UnixEpoch);

        Assert.Equal(cash.Total, cash.Available);
    }

    [Fact]
    public void ABalanceOfZeroFallsBackRatherThanOpeningBroke()
    {
        // A zero or negative opening balance produces a book that rejects its first order for
        // insufficient funds, which reads as a broken terminal rather than a configuration mistake.
        Assert.Equal(100_000m, new PaperAccountOptions { StartingBalance = 0m }.EffectiveStartingBalance);
        Assert.Equal(100_000m, new PaperAccountOptions { StartingBalance = -5m }.EffectiveStartingBalance);
    }

    [Fact]
    public void AConfiguredBalanceIsHonoured()
    {
        Assert.Equal(25_000m, new PaperAccountOptions { StartingBalance = 25_000m }.EffectiveStartingBalance);
    }

    [Fact]
    public void ANonsenseCurrencyFallsBackToUsd()
    {
        Assert.Equal("USD", new PaperAccountOptions { Currency = "" }.EffectiveCurrency);
        Assert.Equal("USD", new PaperAccountOptions { Currency = "  " }.EffectiveCurrency);
        Assert.Equal("USD", new PaperAccountOptions { Currency = "DOLLARS" }.EffectiveCurrency);
    }

    [Fact]
    public void ACurrencyCodeIsNormalisedRatherThanRejected()
    {
        Assert.Equal("EUR", new PaperAccountOptions { Currency = "eur" }.EffectiveCurrency);
    }

    // ── the money type ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MoneyKeepsTwoDecimalPlaces()
    {
        // The engine's money is a coefficient and a scale precisely so cash never travels as a binary
        // float. A starting balance is the one figure a human types, so it is rounded once, here.
        var money = PaperAccount.Money(100_000m);

        Assert.Equal(2, money.Scale);
        Assert.Equal(10_000_000L, money.Coefficient);
    }

    [Fact]
    public void MoneyDoesNotRoundUpwards()
    {
        // Rounding a balance up invents money. Towards zero is the only direction that cannot.
        Assert.Equal(99L, PaperAccount.Money(0.999m).Coefficient);
    }

    [Fact]
    public void MoneyIsValidForTheEngine()
    {
        Assert.True(PaperAccount.Money(100_000m).IsValid);
    }

    // ── one account ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThereIsOneAccountNotOnePerBroker()
    {
        // The whole point of moving paper inside the terminal: a user should not have to open, fund
        // and keep straight a separate paper account at every venue they want to try a strategy on.
        Assert.Equal(PaperAccount.Identity, PaperAccount.Identity);
        Assert.Equal(PaperAccountOptions.AccountId, PaperAccount.Identity.AccountId.Value);
    }

    [Fact]
    public void TheAccountIsTheTerminalsNotABrokers()
    {
        // The adapter id names the terminal, not a venue — nothing here routes to anybody's paper
        // environment.
        Assert.Equal("paper", PaperAccount.Identity.AdapterId.Value);
    }

    [Fact]
    public void TheIdentityIsValid()
    {
        Assert.True(PaperAccount.Identity.IsValid);
    }

    // ── the bug this uncovered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReconciliationBasisMatchesTheOpeningCashExactly()
    {
        // Two claims about the same money. If the adapter opens with a hundred thousand and the
        // reconciler still believes the old zero basis, every paper book starts life with a break
        // reading "adapter subject is absent from the local ledger" — which is the engine correctly
        // noticing the disagreement. It caught exactly that when this was first wired.
        var options = new PaperAccountOptions();
        var cash = PaperAccount.OpeningCash(options, DateTime.UnixEpoch);
        var basis = PaperAccount.CashBasis(options);

        Assert.Equal(cash.Currency, basis.Currency);
        Assert.Equal(cash.Total, basis.OpeningTotal);
        Assert.Equal(cash.Available, basis.OpeningAvailable);
    }

    [Fact]
    public void TheBasisDoesNotCompareAvailableCash()
    {
        // Comparing it was tried first, on the reasoning that a simulated venue extends no margin so
        // available must be exactly derivable. A working order reserves against available before it
        // fills, so the two sides disagree for as long as anything is live. Total is what must
        // reconcile; available is a working number.
        Assert.False(PaperAccount.CashBasis(new PaperAccountOptions()).CompareAvailable);
    }

    [Fact]
    public void TheBasisIsValidForTheEngine()
    {
        Assert.True(PaperAccount.CashBasis(new PaperAccountOptions()).IsValid);
    }

    [Fact]
    public void ACustomBalanceStillReconciles()
    {
        var options = new PaperAccountOptions { StartingBalance = 250_000m, Currency = "EUR" };
        var cash = PaperAccount.OpeningCash(options, DateTime.UnixEpoch);
        var basis = PaperAccount.CashBasis(options);

        Assert.Equal("EUR", basis.Currency);
        Assert.Equal(cash.Total, basis.OpeningTotal);
    }
}
