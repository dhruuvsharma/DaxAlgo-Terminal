using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>Wires the AI Strategy Builder into DI. Called once per shell from <c>AddStrategyPlugins</c>.</summary>
public static class StrategyCodegenServiceCollectionExtensions
{
    /// <summary>
    /// Registers the codegen backend: the bound <see cref="AiCodegenOptions"/>, the embedded context
    /// pack, the build-loop orchestrator, the provider factory, and <see cref="IAiStrategyBuilder"/>.
    /// Keys are resolved through <see cref="IAiKeyResolver"/> — a shell that can read its credential
    /// store registers one before calling this; otherwise the <see cref="IAiKeyResolver.Null"/> fallback
    /// leaves only keyless providers (installed agent CLIs, local Ollama) usable, which is a valid state.
    /// <see cref="StrategyCodegenOrchestrator"/> depends on <c>IStrategyCompiler</c>, already registered
    /// by <c>AddStrategyPlugins</c>.
    /// </summary>
    public static IServiceCollection AddStrategyCodegen(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiCodegenOptions>(configuration.GetSection(AiCodegenOptions.SectionName));
        services.TryAddSingleton(IAiKeyResolver.Null);

        // One HttpClient per keyed request via the factory (short-lived; codegen calls are infrequent).
        services.AddHttpClient();

        services.AddSingleton(_ => StrategyContextPack.Load());
        // On-demand domain packs (order flow, quant math, risk/exits, live window, instruments). Selected
        // once per conversation from the brief, so the system prompt stays cacheable.
        services.AddSingleton(_ => StrategySkillLibrary.Load());
        services.AddSingleton<StrategyCodegenOrchestrator>();

        // The critics' reference bar. Registered whether or not a key is configured: an unconfigured
        // install gets a search that finds nothing and says so, and the review falls back to the
        // rubric the planner wrote — weaker, but real, and nothing about the build depends on a key
        // the user has not obtained.
        services.AddSingleton<Reference.IReferenceSearch>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiCodegenOptions>>().Value;
            if (!options.Search.IsConfigured) return Reference.NullReferenceSearch.Instance;

            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new Reference.BraveReferenceSearch(
                httpFactory.CreateClient("reference-search"),
                options.Search,
                sp.GetService<ILoggerFactory>()?.CreateLogger<Reference.BraveReferenceSearch>());
        });

        services.AddSingleton<Reference.ReferenceBarBuilder>(sp => new Reference.ReferenceBarBuilder(
            sp.GetRequiredService<Reference.IReferenceSearch>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<Reference.ReferenceBarBuilder>()));

        // ONE browser sign-in wrapper for the whole app. The provider factory asks it whether signing in
        // is possible, and so does the settings pane's Sign in button; two independently constructed
        // wrappers can disagree, and did — the pane greyed the button out while the provider list
        // offered the signed-in client as available.
        services.AddSingleton<AnthropicOAuthCli>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiCodegenOptions>>().Value;
            var keys = sp.GetRequiredService<IAiKeyResolver>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new StrategyCodegenClientFactory(
                () => httpFactory.CreateClient("ai-codegen"),
                options,
                keys.Resolve,
                sp.GetRequiredService<AnthropicOAuthCli>());
        });
        services.AddSingleton(sp =>
            new AiStrategyBuilder(
                sp.GetRequiredService<StrategyCodegenClientFactory>(),
                sp.GetRequiredService<StrategyCodegenOrchestrator>(),
                sp.GetRequiredService<StrategyContextPack>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiCodegenOptions>>().Value));
        services.AddSingleton<IAiStrategyBuilder>(sp => sp.GetRequiredService<AiStrategyBuilder>());

        // The interactive escape hatch: scaffolds a Hyperion workspace (context pack + skill packs +
        // starter project) and opens an installed agent CLI in a real terminal there.
        services.AddSingleton<ICliWorkspaceLauncher, CliWorkspaceLauncher>();

        return services;
    }
}
