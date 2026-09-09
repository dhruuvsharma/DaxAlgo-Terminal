using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

/// <summary>What a whole gauntlet pass concluded.</summary>
/// <param name="Verdicts">Every critic's answer, including the ones that could not run.</param>
/// <param name="Skipped">True when the whole pass was skipped because nothing had changed since the
/// last one.</param>
public sealed record GauntletResult(IReadOnlyList<CriticVerdict> Verdicts, bool Skipped = false)
{
    /// <summary>Every finding, worst panel first — the picture is what a user sees.</summary>
    public IReadOnlyList<VerificationFinding> Findings =>
    [
        .. Verdicts.Where(v => v.Panel == CriticPanel.Picture).SelectMany(v => v.Findings),
        .. Verdicts.Where(v => v.Panel == CriticPanel.Quant).SelectMany(v => v.Findings),
    ];

    /// <summary>Critics that actually ran and had nothing to say.</summary>
    public int Cleared => Verdicts.Count(v => v.Passes);

    /// <summary>Critics that could not run at all. Never counted as passes.</summary>
    public int Unavailable => Verdicts.Count(v => !v.Ran);

    /// <summary>Nothing to fix. <b>Not the same as "no findings"</b>: a pass in which every critic was
    /// unavailable found nothing and checked nothing, and the cheapest way to satisfy any quality gate
    /// is to arrange that none of it runs.</summary>
    public bool Passed => Cleared > 0 && Findings.Count == 0;

    public string Summary => Skipped
        ? "Review skipped — nothing changed since the last one."
        : $"{Cleared} critic(s) satisfied, {Findings.Count} finding(s)"
          + (Unavailable > 0 ? $", {Unavailable} could not run" : string.Empty);
}

/// <summary>
/// The Gauntlet: fresh-context critics judging a finished unit against a concrete bar, in parallel,
/// and the findings going back to whoever owns the broken file.
///
/// <para><b>It runs behind the ladder, never instead of it.</b> The ladder is deterministic and free;
/// critics cost the user money per call. A unit that does not compile is never shown to one.</para>
///
/// <para><b>And it does not re-run over an unchanged artifact.</b> Prime Agent's autonomous gate skips
/// a verification command when the workspace has not moved since the last attempt, and this harness has
/// a measured reason to want that rule: a hundred and forty repair turns, every one re-judging code
/// that had not changed. A critic pass over an identical subject returns an identical verdict and costs
/// exactly as much as one that would not.</para>
/// </summary>
public sealed class GauntletLoop(IReadOnlyList<IUnitCritic> critics, ILogger? logger = null)
{
    private readonly IReadOnlyList<IUnitCritic> _critics =
        critics ?? throw new ArgumentNullException(nameof(critics));

    /// <summary>The fingerprint of the last subject judged, so an unchanged one is not judged twice.</summary>
    private string? _lastJudged;

    /// <summary>How many critics may be in flight at once.</summary>
    public const int MaximumParallel = 3;

    /// <summary>
    /// Runs every applicable critic over one subject.
    /// </summary>
    /// <param name="maxParallel">Bounded for the same reason the builders are: a burst of six calls to
    /// one provider earns 429s, and an agent CLI answers it by starting six processes.</param>
    public async Task<GauntletResult> RunAsync(
        GauntletSubject subject,
        ReferenceBar bar,
        int maxParallel = MaximumParallel,
        IProgress<CriticVerdict>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(bar);

        if (_critics.Count == 0) return new GauntletResult([]);

        var fingerprint = subject.Fingerprint;
        if (_lastJudged == fingerprint)
        {
            logger?.LogInformation("Gauntlet skipped: the subject has not changed.");
            return new GauntletResult([], Skipped: true);
        }

        using var slots = new SemaphoreSlim(Math.Max(1, maxParallel));

        var running = _critics.Select(async critic =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var verdict = await critic.JudgeAsync(subject, bar, ct).ConfigureAwait(false);
                progress?.Report(verdict);
                return verdict;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A critic that throws is a fault in the critic, not in the candidate. Letting it
                // escape would fail the review of somebody's strategy and report nothing about the
                // strategy — the same rule the ladder's guarded runner already follows.
                logger?.LogWarning(ex, "Critic {Critic} threw.", critic.Id);
                var skipped = CriticVerdict.Skipped(critic.Id, critic.Panel, $"The critic itself failed: {ex.Message}");
                progress?.Report(skipped);
                return skipped;
            }
            finally
            {
                slots.Release();
            }
        });

        var verdicts = await Task.WhenAll(running).ConfigureAwait(false);

        // Recorded only once a pass has actually completed. Marking it before would let a cancelled
        // run leave a fingerprint claiming work that never happened.
        _lastJudged = fingerprint;
        return new GauntletResult(verdicts);
    }

    /// <summary>
    /// Builds the panel for one unit against one provider pair.
    /// </summary>
    /// <param name="build">The model the user picked, which does the reading.</param>
    /// <param name="vision">A model that can be shown pictures, when one is configured. <b>Only the
    /// picture critic is routed to it</b> — the build stays where the user put it, and a critic call is
    /// one image and a rubric, so the cost of borrowing a second provider for it is small.</param>
    public static GauntletLoop For(
        IStrategyCodegenClient build,
        IStrategyCodegenClient? vision,
        string sharedContext,
        AuthoringKind kind,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(build);

        var buildSees = AiModelCatalog.SupportsVision(build.ProviderId, build.Model);
        var visionSees = vision is not null && AiModelCatalog.SupportsVision(vision.ProviderId, vision.Model);

        var panel = CriticPrompts.For(kind).Select(IUnitCritic (definition) =>
        {
            var useVision = definition.NeedsPicture && !buildSees && visionSees;
            var client = useVision ? vision! : build;
            return new ModelCritic(
                client, definition, sharedContext,
                canSeeImages: AiModelCatalog.SupportsVision(client.ProviderId, client.Model));
        });

        return new GauntletLoop([.. panel], logger);
    }
}
