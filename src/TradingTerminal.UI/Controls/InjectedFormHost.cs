using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Attached property that drops a view-model's view into the target <see cref="ContentControl"/>,
/// where the VM→view mapping is supplied at runtime via <see cref="ViewFactory"/>. It lets a single
/// <c>DataTemplate</c> host heterogeneous content (e.g. one row template that shows any broker's
/// credential form) without a <c>DataTemplateSelector</c>.
///
/// <para>This lives in the shared UI assembly on purpose: it is named in markup, so it must be
/// resolvable in WPF's MarkupCompilePass1. A type referenced from another (already-built) assembly is
/// resolved cleanly there, whereas a same-project type forces pass1 to compile the host project's
/// code-behind — which, for the login window, references same-project XAML-generated UserControls that
/// pass1 cannot see. Keeping the host here and injecting the factory from the consumer's code-behind
/// (built in the main pass) sidesteps that entirely.</para>
/// </summary>
public static class InjectedFormHost
{
    /// <summary>Builds the view for a given view-model. Assigned once at runtime by the consumer;
    /// returns null for an unrecognised VM. A process-wide hook — there is one login window at a time.</summary>
    public static Func<object, UIElement?>? ViewFactory { get; set; }

    public static readonly DependencyProperty FormProperty = DependencyProperty.RegisterAttached(
        "Form", typeof(object), typeof(InjectedFormHost), new PropertyMetadata(null, OnFormChanged));

    public static void SetForm(DependencyObject element, object value) => element.SetValue(FormProperty, value);
    public static object? GetForm(DependencyObject element) => element.GetValue(FormProperty);

    private static void OnFormChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl host) return;
        host.Content = e.NewValue is null ? null : ViewFactory?.Invoke(e.NewValue);
    }
}
