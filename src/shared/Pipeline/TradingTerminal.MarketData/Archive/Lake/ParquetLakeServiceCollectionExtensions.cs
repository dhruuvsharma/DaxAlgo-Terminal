using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.MarketData.Archive.Lake;

public static class ParquetLakeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local Parquet lake exporter + its scheduled service. Must be called after
    /// <c>AddMarketDataPipeline</c> (depends on <see cref="Core.MarketData.IMarketDataStore"/> and
    /// <see cref="Core.MarketData.IInstrumentRegistry"/>). Independent of the Telegram offloader;
    /// the export service idles cheaply when <see cref="ParquetLakeOptions.Enabled"/> is false.
    /// </summary>
    public static IServiceCollection AddParquetLake(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ParquetLakeOptions>(configuration.GetSection(ParquetLakeOptions.SectionName));

        services.AddSingleton<LocalParquetLakeExporter>();
        services.AddSingleton<ParquetLakeExportService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ParquetLakeExportService>());

        return services;
    }
}
