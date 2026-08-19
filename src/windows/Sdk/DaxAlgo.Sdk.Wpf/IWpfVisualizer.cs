using System.Windows;

namespace DaxAlgo.Sdk.Wpf;

/// <summary>
/// <b>Retired.</b> Draw through <see cref="DaxAlgo.Sdk.IVisualizer.Draw"/> instead.
///
/// <para>This handed the host a <see cref="FrameworkElement"/> built by the visualizer, which means
/// arbitrary WPF from an untrusted author running inside the application — the isolation the sandbox
/// exists for is gone the moment the host mounts it. It also forked rendering in two: a visualizer
/// that returns a control cannot be drawn by the same renderer as a sealed one, so every chart would
/// have had to be written twice.</para>
///
/// <para>The replacement is a data contract. A visualizer describes its frame through
/// <c>IRenderSurface</c> — panels, axes, series and primitives — and the host turns that into pixels,
/// free to bound, batch or refuse it. One renderer serves every visualizer, and an author learns one
/// API.</para>
///
/// <para>Nothing ever implemented or consumed this interface. It is marked rather than deleted
/// because <c>DaxAlgo.Sdk.Wpf</c> is published; it should be removed with the package at the next
/// major version.</para>
/// </summary>
[Obsolete(
    "Visualizers no longer supply WPF content: a FrameworkElement from an untrusted author defeats " +
    "the sandbox and cannot share the host renderer. Implement IVisualizer.Draw(IRenderSurface) instead.",
    error: false)]
public interface IWpfVisualizer : DaxAlgo.Sdk.IVisualizer
{
    /// <summary>The visualizer-owned content the host may mount.</summary>
    FrameworkElement Content { get; }
}
