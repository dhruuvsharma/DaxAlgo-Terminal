using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class QuestDbNativeBootstrapperTests
{
    [Theory]
    [InlineData(MarketDataProvider.QuestDb, QuestDbLaunchMode.Native, true, true)]
    [InlineData(MarketDataProvider.QuestDb, QuestDbLaunchMode.Native, false, false)]
    [InlineData(MarketDataProvider.QuestDb, QuestDbLaunchMode.External, true, false)]
    [InlineData(MarketDataProvider.SqlitePerBroker, QuestDbLaunchMode.Native, true, false)]
    public void Launcher_auto_start_requires_the_questdb_native_mode(
        MarketDataProvider provider,
        QuestDbLaunchMode launchMode,
        bool configuredAutoStart,
        bool expected)
    {
        var options = new MarketDataStoreOptions
        {
            Provider = provider,
            QuestDbLaunchMode = launchMode,
            AutoStartQuestDb = configuredAutoStart,
        };
        using var launcher = new QuestDbNativeService(
            Options.Create(options),
            Substitute.For<IMarketDataStore>(),
            NullLogger<QuestDbNativeService>.Instance);

        launcher.IsApplicable.Should().Be(provider == MarketDataProvider.QuestDb);
        launcher.AutoStart.Should().Be(expected);
    }

    [Fact]
    public async Task External_mode_never_starts_a_local_process_when_the_endpoint_is_unreachable()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbLaunchMode = QuestDbLaunchMode.External,
            QuestDbPgConnectionString =
                "Host=127.0.0.1;Port=1;Database=qdb;Username=admin;Password=quest;Timeout=1",
        };
        using var launcher = new QuestDbNativeService(
            Options.Create(options),
            Substitute.For<IMarketDataStore>(),
            NullLogger<QuestDbNativeService>.Instance);

        var started = await launcher.StartAsync();

        started.Should().BeFalse();
    }

    [Fact]
    public void ResolvePaths_uses_bundled_runtime_and_per_user_data_by_default()
    {
        var appBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo App"));
        var localData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo Local"));

        var paths = QuestDbNativeBootstrapper.ResolvePaths(
            new MarketDataStoreOptions(), appBase, localData);

        paths.ExecutablePath.Should().Be(Path.Combine(appBase, "questdb", "bin", "questdb.exe"));
        paths.RootPath.Should().Be(Path.Combine(localData, "DaxAlgoTerminal", "QuestDB"));
    }

    [Theory]
    [InlineData(QuestDbLaunchMode.Native, "localhost", 8812, "http", "localhost:9000", true)]
    [InlineData(QuestDbLaunchMode.Native, "127.0.0.1", 8812, "http", "127.0.0.1:9000", true)]
    [InlineData(QuestDbLaunchMode.Native, "::1", 8812, "http", "[::1]:9000", false)]
    [InlineData(QuestDbLaunchMode.Native, "localhost", 18812, "http", "localhost:9000", false)]
    [InlineData(QuestDbLaunchMode.Native, "localhost", 8812, "http", "localhost:19000", false)]
    [InlineData(QuestDbLaunchMode.Native, "localhost", 8812, "https", "localhost:9000", false)]
    [InlineData(QuestDbLaunchMode.External, "::1", 18812, "http", "[::1]:19000", true)]
    [InlineData(QuestDbLaunchMode.External, "127.0.0.1", 18812, "https", "127.0.0.1:19000", true)]
    [InlineData(QuestDbLaunchMode.External, "questdb.internal", 8812, "http", "localhost:9000", false)]
    [InlineData(QuestDbLaunchMode.External, "localhost", 8812, "http", "10.0.0.8:9000", false)]
    public void Endpoint_validation_enforces_the_launch_mode_contract(
        QuestDbLaunchMode launchMode,
        string pgHost,
        int pgPort,
        string ilpScheme,
        string ilpAddress,
        bool expected)
    {
        var options = new MarketDataStoreOptions
        {
            QuestDbLaunchMode = launchMode,
            QuestDbPgConnectionString =
                $"Host={pgHost};Port={pgPort};Database=qdb;Username=admin;Password=quest",
            QuestDbIlpConfig = $"{ilpScheme}::addr={ilpAddress};auto_flush=off;",
        };

        var valid = QuestDbNativeBootstrapper.HasSafeEndpoints(options, out var reason);

        valid.Should().Be(expected);
        if (expected) reason.Should().BeNull();
        else reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ResolvePaths_keeps_relative_runtime_and_data_paths_in_their_safe_roots()
    {
        var appBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo App"));
        var localData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo Local"));
        var options = new MarketDataStoreOptions
        {
            QuestDbExecutablePath = Path.Combine("runtime", "questdb.exe"),
            QuestDbRootPath = "custom-data",
        };

        var paths = QuestDbNativeBootstrapper.ResolvePaths(options, appBase, localData);

        paths.ExecutablePath.Should().Be(Path.Combine(appBase, "runtime", "questdb.exe"));
        paths.RootPath.Should().Be(Path.Combine(localData, "DaxAlgoTerminal", "custom-data"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolvePaths_rejects_relative_traversal(bool executablePath)
    {
        var appBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo App"));
        var localData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DaxAlgo Local"));
        var options = new MarketDataStoreOptions();
        if (executablePath)
            options.QuestDbExecutablePath = Path.Combine("..", "outside", "questdb.exe");
        else
            options.QuestDbRootPath = Path.Combine("..", "outside-data");

        var act = () => QuestDbNativeBootstrapper.ResolvePaths(options, appBase, localData);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot traverse outside*");
    }

    [Fact]
    public void EnsureManagedConfiguration_repairs_an_existing_unsafe_file()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), "daxalgo-qdb-test-" + Guid.NewGuid().ToString("N"));
        var paths = new QuestDbRuntimePaths(
            Path.Combine(temporaryRoot, "runtime", "questdb.exe"),
            Path.Combine(temporaryRoot, "data"));
        var configurationPath = Path.Combine(paths.RootPath, "conf", "server.conf");

        try
        {
            QuestDbNativeBootstrapper.EnsureManagedConfiguration(paths);
            var managed = File.ReadAllText(configurationPath);
            managed.Should().Contain("http.net.bind.to=127.0.0.1:9000");
            managed.Should().Contain("http.min.enabled=false");
            managed.Should().Contain("pg.net.bind.to=127.0.0.1:8812");
            managed.Should().Contain("line.tcp.enabled=false");
            managed.Should().Contain("telemetry.enabled=false");

            File.WriteAllText(configurationPath, "http.net.bind.to=0.0.0.0:9000\ntelemetry.enabled=true\n");
            QuestDbNativeBootstrapper.EnsureManagedConfiguration(paths);
            File.ReadAllText(configurationPath).Should().Be(
                QuestDbNativeBootstrapper.ManagedServerConfiguration);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Root_ownership_is_exclusive_and_released_with_the_handle()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), "daxalgo-qdb-lock-test-" + Guid.NewGuid().ToString("N"));
        var paths = new QuestDbRuntimePaths(
            Path.Combine(temporaryRoot, "runtime", "questdb.exe"),
            Path.Combine(temporaryRoot, "data"));

        try
        {
            QuestDbNativeBootstrapper.TryAcquireRootOwnership(paths, out var first).Should().BeTrue();
            using (first)
            {
                QuestDbNativeBootstrapper.TryAcquireRootOwnership(paths, out var second).Should().BeFalse();
                second.Should().BeNull();
            }

            QuestDbNativeBootstrapper.TryAcquireRootOwnership(paths, out var afterRelease).Should().BeTrue();
            afterRelease!.Dispose();
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Sanitized_endpoint_never_contains_credentials()
    {
        const string secret = "do-not-log-this";
        var endpoint = QuestDbNativeBootstrapper.DescribePgEndpoint(
            $"Host=127.0.0.1;Port=8812;Database=qdb;Username=admin;Password={secret}");

        endpoint.Should().Be("127.0.0.1:8812");
        endpoint.Should().NotContain(secret);
        endpoint.Should().NotContain("admin");
    }

    [Fact]
    public void Remote_questdb_configuration_is_rejected_before_store_construction()
    {
        const string secret = "remote-password-must-not-escape";
        var values = new Dictionary<string, string?>
        {
            [$"{MarketDataStoreOptions.SectionName}:Provider"] = nameof(MarketDataProvider.QuestDb),
            [$"{MarketDataStoreOptions.SectionName}:QuestDbLaunchMode"] = nameof(QuestDbLaunchMode.External),
            [$"{MarketDataStoreOptions.SectionName}:QuestDbPgConnectionString"] =
                $"Host=198.51.100.20;Port=8812;Database=qdb;Username=admin;Password={secret};Timeout=30",
            [$"{MarketDataStoreOptions.SectionName}:QuestDbIlpConfig"] =
                "http::addr=198.51.100.20:9000;auto_flush=off;",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddMarketDataPipeline(configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IMarketDataStore>();

        var assertion = act.Should().Throw<InvalidOperationException>();
        assertion.Which.Message.Should().Contain("Unsafe QuestDB configuration");
        assertion.Which.Message.Should().NotContain(secret);
    }

    [Fact]
    public void CreateStartInfo_passes_the_data_root_as_one_argument()
    {
        var paths = new QuestDbRuntimePaths(
            Path.Combine(Path.GetTempPath(), "QuestDB Runtime", "questdb.exe"),
            Path.Combine(Path.GetTempPath(), "DaxAlgo Data", "QuestDB"));

        var startInfo = QuestDbNativeBootstrapper.CreateStartInfo(paths);

        startInfo.FileName.Should().Be(paths.ExecutablePath);
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.CreateNoWindow.Should().BeTrue();
        startInfo.ArgumentList.Should().Equal("-d", paths.RootPath);
    }
}
