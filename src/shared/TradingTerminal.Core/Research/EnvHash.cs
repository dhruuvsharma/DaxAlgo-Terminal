namespace TradingTerminal.Core.Research;

/// <summary>
/// A stable hash of the resolved execution environment (resolved dependency lockfile, base image,
/// interpreter version). Rides on <see cref="ReproResult"/> and every <see cref="ReproducedSignal"/>
/// as provenance so a reproduced output can always be traced back to the exact environment that
/// produced it. The resolution that yields this hash happens in the sidecar; C# only carries it.
/// </summary>
public sealed record EnvHash(string Value)
{
    /// <summary>An unresolved/unknown environment (e.g. a reproduction that never built).</summary>
    public static EnvHash None => new(string.Empty);

    public bool IsNone => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}
