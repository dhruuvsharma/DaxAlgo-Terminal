using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research.Sandbox;

namespace TradingTerminal.Infrastructure.Research;

public static class ResearchReproServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Paper Lab reproduction backend: the SQLite job store, the paper-ingest client
    /// (Null by default, Http when enabled + a loopback sidecar URL is set), the sandbox runner
    /// selected by <c>SandboxOptions.Kind</c>, and the in-process orchestrator. Everything defaults so
    /// the app builds/runs with NO sidecar and NO Docker — the seams degrade gracefully exactly like the
    /// AI analyst.
    /// </summary>
    public static IServiceCollection AddPaperResearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResearchReproOptions>(configuration.GetSection(ResearchReproOptions.SectionName));
        services.Configure<SandboxOptions>(configuration.GetSection(SandboxOptions.SectionName));

        // Job store: small SQLite DB, independent of the main store backend. Singleton so all consumers
        // (orchestrator, UI) share one write connection.
        services.AddSingleton<IReproJobStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<ResearchReproOptions>>().CurrentValue;
            var path = string.IsNullOrWhiteSpace(opts.JobDatabasePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal", "repro-jobs.db")
                : opts.JobDatabasePath;
            return new ReproJobStore(path);
        });

        // Paper-ingest client: Http when enabled + a sidecar URL is set, else Null (degrade gracefully).
        services.AddHttpClient(HttpPaperIngestClient.HttpClientName, c =>
        {
            // Outer ceiling; the per-call timeout lives in ResearchReproOptions.SidecarTimeoutSeconds.
            c.Timeout = TimeSpan.FromMinutes(5);
        });
        // Always register the Http client — it reads IOptionsMonitor live and already no-ops (IsAvailable
        // = false, every call returns Empty) when the feature is disabled or the URL is blank, so its
        // observable behaviour is identical to the Null client until enabled. Registering it
        // unconditionally is what makes Settings → Research a *live* toggle: flipping Enabled in the user
        // file flips availability without an app restart (the HttpClient itself is only built lazily, on
        // the first call). Mirrors the AI-analyst seam's hot-swap.
        services.AddSingleton<IPaperIngestClient>(sp => new HttpPaperIngestClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptionsMonitor<ResearchReproOptions>>(),
            sp.GetRequiredService<ILogger<HttpPaperIngestClient>>()));

        // Env-resolver client: same hot-swap as the paper-ingest client. Http calls /research/plan
        // (static analysis only, loopback only); Null returns an empty plan so the app runs with no
        // sidecar. The orchestrator falls back to the configured base image when the plan is empty.
        services.AddHttpClient(HttpEnvResolverClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IEnvResolverClient>(sp => new HttpEnvResolverClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptionsMonitor<ResearchReproOptions>>(),
            sp.GetRequiredService<ILogger<HttpEnvResolverClient>>()));

        // Sandbox runner: selected by SandboxOptions.Kind. Docker is the only implemented backend;
        // WSL2 is a stub (IsAvailable=false), RemoteWorker is not registered locally.
        services.AddSingleton<DockerSandboxRunner>();
        services.AddSingleton<Wsl2SandboxRunner>();
        services.AddSingleton<ISandboxRunner>(sp =>
        {
            var kind = sp.GetRequiredService<IOptionsMonitor<SandboxOptions>>().CurrentValue.Kind;
            return kind switch
            {
                SandboxKind.Wsl2 => sp.GetRequiredService<Wsl2SandboxRunner>(),
                _ => sp.GetRequiredService<DockerSandboxRunner>(),
            };
        });

        services.AddSingleton<IReproOrchestrator, LocalReproOrchestrator>();

        // Phase-3 bridge: artifact → InstrumentId-keyed manifest; replication-confidence scorer; and the
        // "save as strategy" registrar the Paper Lab window calls to register a reproduced strategy into
        // IBacktestStrategyRegistry (which the host wires via AddBacktestStrategyCatalog).
        services.AddSingleton<IReproSignalBridge, Bridge.ReproSignalBridge>();
        services.AddSingleton<IReplicationConfidenceScorer, ReplicationConfidenceScorer>();
        services.AddSingleton<Backtest.Strategies.IReproStrategyRegistrar, Backtest.Strategies.ReproStrategyRegistrar>();

        return services;
    }
}
