using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Execution.Routing;

/// <summary>
/// Registration for routed brokers: the shared options. The routes themselves are registered by the
/// infrastructure layer as <c>IBrokerOrderRoute</c>, and the execution console turns each into a broker card.
/// </summary>
public static class RoutedExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RoutedExecutionOptions"/>. Nothing is armed: every routed card starts in PAPER
    /// (or disabled, for a broker without one), and LIVE needs the owner option, stored credentials and the
    /// typed confirmation for the exact account.
    /// </summary>
    public static IServiceCollection AddRoutedExecution(this IServiceCollection services, Action<RoutedExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RoutedExecutionOptions();
        configure?.Invoke(options);
        var fault = options.Validate();
        if (fault is not null)
            throw new InvalidOperationException($"{RoutedExecutionOptions.SectionName}: {fault}");
        services.AddSingleton(options.Snapshot());
        return services;
    }
}
