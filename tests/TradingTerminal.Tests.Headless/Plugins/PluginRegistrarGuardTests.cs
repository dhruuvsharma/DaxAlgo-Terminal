using System;
using System.IO;
using System.Linq;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the registrar guard (<see cref="GuardedServiceCollection"/>): the plugin DI seam is
/// add-only, so a plugin cannot replace a host service — the credential-theft path, since MS.DI
/// resolves the LAST registration and a hostile <c>ICredentialStore</c>/<c>IBrokerSelector</c> would
/// see the user's broker session. Also pins the semantics the nine first-party plugins depend on:
/// multi-registration of the strategy seams, registering their own types, and <c>TryAdd</c> staying a
/// no-op against host services.
/// </summary>
public sealed class PluginRegistrarGuardTests
{
    // ── Host stand-ins ────────────────────────────────────────────────────────────────────────────

    private interface ICredentialStore { string Secret { get; } }

    private sealed class HostCredentialStore : ICredentialStore { public string Secret => "host"; }

    private sealed class HostileCredentialStore : ICredentialStore { public string Secret => "stolen"; }

    private sealed class PluginStrategy : ITradingStrategy
    {
        public string Id => "guard.test";
        public string DisplayName => "Guard Test";
        public string Description => "test";
    }

    private sealed class PluginViewModel;

    /// <summary>A host collection shaped like the real one: a credential store plus an existing
    /// multi-registration strategy seam.</summary>
    private static IServiceCollection HostServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICredentialStore, HostCredentialStore>();
        return services;
    }

    private static GuardedServiceCollection Guard(IServiceCollection host) => new(host, "Test Plugin");

    // ── The attack ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plugin_cannot_replace_a_host_service()
    {
        var host = HostServices();
        var guarded = Guard(host);

        var act = () => guarded.AddSingleton<ICredentialStore, HostileCredentialStore>();

        act.Should().Throw<PluginPolicyViolationException>()
            .Which.ServiceType.Should().Be<ICredentialStore>();
    }

    [Fact]
    public void Host_service_survives_a_rejected_plugin_and_still_resolves_to_the_host_implementation()
    {
        var host = HostServices();
        var guarded = Guard(host);

        try { guarded.AddSingleton<ICredentialStore, HostileCredentialStore>(); }
        catch (PluginPolicyViolationException) { /* expected */ }

        // Nothing was committed, so the host collection is untouched.
        using var provider = host.BuildServiceProvider();
        provider.GetRequiredService<ICredentialStore>().Should().BeOfType<HostCredentialStore>();
        provider.GetServices<ICredentialStore>().Should().ContainSingle();
    }

    [Fact]
    public void A_violation_midway_through_Register_commits_NOTHING()
    {
        // The realistic shape: a plugin registers a legitimate strategy first, then tries to slip in a
        // hostile credential store. Staging means the legitimate half never lands either — the whole
        // plugin is refused rather than half-applied.
        var host = HostServices();
        var guarded = Guard(host);

        guarded.AddSingleton<ITradingStrategy, PluginStrategy>();
        var act = () => guarded.AddSingleton<ICredentialStore, HostileCredentialStore>();
        act.Should().Throw<PluginPolicyViolationException>();

        // Commit is never called by the loader on a violation.
        using var provider = host.BuildServiceProvider();
        provider.GetServices<ITradingStrategy>().Should().BeEmpty();
        provider.GetRequiredService<ICredentialStore>().Should().BeOfType<HostCredentialStore>();
    }

    [Fact]
    public void Plugin_cannot_remove_or_clear_host_registrations()
    {
        var host = HostServices();
        var guarded = Guard(host);
        var hostDescriptor = host[0];

        guarded.Invoking(g => g.Clear()).Should().Throw<PluginPolicyViolationException>();
        guarded.Invoking(g => g.Remove(hostDescriptor)).Should().Throw<PluginPolicyViolationException>();
        guarded.Invoking(g => g.RemoveAt(0)).Should().Throw<PluginPolicyViolationException>();
        guarded.Invoking(g => g[0] = ServiceDescriptor.Singleton<ICredentialStore, HostileCredentialStore>())
            .Should().Throw<PluginPolicyViolationException>();

        host.Should().ContainSingle();
    }

    // ── What a legitimate plugin must still be able to do ─────────────────────────────────────────

    [Fact]
    public void Plugin_can_add_the_multi_registration_strategy_seams_and_its_own_types()
    {
        var host = HostServices();
        var guarded = Guard(host);

        // Exactly what every first-party AddXxxStrategy() does.
        guarded.AddSingleton<ITradingStrategy, PluginStrategy>();
        guarded.AddTransient<PluginViewModel>();
        guarded.AddSingleton(new BacktestStrategyOption(
            Id: "guardTest", DisplayName: "Guard Test", Build: _ => null!));
        guarded.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "guard.test", ViewFactory: _ => null!, ViewModelFactory: _ => null!));

        var registered = guarded.Commit();

        using var provider = host.BuildServiceProvider();
        provider.GetServices<ITradingStrategy>().Should().ContainSingle().Which.Should().BeOfType<PluginStrategy>();
        provider.GetServices<BacktestStrategyOption>().Should().ContainSingle();
        provider.GetServices<StrategyFactoryRegistration>().Should().ContainSingle();
        provider.GetRequiredService<PluginViewModel>().Should().NotBeNull();
        registered.Should().HaveCount(4);
    }

    [Fact]
    public void Two_plugins_both_contribute_strategies_and_neither_can_shadow_the_other()
    {
        var host = HostServices();

        var first = new GuardedServiceCollection(host, "First");
        first.AddSingleton<ITradingStrategy, PluginStrategy>();
        first.AddTransient<PluginViewModel>();
        first.Commit();

        // The second plugin's guard snapshots the host AFTER the first committed, so the first
        // plugin's own types are now off limits — but the allowlisted strategy seam is still open.
        var second = new GuardedServiceCollection(host, "Second");
        second.AddSingleton<ITradingStrategy, PluginStrategy>();
        second.Invoking(g => g.AddTransient<PluginViewModel>())
            .Should().Throw<PluginPolicyViolationException>();
        second.Commit();

        using var provider = host.BuildServiceProvider();
        provider.GetServices<ITradingStrategy>().Should().HaveCount(2);
    }

    [Fact]
    public void TryAdd_of_a_host_service_is_a_silent_no_op_not_a_violation()
    {
        // The host's descriptors stay visible through the read view precisely so this keeps working:
        // a plugin defensively TryAdd-ing a service the host already registered must no-op, not throw.
        var host = HostServices();
        var guarded = Guard(host);

        guarded.Invoking(g => g.TryAddSingleton<ICredentialStore, HostileCredentialStore>()).Should().NotThrow();
        guarded.Staged.Should().BeEmpty();

        guarded.Commit();
        using var provider = host.BuildServiceProvider();
        provider.GetRequiredService<ICredentialStore>().Should().BeOfType<HostCredentialStore>();
    }

    [Fact]
    public void Registering_its_own_type_twice_is_allowed()
    {
        var host = HostServices();
        var guarded = Guard(host);

        guarded.AddTransient<PluginViewModel>();
        guarded.Invoking(g => g.AddTransient<PluginViewModel>()).Should().NotThrow();

        guarded.Commit();
        host.Count(d => d.ServiceType == typeof(PluginViewModel)).Should().Be(2);
    }

    // ── The loader path ───────────────────────────────────────────────────────────────────────────

    /// <summary>A hostile plugin: registers a plausible strategy, then hands the host its own
    /// <c>ICredentialStore</c>. Driven through the same call the loader makes, so the staging +
    /// violation path is exercised exactly as at startup.</summary>
    private sealed class HostilePlugin : IStrategyPlugin
    {
        public string Name => "Hostile";
        public string TargetSdkVersion => SdkInfo.Version;

        public void Register(IPluginRegistrar registrar)
        {
            registrar.Services.AddSingleton<ITradingStrategy, PluginStrategy>();
            registrar.Services.AddSingleton<ICredentialStore, HostileCredentialStore>();
        }
    }

    [Fact]
    public void A_hostile_Register_throws_and_leaves_the_host_collection_pristine()
    {
        var host = HostServices();
        var before = host.Count;
        var guarded = Guard(host);

        var act = () => new HostilePlugin().Register(
            new TestRegistrar(guarded, new PluginContext("Hostile", string.Empty, SdkInfo.Version)));

        act.Should().Throw<PluginPolicyViolationException>()
            .WithMessage("*ICredentialStore*may not replace host services*");
        host.Should().HaveCount(before);
    }

    private sealed class TestRegistrar(IServiceCollection services, PluginContext context) : IPluginRegistrar
    {
        public IServiceCollection Services { get; } = services;
        public PluginContext Context { get; } = context;
    }

    // ── End-to-end: a real hostile plugin assembly, through the real loader ───────────────────────

    /// <summary>
    /// The whole point, proven against a genuine plugin DLL on disk rather than an in-process fake:
    /// compile a plugin that grabs the host's <see cref="IMarketDataStore"/>, let the real loader
    /// discover it, load it into its own ALC, and run its <c>Register</c>. It must be classified
    /// <see cref="PluginLoadOutcome.PolicyViolation"/>, quarantined so it doesn't get a second run,
    /// and leave the host's store registration untouched.
    /// </summary>
    [Fact]
    public void A_hostile_plugin_assembly_is_blocked_quarantined_and_registers_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "daxalgo-tests", "guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            CompileHostilePlugin(root, "Hostile");
            var state = new PluginStateStore(root);
            var services = new ServiceCollection();
            services.AddSingleton<IMarketDataStore>(_ => throw new InvalidOperationException("host store"));

            var report = PluginLoader.LoadWithReport(services, root, SdkInfo.Version, state);

            report.Loaded.Should().BeEmpty();
            var problem = report.Problems.Should().ContainSingle().Subject;
            problem.PluginFolderName.Should().Be("Hostile");
            problem.Outcome.Should().Be(PluginLoadOutcome.PolicyViolation);
            problem.Reason.Should().Contain(nameof(IMarketDataStore));

            state.QuarantineFor("Hostile").Should().NotBeNull("a plugin that reached for a host service gets one shot, not one per startup");
            services.Count(d => d.ServiceType == typeof(IMarketDataStore))
                .Should().Be(1, "the host's own store registration must be the only one");
            services.Should().NotContain(d => d.ServiceType == typeof(ITradingStrategy),
                "the strategy it registered BEFORE the violation must not be committed either");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Emits <c>{root}/{name}/{name}.dll</c> — a plugin that registers a legitimate strategy
    /// and then hijacks <see cref="IMarketDataStore"/>. Contract assemblies (DaxAlgo.Sdk,
    /// TradingTerminal.*, Microsoft.Extensions.*) are shared into the plugin's load context, so the
    /// compiled types have the same identity as the host's.</summary>
    private static void CompileHostilePlugin(string root, string name)
    {
        var source = $$"""
            using DaxAlgo.Sdk;
            using Microsoft.Extensions.DependencyInjection;
            using TradingTerminal.Core.MarketData;
            using TradingTerminal.Core.Strategies;

            public sealed class HostileStrategy : ITradingStrategy
            {
                public string Id => "hostile";
                public string DisplayName => "Hostile";
                public string Description => "looks legitimate";
            }

            public sealed class HostilePlugin : IStrategyPlugin
            {
                public string Name => "Hostile";
                public string TargetSdkVersion => "{{SdkInfo.Version}}";

                public void Register(IPluginRegistrar registrar)
                {
                    // A plausible-looking strategy first...
                    registrar.Services.AddSingleton<ITradingStrategy, HostileStrategy>();
                    // ...then the payload: MS.DI is last-wins, so this would hand the app a store the
                    // plugin controls — every quote, trade and depth snapshot the terminal persists.
                    registrar.Services.AddSingleton<IMarketDataStore>(_ => null!);
                }
            }
            """;

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: name,
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var folder = Directory.CreateDirectory(Path.Combine(root, name));
        var result = compilation.Emit(Path.Combine(folder.FullName, name + ".dll"));
        result.Success.Should().BeTrue(
            string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
}
