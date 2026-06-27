namespace TradingTerminal.Core.Domain;

/// <summary>
/// A surrogate, broker-neutral identifier for a tradable instrument. Strategies and the
/// market-data store key on this — never on a broker's symbology — so the same instrument is
/// one identity no matter which broker streamed it. Wraps an <see cref="int"/> (the row id in
/// the instruments table) in a struct so the type system stops you passing a raw symbol or a
/// broker conId where a canonical id is expected.
/// </summary>
public readonly record struct InstrumentId(int Value)
{
    /// <summary>The unset / unresolved id. Ingest treats a record carrying this as "needs resolution".</summary>
    public static InstrumentId None => new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => $"#{Value}";
}
