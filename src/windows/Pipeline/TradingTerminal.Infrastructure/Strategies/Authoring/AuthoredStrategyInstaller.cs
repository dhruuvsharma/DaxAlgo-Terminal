using System.IO;
using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What an install did, in the terms the user cares about: is it backtestable, is it in the
/// catalog, is it on disk.</summary>
/// <param name="Registered">The strategy is runnable in the backtester now.</param>
/// <param name="InCatalog">It also has a card in the Strategies pane, openable now — that needs the
/// author to have written a descriptor, a live view-model and a view.</param>
/// <param name="Persisted">Absolute path of the plugin folder it was written to, or null when the plugin
/// folder could not be written (it still ran this session).</param>
/// <param name="Message">One line for the status bar.</param>
public sealed record AuthoredStrategyInstall(
    bool Registered,
    bool InCatalog,
    string? Persisted,
    string Message);

/// <summary>
/// Turns a compiled authored strategy into a first-class one: registered in the backtest registry, added
/// to the live strategy catalog, written to the plugins folder as a real plugin, and shown in the Plugin
/// Manager. This is what "Compile &amp; Register" means — before it, an authored strategy only ever
/// reached the backtester.
/// <para>
/// <b>The gate is the compile.</b> The image has already been through the same
/// <see cref="PluginPolicyScanner"/> the plugin loader applies (P/Invoke, Process, the registry,
/// Reflection.Emit and assembly loading never make it this far), and the user pressed the button. This
/// class adds no trust of its own — it only wires an already-vetted image in.
/// </para>
/// <para>
/// It loads from the in-memory image, never from the file it writes, so the DLL on disk stays unlocked
/// and regenerating a strategy simply overwrites it. The persisted copy is what makes the strategy
/// survive a restart: on the next start the ordinary <see cref="PluginLoader"/> picks it up like any
/// other plugin, with no special casing.
/// </para>
/// </summary>
public sealed class AuthoredStrategyInstaller(
    IServiceProvider services,
    IBacktestStrategyRegistry registry,
    IStrategyFactory catalog,
    PluginHostContext? plugins = null,
    ILogger<AuthoredStrategyInstaller>? logger = null)
{
    public AuthoredStrategyInstall Install(StrategyScript script, StrategyCompileResult compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        if (!compiled.Success || compiled.Option is null)
            return new AuthoredStrategyInstall(false, false, null, "The strategy did not compile — nothing was registered.");

        // 1. Backtestable immediately (this much already worked).
        registry.Register(compiled.Option);

        // 2. Catalog card — only when the author wrote the whole live window. A descriptor with no view
        //    would put a card on the pane that throws when clicked, which is worse than no card.
        var inCatalog = false;
        if (compiled.Authored is { HasLiveWindow: true } authored)
        {
            try
            {
                inCatalog = RegisterInCatalog(authored);
            }
            catch (Exception ex)
            {
                // A bad view/VM must not lose the strategy — it stays backtestable and we say why.
                logger?.LogWarning(ex, "Authored strategy {Id} could not be added to the catalog", script.Id);
                return new AuthoredStrategyInstall(
                    true, false, Persist(script, compiled),
                    $"Registered for backtesting, but the live window failed to build: {ex.Message}");
            }
        }

        // 3. Persist as a real plugin so it survives a restart (and shows in the Plugin Manager).
        var path = Persist(script, compiled);

        var missing = compiled.Authored?.MissingForCatalog ?? [];
        var message = inCatalog
            ? $"'{compiled.Option.DisplayName}' is in the Strategies catalog and the backtester — DEV (unsigned)."
            : missing.Count == 0
                ? $"'{compiled.Option.DisplayName}' is registered for backtesting — DEV (unsigned)."
                : $"'{compiled.Option.DisplayName}' is registered for backtesting. For a catalog card it also needs " +
                  $"{string.Join(", ", missing)} — ask the builder to add them.";

        if (path is null)
            message += " (Could not write it to the plugins folder, so it will be gone after a restart.)";

        logger?.LogInformation(
            "Authored strategy {Id} installed: catalog={InCatalog} persisted={Path}", script.Id, inCatalog, path);

        return new AuthoredStrategyInstall(true, inCatalog, path, message);
    }

    /// <summary>Instantiates the descriptor and wires factories for the view-model + view, resolving
    /// their constructor dependencies (LiveStrategyHostServices, IClock, …) out of the running container
    /// — the same services an in-tree strategy plugin gets.</summary>
    private bool RegisterInCatalog(AuthoredStrategyAssembly authored)
    {
        if (Activator.CreateInstance(authored.DescriptorType!) is not ITradingStrategy descriptor)
            return false;

        var vmType = authored.ViewModelType!;
        var viewType = authored.ViewType!;

        catalog.Register(descriptor, new StrategyFactoryRegistration(
            StrategyId: descriptor.Id,
            ViewFactory: sp => ActivatorUtilities.CreateInstance(sp, viewType),
            ViewModelFactory: sp => ActivatorUtilities.CreateInstance(sp, vmType)));

        // Build the pair once, now, so a broken constructor surfaces at Compile & Register — not later,
        // when the user clicks the card and gets an exception dialog.
        _ = ActivatorUtilities.CreateInstance(services, vmType);
        return true;
    }

    /// <summary>Writes the image + a manifest into <c>&lt;pluginsRoot&gt;/&lt;id&gt;/</c> so the next
    /// start loads it through the normal plugin path. Returns null (and logs) if the folder can't be
    /// written — a read-only profile shouldn't lose the strategy the user just made.</summary>
    private string? Persist(StrategyScript script, StrategyCompileResult compiled)
    {
        if (plugins is null || compiled.Authored is null) return null;

        var folderName = Sanitize(script.Id);
        var directory = Path.Combine(plugins.PluginsRoot, folderName);

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"{folderName}.dll"), compiled.Authored.Image);

            var manifest = new PluginManifest(
                Id: script.Id,
                Name: script.DisplayName,
                Version: "0.0.0-authored",
                TargetSdkVersion: SdkInfo.Version,
                Publisher: "Authored in the AI Strategy Builder");
            File.WriteAllText(
                Path.Combine(directory, PluginManifest.FileName),
                System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJson));

            // Show it in the Plugin Manager for the rest of this session; on the next start the loader
            // reports it like any other plugin.
            plugins.AddAuthored(new LoadedPlugin(
                Name: script.DisplayName,
                TargetSdkVersion: SdkInfo.Version,
                AssemblyPath: Path.Combine(directory, $"{folderName}.dll"),
                Scan: null,
                Unsigned: true,
                StrategyImplementationTypes: compiled.Authored.DescriptorType is { } d && d.FullName is { } n ? [n] : []));

            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Could not persist authored strategy {Id} to the plugins folder", script.Id);
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
