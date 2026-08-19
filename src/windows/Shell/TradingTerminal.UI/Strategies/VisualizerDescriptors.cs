using System.Reflection;
using System.Text;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Turns a compiled type into a catalog card.
///
/// <para>Metadata is discovered by shape, the same way <c>AuthoredStrategyTypes</c> and
/// <c>AuthoredPluginBootstrap</c> discover a strategy's parts: optional <c>public static</c>
/// properties supply a name, id and description, and anything absent is derived from the type. An
/// author who writes nothing but the interface still gets a usable card — which matters because
/// Hyperion emits these, and a required attribute is one more thing for a model to get wrong.</para>
/// </summary>
public static class VisualizerDescriptors
{
    /// <summary>Reads <c>public static string Id</c>, if the author declared one.</summary>
    public const string IdProperty = "Id";

    /// <summary>Reads <c>public static string DisplayName</c>, if the author declared one.</summary>
    public const string DisplayNameProperty = "DisplayName";

    /// <summary>Reads <c>public static string Description</c>, if the author declared one.</summary>
    public const string DescriptionProperty = "Description";

    /// <summary>
    /// Builds a registration from a compiled visualizer type.
    /// </summary>
    /// <param name="type">A concrete class implementing <see cref="IVisualizer"/> with a public parameterless constructor.</param>
    /// <param name="id">Overrides the discovered id — used when the installer already assigned one.</param>
    /// <exception cref="ArgumentException">The type cannot be hosted as a visualizer.</exception>
    public static VisualizerRegistration FromType(Type type, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!CanHost(type))
        {
            throw new ArgumentException(
                $"'{type.FullName}' is not a hostable visualizer: it must be a concrete class implementing " +
                $"{nameof(IVisualizer)} with a public parameterless constructor.",
                nameof(type));
        }

        var factory = () => (IVisualizer)Activator.CreateInstance(type)!;

        return new VisualizerRegistration(
            new VisualizerDescriptor(
                id ?? ReadStatic(type, IdProperty) ?? type.FullName ?? type.Name,
                ReadStatic(type, DisplayNameProperty) ?? Humanise(type.Name),
                ReadStatic(type, DescriptionProperty) ?? string.Empty,
                DataRequirementTags: ReadRequirementTags(factory)),
            factory);
    }

    /// <summary>
    /// Every type in an assembly that can be hosted as a visualizer.
    ///
    /// <para>An assembly whose types cannot all be loaded still yields the ones that can:
    /// <see cref="ReflectionTypeLoadException"/> is normal for a plugin built against a different
    /// version of something, and losing the whole pack over one bad type is the wrong trade.</para>
    /// </summary>
    public static IReadOnlyList<VisualizerRegistration> DiscoverIn(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var found = new List<VisualizerRegistration>();
        foreach (var type in types)
        {
            if (type is null || !CanHost(type))
                continue;

            try
            {
                found.Add(FromType(type));
            }
            catch (Exception)
            {
                // A type that throws while being described is one the user cannot open anyway. Skipping
                // it keeps the rest of the pack installable.
            }
        }

        return found;
    }

    /// <summary>Whether the host can construct and run this type.</summary>
    public static bool CanHost(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
               typeof(IVisualizer).IsAssignableFrom(type) &&
               type.GetConstructor(Type.EmptyTypes) is not null;
    }

    /// <summary>
    /// The streams the card advertises, read from a constructed instance.
    ///
    /// <para>Construction is the cost of asking: <c>DataRequirement</c> is an instance member of
    /// <see cref="IVisualizer"/>, so there is no static to read it from. A constructor that throws
    /// yields no tags rather than taking down the catalog — an author is free to write a bad one, and
    /// the failure belongs at the moment the user opens it, with a message, not while the list is
    /// being drawn.</para>
    /// </summary>
    private static IReadOnlyList<string>? ReadRequirementTags(Func<IVisualizer> factory)
    {
        StrategyDataRequirement requirement;
        try
        {
            requirement = factory().DataRequirement;
        }
        catch (Exception)
        {
            return null;
        }

        var tags = new List<string>(4);
        if ((requirement & StrategyDataRequirement.L1) != 0) tags.Add("L1");
        if ((requirement & StrategyDataRequirement.Bars) != 0) tags.Add("BARS");
        if ((requirement & StrategyDataRequirement.Depth) != 0) tags.Add("DEPTH");
        if ((requirement & StrategyDataRequirement.TradeTape) != 0) tags.Add("TAPE");
        return tags.Count > 0 ? tags : null;
    }

    private static string? ReadStatic(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        if (property is null || property.PropertyType != typeof(string))
            return null;

        try
        {
            return property.GetValue(null) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>OrderBookVisualizer</c> → <c>Order Book</c>. A fallback name, not a naming scheme: an author
    /// who cares declares <c>DisplayName</c>. This exists so a card is never blank.
    /// </summary>
    internal static string Humanise(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return string.Empty;

        const string suffix = "Visualizer";
        var name = typeName.Length > suffix.Length && typeName.EndsWith(suffix, StringComparison.Ordinal)
            ? typeName[..^suffix.Length]
            : typeName;

        var text = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            // A boundary is a capital that follows a lower-case letter, or the last capital in a run
            // (so "OrderBookL2View" reads "Order Book L2 View" rather than "Order Book L 2 View").
            if (index > 0 && char.IsUpper(character) &&
                (!char.IsUpper(name[index - 1]) ||
                 (index + 1 < name.Length && char.IsLower(name[index + 1]))))
            {
                text.Append(' ');
            }

            text.Append(character);
        }

        return text.ToString();
    }
}
