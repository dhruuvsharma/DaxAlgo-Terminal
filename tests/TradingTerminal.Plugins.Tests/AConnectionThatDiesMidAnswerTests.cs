using System.IO;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The connection dies after the stream opened.
///
/// <para><b>The third place in this client to learn that an iterator may not yield from a catch.</b>
/// Sending was guarded, stalling was guarded, and reading the body was not — so a network drop
/// mid-generation threw straight out of the iterator, past the drain, past the builder, and out of the
/// whole run.</para>
///
/// <para>Measured: two live runs, an hour and eighteen minutes each, four finished files between them,
/// all discarded because DNS failed on this machine while two builders were mid-stream. Every other
/// provider failure in this pipeline is reported and survivable; this one alone was fatal, and only
/// because of where the catch was missing.</para>
///
/// <para>Shared by both streaming clients — the Anthropic one calls the same helper — so both had the
/// hole and both are closed by the same fix.</para>
/// </summary>
public sealed class AConnectionThatDiesMidAnswerTests
{
    /// <summary>Yields part of an answer, then fails the way a dropped connection does.</summary>
    private sealed class DiesAfter(int chunks, Exception how) : IAsyncEnumerator<JsonElement>
    {
        private int _served;

        public JsonElement Current { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_served++ < chunks)
            {
                Current = JsonDocument.Parse("""{"choices":[{"delta":{"content":"half a "}}]}""").RootElement;
                return ValueTask.FromResult(true);
            }

            throw how;
        }
    }

    /// <summary>Reads the part-answer the model did send, then the failure that ended it.</summary>
    private static async Task<(bool Moved, string? Stalled, string? Broken)> AfterOneChunk(
        Exception how, CancellationToken ct = default)
    {
        var chunks = new DiesAfter(1, how);
        await OpenAiCompatibleCodegenClient.TryMoveAsync(chunks, "TokenRouter", ct);
        return await OpenAiCompatibleCodegenClient.TryMoveAsync(chunks, "TokenRouter", ct);
    }

    [Theory]
    [InlineData("No such host is known. (api.tokenrouter.com:443)")]
    [InlineData("The response ended prematurely.")]
    public async Task A_dropped_connection_is_reported_rather_than_thrown(string message)
    {
        // The whole point: this must come back as an answer the run can act on, not as an exception
        // that unwinds it.
        var (moved, stalled, broken) = await AfterOneChunk(new HttpRequestException(message));

        moved.Should().BeFalse();
        stalled.Should().BeNull("a drop is not a stall — the two need different advice");
        broken.Should().Contain(message);
    }

    [Fact]
    public async Task An_IO_failure_mid_body_is_the_same_thing()
    {
        // What a socket reset actually surfaces as while the body is being read.
        var (_, _, broken) = await AfterOneChunk(
            new IOException("Unable to read data from the transport connection."));

        broken.Should().NotBeNull();
    }

    [Fact]
    public async Task A_stall_is_still_classified_as_a_stall()
    {
        // The advice differs — raise the timeout, versus the connection went away — so the two must not
        // collapse into one message.
        var (_, stalled, broken) = await AfterOneChunk(new TimeoutException("No data for 300s."));

        stalled.Should().Contain("300s");
        broken.Should().BeNull();
    }

    [Fact]
    public async Task Stop_still_throws()
    {
        // Cancellation is not a provider failure and must not be reported as one: the run has its own
        // path for it, and turning Stop into "the provider broke" would be a lie in the transcript.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OpenAiCompatibleCodegenClient.TryMoveAsync(
                new DiesAfter(0, new OperationCanceledException(cts.Token)), "TokenRouter", cts.Token));
    }
}
