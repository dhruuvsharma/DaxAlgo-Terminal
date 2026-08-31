using System.IO;
using System.Reflection;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The codegen system prompt, embedded into this assembly at build so it ships with both the app and
/// the CLI. Read as a resource, never re-parsed.
///
/// <para>It has <b>two halves, and the split is the point</b>:</para>
/// <list type="bullet">
/// <item><b>The generated surface</b> (<c>sdk/ai-context/generated/sdk-surface.md</c>) — every public
/// SDK signature and its documentation, reflected from the assembly by <c>SdkSurfaceGenerator</c>.
/// Facts. Cannot drift, because nobody writes it.</item>
/// <item><b>The hand-written pack</b> (<c>sdk/ai-context/daxalgo-strategy-context.md</c>) — conventions,
/// judgement and worked reasoning. Things a signature cannot express.</item>
/// </list>
///
/// <para>The split exists because the single hand-maintained file that preceded it drifted badly. Its
/// generator went missing, the prose was left to be maintained by hand, and it ended up teaching
/// <c>IBacktestStrategy</c> nine times, <c>IStrategyKernel</c> once, and saying nothing whatsoever
/// about <c>IRenderSurface</c> or <c>Draw</c> — so the only contract that puts pixels on screen was
/// invisible to the thing meant to generate against it. Its own doc comment had warned about exactly
/// this: <i>"keep it true — a wrong statement here reaches a model and lands in generated
/// strategies."</i> A promise nobody can keep by hand is a promise to derive it instead.</para>
/// </summary>
public sealed class StrategyContextPack
{
    internal const string ResourceName = "DaxAlgo.AiContext.StrategyPack.md";
    internal const string SurfaceResourceName = "DaxAlgo.AiContext.SdkSurface.md";

    /// <summary>The pack text — the codegen system prompt.</summary>
    public string SystemPrompt { get; }

    /// <summary>The generated SDK surface on its own, for callers that compose their own prompt.
    /// Marker-free: what a model is actually sent.</summary>
    public string SdkSurface { get; }

    /// <summary>
    /// The surface as generated, boundary markers and all — the input <see cref="SdkSurfaceSelector"/>
    /// parses.
    ///
    /// <para>Separate from <see cref="SdkSurface"/> so that every existing caller is safe by default.
    /// The markers are for the selector to read; a caller that composes its own prompt from the pack —
    /// the CLI workspace, the artifact tool — would otherwise ship them to a model, and a caller who
    /// has to remember not to is a caller who will forget.</para>
    /// </summary>
    public string SdkSurfaceSource { get; }

    /// <summary>The hand-written conventions on their own.</summary>
    public string Conventions { get; }

    private StrategyContextPack(string conventions, string surface)
    {
        Conventions = conventions;
        SdkSurfaceSource = surface;
        SdkSurface = SdkSurfaceSelector.For(surface, brief: null);
        SystemPrompt = Join(SdkSurface, conventions);
    }

    /// <summary>
    /// The two halves in the order they are sent.
    ///
    /// <para>Surface first: it is the larger half, and a stable prefix is what the providers' prompt
    /// caches key on. Conventions change more often, so they go last.</para>
    ///
    /// <para>Public because <see cref="StrategyBuildSession"/> re-joins the halves after cutting the
    /// surface to a brief, and it must produce a byte-identical layout to this one — a filter that
    /// also changed the separator would move the prefix for a second, unrelated reason.</para>
    /// </summary>
    public static string Join(string surface, string conventions) =>
        surface + "\n\n---\n\n" + conventions;

    /// <summary>Loads the embedded pack. Throws if a resource is missing (a build wiring error, not a
    /// runtime condition) so it surfaces in tests rather than as an empty prompt in production.</summary>
    public static StrategyContextPack Load()
    {
        var assembly = typeof(StrategyContextPack).Assembly;
        return new StrategyContextPack(
            Read(assembly, ResourceName, "the EmbeddedResource in DaxAlgo.Codegen.csproj"),
            Read(assembly, SurfaceResourceName,
                "sdk/ai-context/generated/sdk-surface.md — run SdkSurfaceFreshnessTests to generate it"));
    }

    private static string Read(Assembly assembly, string resource, string hint)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"AI context resource '{resource}' is not embedded — check {hint}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
