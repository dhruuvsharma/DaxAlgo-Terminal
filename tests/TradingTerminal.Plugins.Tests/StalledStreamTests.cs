using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What happens when a provider opens a stream and then goes quiet.
///
/// <para>Found by running the benchmark: a fifteen-minute <c>HttpClient.Timeout</c>, a seventeen-minute
/// generation, and nothing fired. The streaming path sends with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so the timeout covers the header phase only
/// and the body is read afterwards under whatever token the caller passed. The factory's own
/// documentation calls that timeout "one generation's wall clock".</para>
///
/// <para>That generation was real work and finished, so nothing was lost. The failure it exposes is the
/// one where the provider never finishes: a turn that cannot end and cannot be cancelled.</para>
/// </summary>
public sealed class StalledStreamTests
{
    /// <summary>How long a test is willing to wait for something that should be immediate.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_stalled_stream_is_cancellable()
    {
        // The reader loop is where cancellation has to work, because it is where the time goes. Asserted
        // by cancelling and requiring the enumeration to STOP, rather than by trusting the token is
        // threaded — a blocking read ignores a token it was never given.
        using var stream = new StallingStream();
        using var cancel = new CancellationTokenSource();

        var reading = Task.Run(async () =>
        {
            await foreach (var _ in ServerSentEvents.ReadAsync(stream, Timeout.InfiniteTimeSpan, cancel.Token))
            {
            }
        });

        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        var finished = await Task.WhenAny(reading, Task.Delay(Patience)) == reading;
        finished.Should().BeTrue("a stalled stream must respond to the Stop button");
    }

    [Fact]
    public async Task A_stalled_stream_gives_up_after_the_idle_timeout()
    {
        // The scripted case: no user, no Stop button, and a provider that says nothing. Without this the
        // turn never ends at all.
        using var stream = new StallingStream();

        var reading = Task.Run(async () =>
        {
            await foreach (var _ in ServerSentEvents.ReadAsync(stream, TimeSpan.FromMilliseconds(200)))
            {
            }
        });

        var finished = await Task.WhenAny(reading, Task.Delay(Patience)) == reading;
        finished.Should().BeTrue("the idle timeout must end a stream that says nothing");
        await Assert.ThrowsAsync<TimeoutException>(() => reading);
    }

    [Fact]
    public async Task The_client_reports_a_stalled_provider_instead_of_hanging()
    {
        // Through the client the application uses, because the reader being correct is not the same
        // claim as the client passing it a timeout. This is the reach half.
        using var http = new HttpClient(new StallingHandler()) { Timeout = TimeSpan.FromMilliseconds(400) };
        var client = new OpenAiCompatibleCodegenClient(
            http, "stalled", "Stalled", "https://example.invalid/v1", "m", "k");

        var reading = Task.Run(async () =>
        {
            StrategyCodegenResponse? completed = null;
            await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest("ctx", [])))
                if (evt is CodegenEvent.Completed done) completed = done.Response;
            return completed;
        });

        var finished = await Task.WhenAny(reading, Task.Delay(Patience)) == reading;
        finished.Should().BeTrue("a stalled provider must not hang the turn");

        var response = await reading;
        response.Should().NotBeNull();
        response!.Success.Should().BeFalse();
        response.Error.Should().Contain("stopped sending");
    }

    [Fact]
    public async Task A_stream_that_answers_is_not_cut_off_by_the_idle_timeout()
    {
        // The expensive direction. A reasoning model emits nothing for minutes before its first byte —
        // 278 seconds, measured — so an idle timeout that fired on total elapsed time rather than on
        // silence would abandon exactly the generations worth waiting for. This one sends slowly and
        // must survive.
        using var stream = new TricklingStream(
            TimeSpan.FromMilliseconds(60),
            "data: {\"n\":1}",
            "data: {\"n\":2}",
            "data: [DONE]");

        var seen = new List<int>();
        await foreach (var chunk in ServerSentEvents.ReadAsync(stream, TimeSpan.FromMilliseconds(400)))
            seen.Add(chunk.GetProperty("n").GetInt32());

        seen.Should().Equal(1, 2);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stream that never produces a byte and never ends, and blocks like a socket does.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly ManualResetEventSlim _never = new(false);

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Blocks the calling thread, exactly as a synchronous read on a quiet socket does. Released
            // on Dispose so the test cannot leak a parked thread-pool thread.
            _never.Wait();
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            _never.Set();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that answers, slowly — a line at a time with a pause between them.</summary>
    private sealed class TricklingStream(TimeSpan gap, params string[] lines) : Stream
    {
        private readonly Queue<byte[]> _pending =
            new(lines.Select(line => System.Text.Encoding.UTF8.GetBytes(line + "\n")));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.Count == 0) return 0;

            await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
            var next = _pending.Dequeue();
            next.CopyTo(buffer.Span);
            return next.Length;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers the headers immediately and then says nothing — which is precisely why
    /// <c>HttpClient.Timeout</c> does not save anyone here.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream()),
            });
    }
}
