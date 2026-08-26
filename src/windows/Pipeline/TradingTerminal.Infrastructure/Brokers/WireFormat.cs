using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Turns a silent parsing failure into a loud one.
///
/// <para>An adapter written from a vendor's documentation and not yet run against a real account has
/// three ways to be wrong, and they are not equally dangerous:</para>
///
/// <list type="bullet">
///   <item>A wrong <b>endpoint</b> answers 404. Loud, and obvious the first time anyone connects.</item>
///   <item>A wrong <b>signature</b> answers 401. Equally loud.</item>
///   <item>A wrong <b>field name</b> answers 200 with a body that parses to nothing. The chart is
///     empty, the log is clean, and it is indistinguishable from a quiet market. <b>That</b> is the one
///     worth engineering against.</item>
/// </list>
///
/// <para>So: when a response arrives with content in it and the parser finds nothing, say so. It costs
/// one branch and it converts the failure that takes an afternoon to find into one that names itself in
/// the activity log the moment a key is pasted in.</para>
/// </summary>
internal static class WireFormat
{
    /// <summary>
    /// Below this, an empty result is unremarkable — an empty JSON array, a <c>{"data":[]}</c>, a venue
    /// with genuinely nothing to say for the window asked about.
    /// </summary>
    private const int MeaningfulResponseLength = 64;

    /// <summary>
    /// Returns <paramref name="parsed"/>, warning when a substantial response yielded none of it.
    /// </summary>
    /// <param name="parsed">What the parser produced.</param>
    /// <param name="response">The raw body it was produced from.</param>
    /// <param name="logger">Where the warning goes.</param>
    /// <param name="venue">The broker's name, for the message.</param>
    /// <param name="what">What was being read — "candles", "instruments".</param>
    public static IReadOnlyList<T> OrWarn<T>(
        IReadOnlyList<T> parsed, string? response, ILogger logger, string venue, string what)
    {
        if (parsed.Count > 0 || response is null || response.Length < MeaningfulResponseLength)
            return parsed;

        logger.LogWarning(
            "{Venue} returned {Bytes} bytes of {What} and none of it parsed. The request succeeded, so "
            + "this is a shape mismatch rather than a connection problem — most likely a renamed field "
            + "or a wrapper this adapter does not unwrap. First 256 bytes: {Sample}",
            venue,
            response.Length,
            what,
            response.Length <= 256 ? response : response[..256]);

        return parsed;
    }

    /// <summary>
    /// The same for a stream: warns once per connection when messages arrive and none of them yield
    /// anything.
    ///
    /// <para>A socket is worse than a request for this. It connects, the subscribe is accepted, messages
    /// flow, and every one of them parses to nothing — so the connection reads as healthy while the
    /// chart stays empty forever. Counting is the only way to notice.</para>
    /// </summary>
    public sealed class StreamWatch(ILogger logger, string venue, string channel, int warnAfter = 25)
    {
        private int _messages;
        private int _yielded;
        private bool _warned;

        /// <summary>Records one message and how many items it produced.</summary>
        public void Observe(int produced)
        {
            _messages++;
            _yielded += produced;

            if (_warned || _yielded > 0 || _messages < warnAfter) return;

            _warned = true;
            logger.LogWarning(
                "{Venue} {Channel} has delivered {Messages} messages and none of them parsed into "
                + "anything. The socket is connected and the subscription was accepted, so this is a "
                + "shape mismatch rather than a connection problem.",
                venue, channel, _messages);
        }
    }
}
