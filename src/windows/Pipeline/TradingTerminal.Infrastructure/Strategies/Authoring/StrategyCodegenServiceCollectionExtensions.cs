using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<StrategyCodegenOrchestrator>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiCodegenOptions>>().Value;
            var keys = sp.GetRequiredService<IAiKeyResolver>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new StrategyCodegenClientFactory(() => httpFactory.CreateClient("ai-codegen"), options, keys.Resolve);
        });
        services.AddSingleton(sp =>
            new AiStrategyBuilder(
                sp.GetRequiredService<StrategyCodegenClientFactory>(),
                sp.GetRequiredService<StrategyCodegenOrchestrator>(),
                sp.GetRequiredService<StrategyContextPack>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiCodegenOptions>>().Value));
        services.AddSingleton<IAiStrategyBuilder>(sp => sp.GetRequiredService<AiStrategyBuilder>());

        return services;
    }
}
