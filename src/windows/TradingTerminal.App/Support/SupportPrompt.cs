using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.App.Support;

/// <summary>
/// Default <see cref="ISupportPrompt"/>. Shows the thank-you / feedback window at most once per
/// launch, after a short randomised delay so it feels organic rather than jarring. Keeps a single
/// live instance so the Help menu re-activates the existing window instead of stacking copies.
/// </summary>
internal sealed class SupportPrompt : ISupportPrompt
{
    // v1: show every launch (WinRAR-style), just at a random moment. Dial this below 1.0 to make
    // the popup probabilistic instead of every-launch.
    private const double LaunchShowProbability = 1.0;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(9);

    private readonly IServiceProvider _services;
    private readonly ILogger<SupportPrompt> _logger;
    private readonly Random _rng = new();

    private bool _firedThisLaunch;
    private SupportWindow? _current;

    public SupportPrompt(IServiceProvider services, ILogger<SupportPrompt> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void MaybeShowOnLaunch(Window owner)
    {
        if (_firedThisLaunch) return;
        _firedThisLaunch = true;

        if (_rng.NextDouble() > LaunchShowProbability)
        {
            _logger.LogDebug("Support prompt skipped this launch by random gate.");
            return;
        }

        var delaySeconds = MinDelay.TotalSeconds +
                           (_rng.NextDouble() * (MaxDelay.TotalSeconds - MinDelay.TotalSeconds));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // The user may have closed the app during the delay — only show if the owner is still up.
            if (owner.IsLoaded && owner.IsVisible) Show(owner);
        };
        timer.Start();
    }

    public void Show(Window owner)
    {
        if (_current is not null)
        {
            _current.Activate();
            return;
        }

        var window = _services.GetRequiredService<SupportWindow>();
        window.DataContext = _services.GetRequiredService<SupportViewModel>();
        window.Owner = owner;
        window.Closed += (_, _) => _current = null;
        _current = window;
        window.Show();
        _logger.LogInformation("Shown support / feedback window.");
    }
}
