using System.Windows;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// AvalonEdit wrapped for MVVM: <see cref="TextEditor"/> exposes <c>Text</c> as a plain CLR property,
/// so this adds the bindable <see cref="Code"/> DP the Vibe Quant workbench binds to
/// (<c>SelectedFile.Content</c>, two-way). Line numbers on, C# highlighting from the embedded
/// theme-neutral definition (falls back to AvalonEdit's stock C# if the resource fails to load —
/// worst case is ugly colors, never a crash).
/// </summary>
public sealed class CodeEditor : TextEditor
{
    /// <summary>Loaded once per process; null when the embedded resource failed.</summary>
    private static readonly IHighlightingDefinition? SharedHighlighting = LoadHighlighting();

    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault |
            FrameworkPropertyMetadataOptions.Journal,
            OnCodePropertyChanged)
        {
            DefaultUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });

    /// <summary>Guards the two-way Text↔Code sync so neither side re-triggers the other.</summary>
    private bool _syncing;

    public CodeEditor()
    {
        ShowLineNumbers = true;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        SyntaxHighlighting = SharedHighlighting ?? HighlightingManager.Instance.GetDefinition("C#");
        TextChanged += (_, _) => PushTextToCode();
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    private static void OnCodePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (CodeEditor)d;
        if (editor._syncing) return;

        editor._syncing = true;
        try
        {
            // A different file landed (or the model rewrote this one): replace wholesale. The caret
            // reset this causes is correct for a file switch and harmless for a model rewrite.
            editor.Text = e.NewValue as string ?? string.Empty;
        }
        finally
        {
            editor._syncing = false;
        }
    }

    private void PushTextToCode()
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            SetCurrentValue(CodeProperty, Text);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static IHighlightingDefinition? LoadHighlighting()
    {
        try
        {
            using var stream = typeof(CodeEditor).Assembly
                .GetManifestResourceStream("TradingTerminal.UI.Controls.CSharpVibeQuant.xshd");
            if (stream is null) return null;

            using var reader = XmlReader.Create(stream);
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
        {
            return null;
        }
    }
}
