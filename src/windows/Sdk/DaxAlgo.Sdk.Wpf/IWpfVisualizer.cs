using System.Windows;

namespace DaxAlgo.Sdk.Wpf;

/// <summary>
/// WPF presentation half of a sandboxed visualizer. It exposes only the visualizer's own content;
/// host services and controls are not available through this contract.
/// </summary>
public interface IWpfVisualizer : DaxAlgo.Sdk.IVisualizer
{
    /// <summary>The visualizer-owned content the host may mount.</summary>
    FrameworkElement Content { get; }
}
