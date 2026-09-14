namespace DaxAlgo.Blocks;

/// <summary>
/// Describes one block for the agent: what it does, what it needs, and what limits it has.
///
/// <para>The card is generated from this attribute plus the lead sentence of each member's XML summary
/// (<c>BlockCatalogGenerator</c>), so the words a model reads are the words next to the code. The
/// agent is told what a block does and how to call it, never how it works inside.</para>
///
/// <para>On an interface, the card lists that interface's members. On the assembly, it describes a
/// block that is not an interface — a slice of the maths library, or raw network use — through
/// <see cref="Types"/> and prose alone.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class BlockCardAttribute(string id, string title) : Attribute
{
    /// <summary>Stable id the planner names a block by, e.g. <c>market</c> or <c>math.indicators</c>.</summary>
    public string Id { get; } = id;

    /// <summary>One line: what the block is for.</summary>
    public string Title { get; } = title;

    /// <summary>What the block does, in a sentence or two.</summary>
    public string Does { get; set; } = string.Empty;

    /// <summary>What the unit must provide or declare before the block works.</summary>
    public string Needs { get; set; } = string.Empty;

    /// <summary>Bounds, threading and anything else a caller would otherwise learn by failing.</summary>
    public string Limits { get; set; } = string.Empty;

    /// <summary>Further types the card describes: records the block hands out, factories it expects,
    /// or the members of a maths slice.</summary>
    public Type[] Types { get; set; } = [];

    /// <summary>Where the card sits in the index. Lower comes first.</summary>
    public int Order { get; set; } = 100;
}
