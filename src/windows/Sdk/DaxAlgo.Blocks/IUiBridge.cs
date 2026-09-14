using System.Text.Json;

namespace DaxAlgo.Blocks;

/// <summary>Talks to the unit's own web page.</summary>
[BlockCard(
    "ui",
    "send data to the unit's own web page and receive its messages",
    Does = "The unit's window shows ui/index.html from the unit's files. Send pushes JSON to that page; On receives what the page sends back (a click, a new threshold, an order request). The look is entirely the page's.",
    Needs = "A ui/index.html file (plus any .js/.css beside it). In the page: dax.on(topic, payload => …) to receive, dax.send(topic, payload) to send, and dax.ready() once the page's listeners are attached.",
    Limits = "Send is coalesced per topic: if you send faster than the page can draw (about 30 per second), only the latest payload per topic is delivered, so send whole state, not deltas. Payloads are serialized with System.Text.Json using camelCase. The page may load scripts from https CDNs. Nothing is sent while no page is open (IsOpen).",
    Order = 130)]
public interface IUiBridge
{
    /// <summary>True while the unit's page is open and has called <c>dax.ready()</c>.</summary>
    bool IsOpen { get; }

    /// <summary>Sends <paramref name="payload"/> to the page under <paramref name="topic"/>; only the latest per topic is kept.</summary>
    void Send(string topic, object? payload);

    /// <summary>Calls <paramref name="handler"/> with the JSON payload whenever the page sends <paramref name="topic"/>; dispose to stop.</summary>
    IDisposable On(string topic, Action<JsonElement> handler);

    /// <summary>Calls <paramref name="handler"/> when the page opens (after <c>dax.ready()</c>) — the moment to send full state.</summary>
    IDisposable OnOpened(Action handler);
}
