namespace DaxAlgo.Blocks;

/// <summary>A small store that survives restarts.</summary>
[BlockCard(
    "state",
    "remember values across restarts",
    Does = "Keeps JSON-serializable values by key for this unit, saved by the host and restored the next time the unit starts.",
    Needs = "Values that System.Text.Json can serialize.",
    Limits = "At most MaxBytes of JSON per unit; a Set that would exceed it throws. Saved when the unit stops and periodically while it runs. Files are not allowed — use this.",
    Order = 110)]
public interface IState
{
    /// <summary>The most JSON one unit may store.</summary>
    const int MaxBytes = 1_048_576;

    /// <summary>The stored value for <paramref name="key"/>, or default when there is none.</summary>
    T? Get<T>(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>.</summary>
    void Set<T>(string key, T value);

    /// <summary>Removes <paramref name="key"/>; false when it was not stored.</summary>
    bool Remove(string key);

    /// <summary>Every stored key.</summary>
    IReadOnlyCollection<string> Keys { get; }
}
