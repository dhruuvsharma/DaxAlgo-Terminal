using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// The <see cref="IServiceCollection"/> a plugin actually sees inside
/// <see cref="DaxAlgo.Sdk.IStrategyPlugin.Register"/>. MS.DI resolves the LAST registration of a
/// service type, so handing a plugin the raw host collection lets it replace
/// <c>ICredentialStore</c> / <c>IBrokerSelector</c> / <c>IMarketDataStore</c> / <c>IAiAnalystClient</c>
/// and quietly intercept the user's broker session, credentials, or market data. This wrapper makes
/// the seam <b>add-only</b>:
/// <list type="bullet">
/// <item>a plugin may register NEW service types (its strategy, view-models, windows) and additional
/// implementations of the multi-registration allowlist (<see cref="ITradingStrategy"/>,
/// <see cref="BacktestStrategyOption"/>, <see cref="StrategyFactoryRegistration"/>);</item>
/// <item>it may NOT register, replace, or remove a service type the host — or an earlier plugin —
/// already provides. That throws <see cref="PluginPolicyViolationException"/>;</item>
/// <item>registrations are STAGED and only copied into the host collection by <see cref="Commit"/>,
/// which the loader calls after <c>Register</c> returns cleanly. A plugin that violates the policy
/// halfway through <c>Register</c> therefore contributes nothing at all, rather than leaving whatever
/// it managed to register before the throw.</item>
/// </list>
/// The host's own descriptors stay visible through the read view (indexer / <see cref="Count"/> /
/// enumeration) so <c>TryAdd*()</c> keeps its no-op semantics: a plugin that <c>TryAdd</c>s a service
/// the host already registered gets the intended silent no-op, not a violation.
/// <para>
/// <b>Honest limits.</b> An in-process .NET plugin runs with full process privileges — it can reflect
/// straight past DI, P/Invoke, or spawn a process. This guard is a tripwire against the cheap attack
/// (and an attribution log of what each plugin contributed), <b>not</b> a sandbox. Curation and code
/// signing remain the actual control; see <c>docs/plugin-security.md</c>.
/// </para>
/// </summary>
public sealed class GuardedServiceCollection : IServiceCollection
{
    /// <summary>Service types a plugin may legitimately contribute an ADDITIONAL registration of —
    /// the seams the host resolves as <c>IEnumerable&lt;T&gt;</c>. Everything else the host already
    /// registered is off limits.</summary>
    public static readonly IReadOnlyList<Type> MultiRegistrationAllowlist =
    [
        typeof(ITradingStrategy),
        typeof(BacktestStrategyOption),
        typeof(StrategyFactoryRegistration),
    ];

    private readonly IServiceCollection _host;
    private readonly List<ServiceDescriptor> _staged = [];
    private readonly HashSet<Type> _hostServiceTypes;
    private readonly HashSet<Type> _allowlist;
    private readonly string _plugin;

    /// <param name="host">The host collection. Snapshotted for the "already registered" check and
    /// exposed read-only until <see cref="Commit"/>.</param>
    /// <param name="plugin">Plugin name, used in violation messages and attribution.</param>
    /// <param name="allowlist">Overrides <see cref="MultiRegistrationAllowlist"/> (tests).</param>
    public GuardedServiceCollection(IServiceCollection host, string plugin, IEnumerable<Type>? allowlist = null)
    {
        _host = host;
        _plugin = plugin;
        _hostServiceTypes = [.. host.Select(d => d.ServiceType)];
        _allowlist = [.. allowlist ?? MultiRegistrationAllowlist];
    }

    /// <summary>What this plugin registered, in order — nothing is in the host collection until
    /// <see cref="Commit"/>.</summary>
    public IReadOnlyList<ServiceDescriptor> Staged => _staged;

    /// <summary>Copies the staged registrations into the host collection and returns the distinct
    /// service-type names for the attribution log. Called by <see cref="PluginLoader"/> only after
    /// <c>Register</c> returned without a violation.</summary>
    public IReadOnlyList<string> Commit()
    {
        foreach (var descriptor in _staged) _host.Add(descriptor);
        return [.. _staged.Select(d => d.ServiceType.FullName ?? d.ServiceType.Name).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The gate. A descriptor is allowed when its service type is on the multi-registration
    /// allowlist, or is a type the host hasn't registered. A plugin registering its OWN type more than
    /// once is fine — it can only shadow itself. Types contributed by an EARLIER plugin are already in
    /// the host collection by the time this plugin's guard snapshots it, so plugins can't shadow each
    /// other either.</summary>
    private ServiceDescriptor Validate(ServiceDescriptor descriptor)
    {
        var type = descriptor.ServiceType;
        if (_allowlist.Contains(type) || !_hostServiceTypes.Contains(type)) return descriptor;

        // A plugin re-registering a host service would WIN (last-registration-wins) and could hand the
        // app a hostile ICredentialStore / IBrokerSelector. Name the type — this reason is what the
        // user reads in the Plugin Manager.
        throw new PluginPolicyViolationException(_plugin, type,
            $"tried to register '{type.FullName}', which the host already provides. Plugins may add new " +
            $"services and additional {nameof(ITradingStrategy)} / {nameof(BacktestStrategyOption)} / " +
            $"{nameof(StrategyFactoryRegistration)} entries, but may not replace host services");
    }

    private PluginPolicyViolationException HostMutation(string operation, Type type) =>
        new(_plugin, type, $"tried to {operation} the host registration of '{type.FullName}'. " +
            "The plugin registration seam is add-only");

    // ── IServiceCollection: read view = host descriptors, then this plugin's staged ones ──────────

    public int Count => _host.Count + _staged.Count;

    public bool IsReadOnly => false;

    public ServiceDescriptor this[int index]
    {
        get => index < _host.Count ? _host[index] : _staged[index - _host.Count];
        set
        {
            if (index < _host.Count) throw HostMutation("replace", _host[index].ServiceType);
            _staged[index - _host.Count] = Validate(value);
        }
    }

    public void Add(ServiceDescriptor item) => _staged.Add(Validate(item));

    public void Insert(int index, ServiceDescriptor item)
    {
        // Inserting ahead of the host's descriptors would shadow them on IEnumerable<T> resolution.
        if (index < _host.Count) throw HostMutation("insert ahead of", item.ServiceType);
        _staged.Insert(index - _host.Count, Validate(item));
    }

    public void Clear() => throw HostMutation("clear", typeof(IServiceCollection));

    public bool Remove(ServiceDescriptor item)
    {
        if (_staged.Remove(item)) return true;
        if (_host.Contains(item)) throw HostMutation("remove", item.ServiceType);
        return false;
    }

    public void RemoveAt(int index)
    {
        if (index < _host.Count) throw HostMutation("remove", _host[index].ServiceType);
        _staged.RemoveAt(index - _host.Count);
    }

    public bool Contains(ServiceDescriptor item) => _host.Contains(item) || _staged.Contains(item);

    public int IndexOf(ServiceDescriptor item)
    {
        var hostIndex = _host.IndexOf(item);
        if (hostIndex >= 0) return hostIndex;
        var stagedIndex = _staged.IndexOf(item);
        return stagedIndex < 0 ? -1 : _host.Count + stagedIndex;
    }

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
    {
        _host.CopyTo(array, arrayIndex);
        _staged.CopyTo(array, arrayIndex + _host.Count);
    }

    public IEnumerator<ServiceDescriptor> GetEnumerator() => _host.Concat(_staged).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Thrown when a plugin's <c>Register</c> tries to replace or remove a service the host
/// already provides (see <see cref="GuardedServiceCollection"/>). The plugin's staged registrations
/// are discarded and the loader quarantines it.</summary>
public sealed class PluginPolicyViolationException(string pluginName, Type serviceType, string reason)
    : Exception($"Plugin '{pluginName}' {reason}.")
{
    public string PluginName { get; } = pluginName;
    public Type ServiceType { get; } = serviceType;
    public string Reason { get; } = reason;
}
