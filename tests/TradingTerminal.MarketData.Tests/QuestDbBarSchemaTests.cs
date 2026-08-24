using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.MarketData.Tests;

/// <summary>
/// Bars moved into QuestDB on 2026-08-23, which removed the split store and cut the provider list to
/// two. These pin the parts that can be checked without a running QuestDB.
/// </summary>
public sealed class QuestDbBarSchemaTests
{
    [Fact]
    public void BarsDedupOnTheDesignatedTimestampAndTheirNaturalKey()
    {
        // The whole basis for keeping bars in an append-only store. A forming bar is re-sent on every
        // update, so without upsert-dedup each tick of a live bar lands as another row and a day of
        // one-minute bars reads back thousands deep. QuestDB also REQUIRES the designated timestamp to
        // be among the keys — omit `ts` and the DDL is rejected at table creation, which would take
        // the whole store down at startup rather than fail quietly.
        var keys = QuestDbSchema.BarDedupKeys.Split(',', StringSplitOptions.TrimEntries);

        Assert.Contains("ts", keys);
        Assert.Contains("instrument", keys);
        Assert.Contains("bar_size", keys);
    }

    [Fact]
    public void TheBarsDdlKeepsEveryClauseSeparated()
    {
        // Regression: the DDL used to be a raw literal ending in `PARTITION BY DAY WAL` with the
        // `DEDUP UPSERT KEYS(...)` clause concatenated onto it. A raw string literal does not keep
        // the newline before its closing delimiter, so the two clauses fused into one token and
        // QuestDB answered `unexpected token [WALDEDUP]`. The store constructor does not guard
        // EnsureCreated, so that killed application startup outright rather than degrading.
        Assert.DoesNotContain("WALDEDUP", QuestDbSchema.BarsDdl);

        // Assert the shape rather than the absence of one typo: every clause the statement is built
        // from has to survive as its own whitespace-delimited token.
        var tokens = QuestDbSchema.BarsDdl.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("WAL", tokens);
        Assert.Contains("DEDUP", tokens);
        Assert.Contains("UPSERT", tokens);
    }

    [Fact]
    public void TheBarsDdlDedupsOnExactlyTheDeclaredKeys()
    {
        // The key list is interpolated into the statement, so the constant and the DDL cannot drift.
        Assert.Contains($"DEDUP UPSERT KEYS({QuestDbSchema.BarDedupKeys});", QuestDbSchema.BarsDdl);

        // QuestDB requires the designated timestamp to be among the dedup keys; pin that the column
        // named in TIMESTAMP(...) is the one the key list leads with.
        Assert.Contains("TIMESTAMP(ts)", QuestDbSchema.BarsDdl);
    }

    [Fact]
    public void TheProviderListIsDownToTwo()
    {
        // Single-file `Sqlite` was a strictly worse SqlitePerBroker — one writer for every stream, and
        // it dropped depth — and `Postgres` was a second store implementation no shipped configuration
        // selected.
        Assert.Equal(
            [MarketDataProvider.QuestDb, MarketDataProvider.SqlitePerBroker],
            Enum.GetValues<MarketDataProvider>());
    }

    [Fact]
    public void TheSurvivingProvidersKeepTheirOriginalNumericValues()
    {
        // Removing enum members renumbers whatever follows them unless the values are pinned. A stored
        // configuration holding the old `QuestDb = 2` must not silently come back as something else.
        Assert.Equal(2, (int)MarketDataProvider.QuestDb);
        Assert.Equal(3, (int)MarketDataProvider.SqlitePerBroker);
    }

    [Fact]
    public void TheDefaultIsQuestDbAndTheFallbackNeedsNoServer()
    {
        Assert.Equal(MarketDataProvider.QuestDb, new MarketDataStoreOptions().Provider);
        // The escape hatch when QuestDB cannot run: embedded files, no process to start.
        Assert.Contains(MarketDataProvider.SqlitePerBroker, Enum.GetValues<MarketDataProvider>());
    }
}
