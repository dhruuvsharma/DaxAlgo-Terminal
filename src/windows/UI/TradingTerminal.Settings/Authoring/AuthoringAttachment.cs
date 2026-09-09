using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// A picture the user attached to the message they are about to send.
///
/// <para>It carries the bytes AND the path it came from, which looks redundant and is not: the bytes go
/// on the wire, and the path is what a thumbnail binds to. This project is WPF-free, so it cannot hold
/// a decoded image — a path is the one form both sides can use.</para>
/// </summary>
/// <param name="Path">Where it came from, for the thumbnail.</param>
/// <param name="Name">The file's own name, for the transcript.</param>
/// <param name="Image">The bytes and media type, as the provider will receive them.</param>
public sealed record AuthoringAttachment(string Path, string Name, CodegenImage Image);
