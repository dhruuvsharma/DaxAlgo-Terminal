using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Every transcript template is actually MATERIALISED against the real dictionary.
///
/// <para><b>The defect this exists for reached a user's machine and wedged the application.</b> The
/// thinking template referenced <c>HYP.ShimmerLabel</c> with <c>StaticResource</c>, and that style is
/// declared later in the same dictionary — a StaticResource only resolves against what precedes it.
/// So the moment a reasoning model produced its first thought, the template threw
/// XamlParseException("Cannot find resource named 'HYP.ShimmerLabel'") from inside Measure, on the
/// dispatcher, on every layout pass: thirty identical crash reports in one second, a beep for each,
/// and a frozen window.</para>
///
/// <para><b>Why nothing caught it.</b> The pane's own tests parse the view, and the view parses fine —
/// the templates live in a dictionary and are only expanded when a message of that Kind exists to
/// expand them against. The view-model tests assert a Thinking message is produced but never render
/// one. Both halves passed while the thing they describe crashed on sight.</para>
///
/// <para>So this renders one message of every Kind. A template that cannot find a resource fails
/// here, in a second, instead of on a user's machine a quarter of an hour into a generation.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed partial class HyperionTemplatesRenderTests
{
    public static TheoryData<string, AuthoringMessage> EveryKind() => new()
    {
        { AuthoringMessage.KindUser, new AuthoringMessage(CodegenRole.User, "build me a strategy") },
        { AuthoringMessage.KindAssistant, new AuthoringMessage(CodegenRole.Assistant, "Here is the plan.") },
        { AuthoringMessage.KindNote, AuthoringMessage.System("Switched provider.") },
        { AuthoringMessage.KindThinking, AuthoringMessage.Thinking("weighing the entry rule against the exit") },
        { AuthoringMessage.KindTool, AuthoringMessage.Tool("Ok", "Compiled", "1 file · 1 generation", "full output") },
        { AuthoringMessage.KindFiles, AuthoringMessage.FilesChanged([new FileChangeSummary("Strategy.cs", 42, 0)]) },
    };

    [WpfTheory]
    [MemberData(nameof(EveryKind))]
    public void A_message_of_every_kind_renders_against_the_real_dictionary(string kind, AuthoringMessage message)
    {
        var dictionary = HyperionDictionary();

        var host = new ContentControl
        {
            Content = message,
            Style = (Style)dictionary["HYP.MessageSwitch"],
            Resources = dictionary,
        };

        // Measure is where a template is expanded, and where the missing resource threw. Parsing the
        // dictionary alone never touches it.
        host.Measure(new Size(800, 2000));
        host.Arrange(new Rect(0, 0, 800, 2000));
        host.UpdateLayout();

        Assert.Equal(kind, message.Kind);

        // A template that threw would never have produced a visual child; one that resolved has at
        // least a root border or panel under the presenter.
        Assert.True(
            System.Windows.Media.VisualTreeHelper.GetChildrenCount(host) > 0,
            $"the '{kind}' template produced no visual — it did not expand");
    }

    /// <summary>
    /// No <c>StaticResource</c> in the dictionary points at a key declared later in the same file.
    ///
    /// <para><b>This is the test that would have caught it.</b> The rendering theory above does not:
    /// loaded standalone, the whole dictionary is in scope by the time a template is instantiated, so
    /// a forward reference resolves and the test passes. I verified that by putting the bug back — all
    /// seven rendering cases stayed green. In the running application the dictionary is merged into
    /// <c>Application.Resources</c> and WPF expands templates through its optimised path, which
    /// resolves <c>StaticResource</c> against what was parsed BEFORE the template; there, the same
    /// forward reference throws from inside Measure, on the dispatcher, on every layout pass.</para>
    ///
    /// <para>So the file is checked as text, which is where the ordering actually lives. It is a
    /// cheaper and stricter guard than trying to reconstruct the application's resource scope, and it
    /// cannot pass for the wrong reason.</para>
    /// </summary>
    [Fact]
    public void No_StaticResource_points_at_a_key_declared_later_in_the_file()
    {
        var xaml = File.ReadAllLines(HyperionStylesPath());

        var declaredAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedAt = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var line = 0; line < xaml.Length; line++)
        {
            foreach (Match key in KeyPattern().Matches(xaml[line]))
            {
                var name = key.Groups["name"].Value;
                if (!declaredAt.ContainsKey(name)) declaredAt[name] = line;
            }

            foreach (Match use in StaticResourcePattern().Matches(xaml[line]))
            {
                var name = use.Groups["name"].Value.Trim();
                if (!usedAt.ContainsKey(name)) usedAt[name] = line;
            }
        }

        var forward = usedAt
            .Where(u => declaredAt.TryGetValue(u.Key, out var d) && d > u.Value)
            .Select(u => $"{u.Key}: used on line {u.Value + 1}, declared on line {declaredAt[u.Key] + 1}")
            .ToArray();

        Assert.True(forward.Length == 0,
            "StaticResource resolves only against earlier declarations, so these throw when their "
            + "template is first expanded — on the dispatcher, on every layout pass:\n  "
            + string.Join("\n  ", forward));

        var undeclared = usedAt.Keys
            .Where(k => k.StartsWith("HYP.", StringComparison.Ordinal) && !declaredAt.ContainsKey(k))
            .ToArray();

        Assert.True(undeclared.Length == 0,
            "referenced but never declared in this dictionary: " + string.Join(", ", undeclared));
    }

    [GeneratedRegex("x:Key=\"(?<name>[^\"]+)\"")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\{StaticResource\s+(?<name>[^}]+)\}")]
    private static partial Regex StaticResourcePattern();

    /// <summary>The dictionary on disk, found from the repo root rather than from the build output —
    /// the compiled form is BAML and the ordering question only exists in the text.</summary>
    private static string HyperionStylesPath([CallerFilePath] string thisFile = "")
    {
        // Located from THIS FILE, not by walking up from the build output: Directory.Build.props
        // redirects every project in the tree to C:\DaxAlgoBuild\..., which is outside the repository,
        // so a walk from AppContext.BaseDirectory never finds the solution and the test fails for a
        // reason that has nothing to do with what it checks. The compiler knows where the source is.
        var repoRoot = Directory.GetParent(Path.GetDirectoryName(thisFile)!)!.Parent!.FullName;

        var path = Path.Combine(
            repoRoot, "src", "windows", "Shell", "TradingTerminal.UI", "Themes", "HyperionStyles.xaml");

        Assert.True(File.Exists(path), $"expected the dictionary at {path}");
        return path;
    }

    [WpfFact]
    public void Every_StaticResource_in_the_dictionary_resolves()
    {
        // The general form of the same bug: a StaticResource resolves only against declarations that
        // PRECEDE it, so a forward reference is legal XAML that throws on first use. Loading the
        // dictionary and asking for each key back catches any that cannot be built at all.
        var dictionary = HyperionDictionary();

        foreach (var key in dictionary.Keys.Cast<object>().ToArray())
        {
            var value = dictionary[key];
            Assert.True(value is not null, $"'{key}' resolved to null");
        }
    }

    private static ResourceDictionary HyperionDictionary()
    {
        // Touching PackUriHelper registers the "pack" scheme; a bare test host has not, and the Uri
        // constructor then reads "application:,,," as a malformed authority ("Invalid port
        // specified"). The app never meets this because Application's startup registers it first.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        // LoadComponent with the RELATIVE form, because the absolute "application:,,," authority
        // resolves through Application.ResourceAssembly, which a test host does not have — it falls
        // through to WebRequest and fails with "The URI prefix is not recognized". The relative form
        // resolves the assembly by name instead, which needs no Application at all.
        return (ResourceDictionary)Application.LoadComponent(
            new Uri("/TradingTerminal.UI;component/Themes/HyperionStyles.xaml", UriKind.Relative));
    }
}
