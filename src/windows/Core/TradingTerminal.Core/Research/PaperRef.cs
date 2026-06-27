namespace TradingTerminal.Core.Research;

/// <summary>
/// A resolved research paper: its arXiv id, title, and canonical URL. The URL becomes the
/// <c>ResearchPaperUrl</c> on any strategy bridged from a reproduction, so it must always be
/// preserved as provenance. Resolution (arXiv metadata lookup) happens in the Python sidecar —
/// the C# side only carries the resolved record.
/// </summary>
public sealed record PaperRef(string ArxivId, string Title, string Url);
