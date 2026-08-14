using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Plugins;
using EngineParameterKind = TradingTerminal.Core.Backtesting.ParameterKind;
using EngineParameters = TradingTerminal.Core.Backtesting.StrategyParameters;
using EngineSchema = TradingTerminal.Core.Backtesting.StrategyParameterSchema;
using RichParameter = TradingTerminal.Core.Strategies.Parameters.StrategyParameter;
using RichParameterKind = TradingTerminal.Core.Strategies.Parameters.ParameterKind;
using RichParameters = TradingTerminal.Core.Strategies.Parameters.StrategyParameters;
using RichSchema = TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema;

namespace TradingTerminal.BacktestStudio;

/// <summary>Where a Studio strategy artifact came from.</summary>
public enum StrategyProvenance
{
    BuiltIn,
    Authored,
    SealedDaxq,
}

/// <summary>Which existing backtest path executes a catalog entry.</summary>
public enum StrategyExecutionRoute
{
    OrderNative,
    Signal,
}

/// <summary>
/// One strategy as seen by every Studio surface. The richer authored parameter schema is canonical;
/// built-in numeric schemas are projected into it and converted back only when a RunSpec is built.
/// </summary>
public sealed class StrategyCatalogDescriptor
{
    private readonly Func<RichParameters, IStrategyKernel> _createKernel;
    private readonly Func<RichParameters, EngineParameters> _createRunParameters;

    internal StrategyCatalogDescriptor(
        string id,
        string displayName,
        string description,
        StrategyProvenance provenance,
        StrategyExecutionRoute executionRoute,
        bool supportsOptimization,
        RichSchema schema,
        StrategyDataRequirement dataRequirement,
        Func<RichParameters, IStrategyKernel> createKernel,
        Func<RichParameters, EngineParameters> createRunParameters,
        string? researchPaperUrl = null)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Provenance = provenance;
        ExecutionRoute = executionRoute;
        SupportsOptimization = supportsOptimization;
        Schema = schema;
        DataRequirement = dataRequirement;
        ResearchPaperUrl = researchPaperUrl;
        _createKernel = createKernel;
        _createRunParameters = createRunParameters;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Name => DisplayName;
    public string Description { get; }
    public StrategyProvenance Provenance { get; }
    public StrategyExecutionRoute ExecutionRoute { get; }
    public bool SupportsOptimization { get; }
    public RichSchema Schema { get; }
    public StrategyDataRequirement DataRequirement { get; }
    public string? ResearchPaperUrl { get; }
    public string CatalogKey => $"{Provenance}:{Id}";
    public string ProvenanceTag => Provenance switch
    {
        StrategyProvenance.BuiltIn => "built-in",
        StrategyProvenance.Authored => "authored",
        StrategyProvenance.SealedDaxq => "sealed-DAXQ",
        _ => Provenance.ToString(),
    };
    public string DataRequirementText => DataRequirement.ToString();

    public RichParameters ResolveParameters(IReadOnlyDictionary<string, object?>? values) =>
        new(Schema, values);

    public IStrategyKernel CreateKernel(RichParameters parameters) =>
        _createKernel(parameters ?? throw new ArgumentNullException(nameof(parameters)));

    public EngineParameters CreateRunParameters(RichParameters parameters) =>
        _createRunParameters(parameters ?? throw new ArgumentNullException(nameof(parameters)));
}

/// <summary>The only strategy enumeration seam consumed by the Backtest Studio.</summary>
public interface IStrategyCatalog
{
    IReadOnlyList<StrategyCatalogDescriptor> All { get; }
    event EventHandler? Changed;
}

/// <summary>
/// Pro-owned observation point for registrations returned by the protected engine. The public engine
/// contract deliberately has no enumeration member, so the Studio tracks successful load results here
/// and only publishes entries that subsequently appear in the runtime backtest registry.
/// </summary>
public interface IProtectedStrategyRegistrationSource
{
    IReadOnlyList<ProtectedStrategyRegistration> All { get; }
    event EventHandler? Changed;
}

public sealed class ProtectedStrategyRegistrationSource : IProtectedStrategyRegistrationSource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProtectedStrategyRegistration> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProtectedStrategyRegistration> All
    {
        get
        {
            lock (_gate)
                return _byId.Values.ToArray();
        }
    }

    internal void Capture(IReadOnlyList<ProtectedStrategyRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var changed = false;
        lock (_gate)
        {
            foreach (var registration in registrations)
            {
                ArgumentNullException.ThrowIfNull(registration);
                var id = registration.BacktestStrategy?.Id;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("A protected strategy registration has no backtest id.");

                changed |= !_byId.TryGetValue(id, out var existing) || !ReferenceEquals(existing, registration);
                _byId[id] = registration;
            }
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}

/// <summary>Projects built-in, authored, Python-authored, and sealed-DAXQ sources into one list.</summary>
public sealed class StrategyCatalog : IStrategyCatalog, IDisposable
{
    private static readonly StrategyDataRequirement BuiltInDataRequirement =
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    private readonly IStrategyKernelRegistry _builtIns;
    private readonly IBacktestStrategyRegistry _authored;
    private readonly IProtectedStrategyRegistrationSource _protected;
    private readonly IReadOnlyList<StrategyKernelDescriptor> _authoredKernels;
    private int _disposed;

    public StrategyCatalog(
        IStrategyKernelRegistry builtIns,
        IBacktestStrategyRegistry authored,
        IProtectedStrategyRegistrationSource protectedStrategies,
        IReadOnlyList<StrategyKernelDescriptor>? authoredKernels = null)
    {
        _builtIns = builtIns ?? throw new ArgumentNullException(nameof(builtIns));
        _authored = authored ?? throw new ArgumentNullException(nameof(authored));
        _protected = protectedStrategies ?? throw new ArgumentNullException(nameof(protectedStrategies));
        _authoredKernels = authoredKernels ?? [];

        _authored.Changed += OnSourceChanged;
        _protected.Changed += OnSourceChanged;
    }

    public IReadOnlyList<StrategyCatalogDescriptor> All
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var entries = new List<StrategyCatalogDescriptor>();
            var occupiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in _builtIns.All)
            {
                entries.Add(ProjectKernel(descriptor, StrategyProvenance.BuiltIn));
                occupiedIds.Add(descriptor.Id);
            }

            var sealedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var registration in _protected.All)
            {
                var id = registration.BacktestStrategy.Id;
                // LoadStrategies is also used for verification/staging. Only an entry registered into
                // the running catalog is installed and therefore eligible for the Studio dropdown.
                if (_authored.Find(id) is not { } active ||
                    !ReferenceEquals(active, registration.BacktestStrategy))
                    continue;

                sealedIds.Add(id);
                if (occupiedIds.Add(id))
                    entries.Add(ProjectProtected(registration));
            }

            foreach (var option in _authored.All)
            {
                if (occupiedIds.Contains(option.Id) || sealedIds.Contains(option.Id))
                    continue;

                entries.Add(ProjectAuthored(option));
                occupiedIds.Add(option.Id);
            }

            foreach (var descriptor in _authoredKernels)
            {
                if (!occupiedIds.Add(descriptor.Id))
                    continue;
                entries.Add(ProjectKernel(descriptor, StrategyProvenance.Authored));
            }

            return entries
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Provenance)
                .ToArray();
        }
    }

    public event EventHandler? Changed;

    private void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private static StrategyCatalogDescriptor ProjectKernel(
        StrategyKernelDescriptor descriptor,
        StrategyProvenance provenance)
    {
        var richSchema = ProjectSchema(descriptor.Schema);
        return new StrategyCatalogDescriptor(
            descriptor.Id,
            descriptor.Name,
            descriptor.Description,
            provenance,
            StrategyExecutionRoute.OrderNative,
            supportsOptimization: true,
            richSchema,
            BuiltInDataRequirement,
            _ => descriptor.Create(),
            parameters => ToEngineParameters(descriptor.Schema, parameters),
            descriptor.ResearchPaperUrl);
    }

    private static StrategyCatalogDescriptor ProjectAuthored(BacktestStrategyOption option) =>
        ProjectOption(
            option,
            option.DisplayName,
            "Compiled or installed authored strategy.",
            StrategyProvenance.Authored,
            StrategyExecutionRoute.OrderNative,
            option.ResearchPaperUrl);

    private static StrategyCatalogDescriptor ProjectProtected(ProtectedStrategyRegistration registration) =>
        ProjectOption(
            registration.BacktestStrategy,
            registration.Strategy.DisplayName,
            registration.Strategy.Description,
            StrategyProvenance.SealedDaxq,
            StrategyExecutionRoute.Signal,
            registration.Strategy.ResearchPaperUrl);

    private static StrategyCatalogDescriptor ProjectOption(
        BacktestStrategyOption option,
        string displayName,
        string description,
        StrategyProvenance provenance,
        StrategyExecutionRoute route,
        string? researchPaperUrl)
    {
        return new StrategyCatalogDescriptor(
            option.Id,
            displayName,
            description,
            provenance,
            route,
            supportsOptimization: false,
            option.Schema,
            option.DataRequirement,
            parameters =>
            {
                var snapshot = parameters.ToDictionary();
                return new BacktestStrategyKernelAdapter(contract =>
                {
                    if (option.ParameterizedBuild is null)
                        return option.CreateForBacktest(contract);

                    var runtimeParameters = new RichParameters(option.Schema, snapshot);
                    return option.Create(contract, runtimeParameters);
                });
            },
            ToEngineParameters,
            researchPaperUrl);
    }

    private static RichSchema ProjectSchema(EngineSchema schema) =>
        new(schema.Parameters.Select(ProjectParameter));

    private static RichParameter ProjectParameter(ParameterDescriptor parameter)
    {
        var kind = parameter.Kind switch
        {
            EngineParameterKind.Integer => RichParameterKind.Integer,
            EngineParameterKind.Boolean => RichParameterKind.Boolean,
            EngineParameterKind.Categorical => RichParameterKind.Choice,
            _ => RichParameterKind.Number,
        };
        object defaultValue = kind switch
        {
            RichParameterKind.Integer => (long)Math.Round(parameter.Default),
            RichParameterKind.Boolean => parameter.Default >= 0.5,
            RichParameterKind.Choice => ChoiceAt(parameter, parameter.Default),
            _ => parameter.Default,
        };

        return new RichParameter
        {
            Key = parameter.Name,
            DisplayName = parameter.Label,
            Kind = kind,
            Default = defaultValue,
            Min = double.IsFinite(parameter.Min) ? parameter.Min : null,
            Max = double.IsFinite(parameter.Max) ? parameter.Max : null,
            Step = parameter.Step > 0 ? parameter.Step : null,
            Choices = parameter.Choices,
        };
    }

    private static string ChoiceAt(ParameterDescriptor parameter, double value)
    {
        if (parameter.Choices is not { Count: > 0 } choices)
            return string.Empty;
        var index = Math.Clamp((int)Math.Round(value), 0, choices.Count - 1);
        return choices[index];
    }

    private static EngineParameters ToEngineParameters(EngineSchema schema, RichParameters parameters)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in schema.Parameters)
        {
            var value = parameters.GetRaw(parameter.Name);
            values[parameter.Name] = parameter.Kind switch
            {
                EngineParameterKind.Boolean => value is true ? 1d : 0d,
                EngineParameterKind.Categorical => parameter.Choices is { } choices
                    ? Math.Max(0, IndexOfChoice(choices, value?.ToString() ?? string.Empty))
                    : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return schema.Resolve(values);
    }

    private static EngineParameters ToEngineParameters(RichParameters parameters)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in parameters.Schema.Parameters)
        {
            var value = parameters.GetRaw(parameter.Key);
            switch (parameter.Kind)
            {
                case RichParameterKind.Integer:
                case RichParameterKind.Number:
                    values[parameter.Key] = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case RichParameterKind.Boolean:
                    values[parameter.Key] = value is true ? 1d : 0d;
                    break;
                case RichParameterKind.Choice when parameter.Choices is { } choices:
                    values[parameter.Key] = Math.Max(0, IndexOfChoice(choices, value?.ToString() ?? string.Empty));
                    break;
            }
        }
        return new EngineParameters(values);
    }

    private static int IndexOfChoice(IReadOnlyList<string> choices, string value)
    {
        for (var index = 0; index < choices.Count; index++)
        {
            if (string.Equals(choices[index], value, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _authored.Changed -= OnSourceChanged;
        _protected.Changed -= OnSourceChanged;
    }
}

internal sealed class CatalogingProtectedStrategyEngine(
    IProtectedStrategyEngine inner,
    ProtectedStrategyRegistrationSource registrations) : IProtectedStrategyEngine
{
    public IReadOnlyList<ProtectedStrategyRegistration> LoadStrategies(string daxqPath)
    {
        var loaded = inner.LoadStrategies(daxqPath)
            ?? throw new InvalidOperationException("The protected strategy engine returned no registration collection.");
        registrations.Capture(loaded);
        return loaded;
    }
}

internal static class ProtectedStrategyEngineDecoration
{
    internal static void Install(IServiceCollection services)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IProtectedStrategyEngine));
        if (existing is null)
            return;

        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProtectedStrategyEngine),
            provider => new CatalogingProtectedStrategyEngine(
                Resolve(provider, existing),
                provider.GetRequiredService<ProtectedStrategyRegistrationSource>()),
            existing.Lifetime));
    }

    private static IProtectedStrategyEngine Resolve(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProtectedStrategyEngine instance)
            return instance;
        if (descriptor.ImplementationFactory is { } factory)
            return (IProtectedStrategyEngine)factory(provider);
        if (descriptor.ImplementationType is { } implementationType)
            return (IProtectedStrategyEngine)ActivatorUtilities.GetServiceOrCreateInstance(provider, implementationType);
        throw new InvalidOperationException("The protected strategy engine registration has no implementation.");
    }
}
