using System.Text.Json;
using TradingTerminal.Backtest.Protocol;

namespace TradingTerminal.Backtest.Worker;

/// <summary>Serializes a finite number of coarse progress records to stdout as NDJSON.</summary>
internal sealed class WorkerProgressEmitter(string jobId, int maxMessages)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _sequence;
    private int _messages;

    public async Task EmitAsync(
        BacktestWorkerPhase phase,
        string? message = null,
        long? eventsProcessed = null,
        long? eventsTotal = null,
        double? percentComplete = null,
        bool isHeartbeat = false,
        CancellationToken ct = default)
    {
        // Terminal/phase messages are never lost. Heartbeats stop at the configured finite ceiling.
        if (isHeartbeat && Volatile.Read(ref _messages) >= maxMessages - 2) return;

        var progress = new BacktestJobProgress
        {
            JobId = jobId,
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUtc = DateTime.UtcNow,
            Phase = phase,
            Message = message,
            EventsProcessed = eventsProcessed,
            EventsTotal = eventsTotal,
            PercentComplete = percentComplete,
            IsHeartbeat = isHeartbeat,
        };

        var line = BacktestProtocolJson.Serialize(progress);
        if (line.Length > BacktestProtocolLimits.MaxProgressLineCharacters)
            throw new InvalidDataException("A worker progress record exceeded the protocol line limit.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await Console.Out.FlushAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _messages);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RunHeartbeatAsync(long? eventsTotal, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await EmitAsync(
                    BacktestWorkerPhase.Running,
                    "Worker heartbeat.",
                    eventsTotal: eventsTotal,
                    isHeartbeat: true,
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when execution reaches a terminal phase.
        }
    }
}
