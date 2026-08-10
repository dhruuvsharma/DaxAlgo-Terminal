namespace TradingTerminal.Sandbox.Portfolio;

/// <summary>
/// Local, non-wire fault categories for the generic host-call fault outcome specified by the
/// sections 5.4.2 and 5.4.5.
/// </summary>
public enum ModelPortfolioFault : byte
{
    /// <summary>No fault occurred.</summary>
    None = 0,

    /// <summary>The bounded model declaration is invalid.</summary>
    InvalidConfiguration = 1,

    /// <summary>The callback lifecycle was used out of order.</summary>
    InvalidCallbackState = 2,

    /// <summary>The run has already completed.</summary>
    RunCompleted = 3,

    /// <summary>No finite, strictly positive reference price was available.</summary>
    InvalidReferencePrice = 4,

    /// <summary>A finite positive quote was crossed.</summary>
    CrossedQuote = 5,

    /// <summary><c>MpMarket</c> received non-finite or zero units.</summary>
    InvalidMarketUnits = 6,

    /// <summary><c>MpClose</c> received a fraction outside <c>(0, 1]</c>.</summary>
    InvalidCloseFraction = 7,

    /// <summary><c>MpClose</c> was called while flat.</summary>
    CloseWhileFlat = 8,

    /// <summary>A retained-trip read used an unavailable index.</summary>
    TradeIndexOutOfRange = 9,

    /// <summary>A binary64 intermediate became non-finite or degenerate.</summary>
    NonFiniteArithmetic = 10,

    /// <summary>A bounded integral counter could not be incremented.</summary>
    CounterOverflow = 11,

    /// <summary>An exit declaration used a mode that is not valid for that function.</summary>
    InvalidExitMode = 12,

    /// <summary>An exit declaration received a non-finite or non-positive value.</summary>
    InvalidExitValue = 13,

    /// <summary>An exit declaration or cancellation was attempted while flat.</summary>
    ExitWhileFlat = 14,

    /// <summary>A stop or target resolved to the wrong side of the average entry.</summary>
    ExitOnWrongSide = 15,

    /// <summary>An exit resolved to the average entry and would establish a zero R.</summary>
    ExitAtEntry = 16,

    /// <summary>An operation attempted to consume R before it was captured.</summary>
    UndefinedR = 17,

    /// <summary><c>MpTrail</c> received a non-finite or negative activation R.</summary>
    InvalidTrailActivation = 18,
}
