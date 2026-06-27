using Microsoft.Extensions.Logging;

namespace TradingTerminal.UI;

/// <summary>
/// Helpers for safely fire-and-forgetting tasks from synchronous view-model code (e.g.
/// publishing a notification from an event handler). Bare <c>_ = task</c> swallows
/// exceptions; this logs them instead so silent failures show up in the Logs pane.
/// </summary>
public static class TaskExtensions
{
    /// <summary>Fires the task and logs any exception via <paramref name="logger"/>.</summary>
    public static void FireAndForgetSafe(this Task task, ILogger logger, string? context = null)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = task.ContinueWith(
            t => logger.LogWarning(t.Exception, "Fire-and-forget task failed ({Context})", context ?? "unspecified"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>ValueTask flavour for hot paths (publisher.PublishAsync returns ValueTask).</summary>
    public static void FireAndForgetSafe(this ValueTask task, ILogger logger, string? context = null)
    {
        if (task.IsCompletedSuccessfully) return;
        task.AsTask().FireAndForgetSafe(logger, context);
    }
}
