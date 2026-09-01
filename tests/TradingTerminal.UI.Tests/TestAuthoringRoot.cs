using System.IO;
using System.Runtime.CompilerServices;
using TradingTerminal.App.Authoring;


namespace TradingTerminal.UI.Tests;

/// <summary>
/// Points every per-user path this assembly can reach at a temp folder, before a single test runs.
///
/// <para><b>Because the redirect and the collection together were still not enough, and the proof was
/// in the user's own application.</b> They opened Hyperion and found three saved chats:
/// <c>agent-context.json</c>, <c>action-test.json</c> and <c>test-strategy.json</c> — the display names
/// of three fixtures in this assembly, sitting in the real session rail with nothing else beside them.
/// The whole feature looked like it produced naive prompts, because the only sessions to read were
/// ours.</para>
///
/// <para>Every one of those classes redirects correctly in its constructor. The hole is the other end:
/// <c>Dispose</c> put the static back to <see cref="AuthoringSessionStore.DefaultDirectory"/> — the
/// real folder — and a turn calls <c>Save()</c> in a finally, so anything still in flight between one
/// class tearing down and the next constructing landed on the user. One test made it certain rather
/// than likely: it assigned <c>Directory = null!</c> to prove the fallback is a real path, and left it
/// there.</para>
///
/// <para>So the default is moved rather than the discipline tightened. A class may still redirect to
/// its own folder for isolation, and restores to <see cref="Directory"/> instead of to the user's.
/// A class that forgets entirely now writes to temp rather than to somebody's chat list, which is the
/// property that was missing: the safe thing happens when nobody remembers to do it.</para>
///
/// <para>This is the THIRD time fixtures have escaped into real per-user state —
/// <see cref="AuthoringSessionStore.Directory"/> documents the first, <c>AiCodegenUserFile.Path</c> the
/// second. Both were found by looking at the running application, never by a test.</para>
/// </summary>
internal static class TestAuthoringRoot
{
    /// <summary>The folder this assembly's authoring state lives in for the life of the run.</summary>
    public static string Directory { get; } = Path.Combine(
        Path.GetTempPath(), "daxalgo-ui-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>The provider file, redirected for the same reason and found the same way.</summary>
    public static string ProviderFile { get; } = Path.Combine(Directory, "ai-codegen.json");

    /// <summary>
    /// Runs before any test in this assembly, which is the point: a fixture cannot opt out of it, and
    /// a new test class cannot forget it.
    /// </summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        AuthoringSessionStore.Directory = Directory;
        AiCodegenUserFile.Path = ProviderFile;
    }
}
