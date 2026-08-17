using System;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Core.Updates;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// The checker registered when no feed URL or no pinned key is configured. It answers
/// <see cref="UpdateOutcome.NotConfigured"/> immediately so the rest of the app has one code path
/// whether or not updates are switched on, and never touches the network.
///
/// It doubles as the <see cref="IUpdateNotifier"/> for that off state — an event that never fires and
/// a null <see cref="Latest"/> — so the shell resolves the same dependency either way and the notice
/// view-model needs no "is the feature on?" branch.
/// </summary>
public sealed class NullUpdateChecker(Version current) : IUpdateChecker, IUpdateNotifier
{
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpdateCheckResult(
            UpdateOutcome.NotConfigured, current, Detail: "No update feed is configured."));

#pragma warning disable CS0067 // Deliberately never raised: this is the switched-off notifier.
    public event Action<UpdateCheckResult>? UpdateAvailable;
#pragma warning restore CS0067

    public UpdateCheckResult? Latest => null;
}
