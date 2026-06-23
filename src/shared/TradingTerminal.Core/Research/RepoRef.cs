namespace TradingTerminal.Core.Research;

/// <summary>
/// A candidate code repository for a paper, pinned to an exact <paramref name="Commit"/>. The commit
/// pin is mandatory: it is part of the reproduction cache key, and without it a reproduction is not
/// deterministic (the repo's HEAD could move under us). The repo is cloned and run ONLY inside the
/// sandbox — its code never enters the C# build.
/// </summary>
public sealed record RepoRef(string GitUrl, string Commit);
