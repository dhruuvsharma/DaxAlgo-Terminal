using System.Reflection;
using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// The compiled image and the types reflected out of it. A finished authored strategy is a plugin: an
/// <c>IBacktestStrategy</c> kernel (required), plus — when the author wrote them — an
/// <c>ITradingStrategy</c> descriptor and a live view-model, which together are what let it appear in
/// the strategy catalog rather than only in the backtester. A view is optional: without one the host
/// composes the default window from the descriptor's data requirement
/// (<see cref="IAuthoredStrategyViewComposer"/>).
/// </summary>
/// <param name="Image">The emitted assembly bytes — what gets persisted to the plugins folder, and what
/// the policy scanner already read. Loading is from this byte[], never from the file, so the DLL on disk
/// is never locked and a regenerate can overwrite it.</param>
/// <param name="Assembly">The loaded assembly (default load context).</param>
/// <param name="KernelType">The single <c>IBacktestStrategy</c> implementation.</param>
/// <param name="DescriptorType">Optional <c>ITradingStrategy</c> — the catalog card's metadata.</param>
/// <param name="ViewModelType">Optional live view-model (derives <c>LiveSignalStrategyViewModelBase</c>).</param>
/// <param name="ViewType">Optional live view (a WPF <c>UserControl</c> / <c>Window</c>, built in code —
/// Roslyn cannot compile XAML).</param>
public sealed record AuthoredStrategyAssembly(
    byte[] Image,
    Assembly Assembly,
    Type KernelType,
    Type? DescriptorType = null,
    Type? ViewModelType = null,
    Type? ViewType = null)
{
    /// <summary>True when the author supplied a complete hand-written window: the descriptor, a
    /// view-model to run it live, and a view to show.</summary>
    public bool HasLiveWindow => DescriptorType is not null && ViewModelType is not null && ViewType is not null;

    /// <summary>True when the host can put this in the catalog at all: descriptor + view-model. The view
    /// is optional — when the author wrote none, an <see cref="IAuthoredStrategyViewComposer"/> builds
    /// the default window from the descriptor's <see cref="ITradingStrategy.DataRequirement"/>.</summary>
    public bool CanComposeLiveWindow => DescriptorType is not null && ViewModelType is not null;

    /// <summary>What is missing before this could be a catalog entry — for telling the user (and the
    /// model) exactly what to add. A view is deliberately not in this list: with a composer registered
    /// the host supplies the window itself.</summary>
    public IReadOnlyList<string> MissingForCatalog =>
    [
        .. DescriptorType is null ? new[] { "an ITradingStrategy descriptor" } : [],
        .. ViewModelType is null ? new[] { "a live view-model (LiveSignalStrategyViewModelBase)" } : [],
    ];
}

/// <summary>
/// Outcome of compiling a <see cref="StrategyScript"/>. On success, <see cref="Option"/>
/// is a ready-to-run <see cref="BacktestStrategyOption"/> — identical in shape to the
/// in-tree catalog entries, so it can be registered and run/backtested with no special
/// casing. On failure, <see cref="Option"/> is null and <see cref="Diagnostics"/> carries
/// the errors. Warnings may be present even on success.
/// </summary>
public sealed record StrategyCompileResult(
    bool Success,
    BacktestStrategyOption? Option,
    IReadOnlyList<StrategyDiagnostic> Diagnostics,
    AuthoredStrategyAssembly? Authored = null)
{
    public IEnumerable<StrategyDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error);

    public static StrategyCompileResult Failed(IReadOnlyList<StrategyDiagnostic> diagnostics) =>
        new(false, null, diagnostics);

    public static StrategyCompileResult Succeeded(
        BacktestStrategyOption option, IReadOnlyList<StrategyDiagnostic> diagnostics,
        AuthoredStrategyAssembly? authored = null) =>
        new(true, option, diagnostics, authored);
}
