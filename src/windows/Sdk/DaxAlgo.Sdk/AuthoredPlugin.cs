using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sdk;

/// <summary>
/// The types a strategy assembly can contribute. A strategy authored in the AI builder is compiled from
/// loose files, so the host discovers its parts by shape rather than by a hand-written registration:
/// the kernel is required, the rest turn it from a backtest entry into a catalog card with a live window.
/// </summary>
/// <param name="Kernel">The single <see cref="IBacktestStrategy"/> with a public <c>(Contract)</c> ctor.</param>
/// <param name="Descriptor">Optional <see cref="ITradingStrategy"/> — the catalog card's metadata.</param>
/// <param name="ViewModel">Optional live view-model (derives <c>LiveSignalStrategyViewModelBase</c>).</param>
/// <param name="View">Optional live view (a WPF <c>UserControl</c> / <c>Window</c>).</param>
public sealed record AuthoredStrategyTypes(
    Type? Kernel,
    Type? Descriptor = null,
    Type? ViewModel = null,
    Type? View = null)
{
    /// <summary>Everything a catalog card needs: metadata, a view-model to run it, a view to show.</summary>
    public bool HasLiveWindow => Descriptor is not null && ViewModel is not null && View is not null;

    /// <summary>
    /// Finds them by shape. The view-model and view are matched by base-type NAME on purpose: this SDK is
    /// deliberately UI-free (it must stay loadable in a headless host), so it cannot reference the WPF
    /// base types it is looking for — but the authored assembly compiles against them perfectly well.
    /// </summary>
    public static AuthoredStrategyTypes DiscoverIn(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var types = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .ToArray();

        return new AuthoredStrategyTypes(
            Kernel: types.FirstOrDefault(t =>
                typeof(IBacktestStrategy).IsAssignableFrom(t) &&
                t.GetConstructor([typeof(Contract)]) is not null),
            Descriptor: types.FirstOrDefault(t =>
                typeof(ITradingStrategy).IsAssignableFrom(t) &&
                t.GetConstructor(Type.EmptyTypes) is not null),
            ViewModel: types.FirstOrDefault(t =>
                InheritsFrom(t, "TradingTerminal.UI.LiveSignalStrategyViewModelBase")),
            View: types.FirstOrDefault(t =>
                InheritsFrom(t, "System.Windows.Controls.UserControl") ||
                InheritsFrom(t, "System.Windows.Window")));
    }

    private static bool InheritsFrom(Type type, string baseFullName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, baseFullName, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// The <see cref="IStrategyPlugin.Register"/> body for a strategy the user authored in the app. The
/// builder emits a tiny generated <see cref="IStrategyPlugin"/> into the compiled assembly whose whole
/// job is to call this — so an authored strategy is a genuine plugin, indistinguishable to the loader
/// from one built with <c>dotnet new daxalgo-strategy</c>. Without it, the loader finds the DLL on the
/// next start, can't see an entry point, and reports it as failed.
/// <para>
/// It registers exactly what a hand-written plugin's <c>AddXxxStrategy()</c> would: the backtest option,
/// the catalog descriptor, and (when the author wrote them) the live view-model + view behind a
/// <see cref="StrategyFactoryRegistration"/>.
/// </para>
/// </summary>
public static class AuthoredPluginBootstrap
{
    public static void Register(IPluginRegistrar registrar, Assembly assembly, string strategyId, string displayName)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        var found = AuthoredStrategyTypes.DiscoverIn(assembly);
        if (found.Kernel is null) return;  // nothing runnable — register nothing rather than half a plugin

        var services = registrar.Services;
        services.AddSingleton(BuildOption(found.Kernel, strategyId, displayName));

        if (found.Descriptor is null) return;   // backtest-only: a card with no metadata is not a card

        // By TYPE, not by instance: the loader reads the staged descriptors' ImplementationType to work
        // out which catalog entries came from which plugin (and so which wear the DEV badge).
        services.AddSingleton(typeof(ITradingStrategy), found.Descriptor);

        if (!found.HasLiveWindow) return;       // no window to open ⇒ no factory registration

        var viewModel = found.ViewModel!;
        var view = found.View!;
        services.AddTransient(viewModel);
        services.AddTransient(view);
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: strategyId,
            ViewFactory: sp => ActivatorUtilities.CreateInstance(sp, view),
            ViewModelFactory: sp => ActivatorUtilities.CreateInstance(sp, viewModel)));
    }

    /// <summary>Wires the kernel's <c>(Contract)</c> constructor — and its optional declarative
    /// <c>Schema</c> / <c>Create(Contract, StrategyParameters)</c> — into a runnable option.</summary>
    private static BacktestStrategyOption BuildOption(Type kernel, string strategyId, string displayName)
    {
        var ctor = kernel.GetConstructor([typeof(Contract)])!;

        var schema = kernel.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static) is { } property &&
                     property.PropertyType == typeof(StrategyParameterSchema)
            ? property.GetValue(null) as StrategyParameterSchema
            : null;

        var create = kernel.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
            types: [typeof(Contract), typeof(StrategyParameters)], modifiers: null);

        Func<Contract, StrategyParameters, IBacktestStrategy>? parameterized =
            create is not null && typeof(IBacktestStrategy).IsAssignableFrom(create.ReturnType)
                ? (contract, parameters) => (IBacktestStrategy)create.Invoke(null, [contract, parameters])!
                : null;

        return new BacktestStrategyOption(
            strategyId, displayName,
            contract => (IBacktestStrategy)ctor.Invoke([contract]))
        {
            Schema = schema ?? StrategyParameterSchema.Empty,
            ParameterizedBuild = parameterized,
        };
    }
}
