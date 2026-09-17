using System.Globalization;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Wraps a provider so a run survives a subscription's usage window closing.
///
/// <para>A Claude plan is not metered per token, it is metered per window: when the window is spent the
/// CLI refuses and says when it reopens. That is not a failure of the request — the same request
/// succeeds unchanged an hour later — so the harness waits and sends it again rather than reporting a
/// build as broken because the clock ran out. A four-hour swarm will cross a five-hour window often
/// enough that not handling it means never finishing one.</para>
///
/// <para><b>Deliberately here and not in the product.</b> Silently sleeping for an hour inside the
/// authoring pane is a decision about somebody's afternoon, and the pane has a Stop button and a status
/// line precisely so a wait can be shown rather than hidden. This is a harness that runs unattended,
/// which is the one context where sleeping is the right answer.</para>
/// </summary>
public sealed class WaitsOutTheUsageLimit(
    IStrategyCodegenClient inner,
    Action<string> say,
    TimeSpan? fallbackWait = null,
    Func<TimeSpan, CancellationToken, Task>? sleep = null) : IStrategyCodegenClient
{
    /// <summary>When the refusal names no reset time. Long enough not to hammer the limit, short enough
    /// that a window that reopened early is not slept through.</summary>
    private readonly TimeSpan _fallback = fallbackWait ?? TimeSpan.FromMinutes(20);

    private readonly Func<TimeSpan, CancellationToken, Task> _sleep =
        sleep ?? ((span, ct) => Task.Delay(span, ct));

    /// <summary>How many windows one call may wait through before it is treated as a real failure.
    /// Bounded so a misread message cannot turn into an infinite sleep.</summary>
    public const int MaximumWaits = 12;

    public string ProviderId => inner.ProviderId;
    public string DisplayName => inner.DisplayName;
    public bool IsAvailable => inner.IsAvailable;
    public string Model => inner.Model;

    public async Task<StrategyCodegenResponse> GenerateAsync(
        StrategyCodegenRequest request, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await inner.GenerateAsync(request, ct).ConfigureAwait(false);

            if (response.Success || attempt >= MaximumWaits) return response;
            if (WaitFor(response.Error) is not { } wait) return response;

            say($"usage window spent — waiting {Describe(wait)} then sending the same request again "
                + $"(attempt {attempt + 1} of {MaximumWaits}): {Trim(response.Error)}");

            await _sleep(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long to wait, or null when this is not a usage-limit refusal at all.
    ///
    /// <para>The reset time is taken from the message when it carries one, because a CLI that says
    /// "resets 3pm" knows something this code cannot work out — and waiting the fallback instead would
    /// either hammer a closed window or sleep long past an open one.</para>
    /// </summary>
    internal TimeSpan? WaitFor(string? error, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        if (!IsUsageLimit(error)) return null;

        var at = now ?? DateTimeOffset.Now;

        // THE MACHINE-READABLE ONE FIRST. Claude Code's stream carries a rate_limit_event with
        // "resetsAt" as a unix timestamp — measured on a live probe, alongside the window's
        // utilisation — and a number the other end computed beats anything read out of its prose.
        var epoch = Regex.Match(error, @"resetsAt""?\s*[:=]\s*(\d{10,13})", RegexOptions.IgnoreCase);
        if (epoch.Success && long.TryParse(epoch.Groups[1].Value, out var stamp))
        {
            var reset = stamp > 9_999_999_999L
                ? DateTimeOffset.FromUnixTimeMilliseconds(stamp)
                : DateTimeOffset.FromUnixTimeSeconds(stamp);

            if (reset > at) return reset - at + TimeSpan.FromMinutes(1);
        }

        // "resets at 3pm", "resets 15:00", "resets at 3:30pm"
        var clock = Regex.Match(
            error, @"resets?\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?", RegexOptions.IgnoreCase);

        if (clock.Success && int.TryParse(clock.Groups[1].Value, out var hour))
        {
            var minute = clock.Groups[2].Success && int.TryParse(clock.Groups[2].Value, out var m) ? m : 0;
            var meridiem = clock.Groups[3].Value.ToLowerInvariant();

            if (meridiem == "pm" && hour < 12) hour += 12;
            if (meridiem == "am" && hour == 12) hour = 0;

            if (hour is >= 0 and < 24 && minute is >= 0 and < 60)
            {
                var target = new DateTimeOffset(at.Year, at.Month, at.Day, hour, minute, 0, at.Offset);
                if (target <= at) target = target.AddDays(1);

                // A minute past the stated time: reopening exactly on the boundary is a race with
                // whatever clock the other end is using.
                return target - at + TimeSpan.FromMinutes(1);
            }
        }

        // "try again in 42 minutes", "retry after 90 seconds"
        var relative = Regex.Match(
            error, @"(?:in|after)\s+(\d+)\s*(second|minute|hour)s?", RegexOptions.IgnoreCase);

        if (relative.Success
            && int.TryParse(relative.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            var span = relative.Groups[2].Value.ToLowerInvariant() switch
            {
                "second" => TimeSpan.FromSeconds(count),
                "hour" => TimeSpan.FromHours(count),
                _ => TimeSpan.FromMinutes(count),
            };

            return span + TimeSpan.FromMinutes(1);
        }

        return _fallback;
    }

    /// <summary>
    /// Whether this refusal is the window closing rather than the request being wrong.
    ///
    /// <para>Matched on several phrasings on purpose. The CLI's wording is not a contract and has
    /// changed before; treating an unrecognised refusal as a hard failure is the safe direction, since
    /// the cost is a run that stops early rather than one that sleeps for ever on a genuine error.</para>
    /// </summary>
    internal static bool IsUsageLimit(string error) =>
        error.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
        || error.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
        || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || error.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || error.Contains("429", StringComparison.Ordinal)
        || (error.Contains("resets", StringComparison.OrdinalIgnoreCase)
            && error.Contains("limit", StringComparison.OrdinalIgnoreCase));

    public async IAsyncEnumerable<CodegenEvent> StreamAsync(
        StrategyCodegenRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            StrategyCodegenResponse? completed = null;

            await foreach (var evt in inner.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (evt is CodegenEvent.Completed done)
                {
                    completed = done.Response;

                    // Held back until the retry is decided. Forwarding it would tell the run the turn
                    // failed, and the run would act on that before the wait even began.
                    if (!done.Response.Success
                        && attempt < MaximumWaits
                        && WaitFor(done.Response.Error) is not null)
                    {
                        break;
                    }
                }

                yield return evt;
            }

            if (completed is null || completed.Success || attempt >= MaximumWaits) yield break;
            if (WaitFor(completed.Error) is not { } wait) yield break;

            say($"usage window spent — waiting {Describe(wait)} then sending the same request again "
                + $"(attempt {attempt + 1} of {MaximumWaits}): {Trim(completed.Error)}");

            // Kept alive while it waits, so a stall detector upstream does not conclude the provider
            // has died during an hour of deliberate silence.
            yield return new CodegenEvent.TextDelta(string.Empty);

            await _sleep(wait, ct).ConfigureAwait(false);
        }
    }

    private static string Describe(TimeSpan wait) =>
        wait.TotalMinutes < 90d
            ? $"{wait.TotalMinutes:F0} minute(s)"
            : $"{wait.TotalHours:F1} hour(s)";

    private static string Trim(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(no message)"
        : text.Length <= 200 ? text.Trim()
        : text.Trim()[..200] + "…";
}
