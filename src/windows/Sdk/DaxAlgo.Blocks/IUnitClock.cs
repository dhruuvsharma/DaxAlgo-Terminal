namespace DaxAlgo.Blocks;

/// <summary>The time the unit is running at.</summary>
[BlockCard(
    "clock",
    "the current market time",
    Does = "Returns the current time: market time when live, the replayed time in a replay or test.",
    Needs = "Nothing.",
    Limits = "Use this instead of DateTime.UtcNow, which is wrong in a replay.",
    Order = 50)]
public interface IUnitClock
{
    /// <summary>The current time, in UTC.</summary>
    DateTime UtcNow { get; }
}
