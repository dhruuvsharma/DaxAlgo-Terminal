using System.IO;
using TradingTerminal.App.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// No fixture in this assembly may write into the folder the real application reads.
///
/// <para><b>Written because it happened, and because nothing here could see it.</b> The user opened
/// Hyperion and found three saved chats — <c>agent-context</c>, <c>action-test</c> and
/// <c>test-strategy</c> — with nothing else beside them, and concluded the feature produced naive
/// prompts. It produced nothing; those are display names from this assembly.</para>
///
/// <para>Every suite involved was already redirecting correctly in its constructor. The escape was at
/// the other end — <c>Dispose</c> restoring the REAL directory, and one test assigning it deliberately
/// to prove the fallback resolves — so the guard has to be about where the static points, not about
/// whether a class remembered to redirect.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class NoFixtureReachesTheUsersFolderTests
{
    [Fact]
    public void The_session_store_is_never_aimed_at_the_real_folder()
    {
        Assert.NotEqual(AuthoringSessionStore.DefaultDirectory, AuthoringSessionStore.Directory);
        Assert.StartsWith(Path.GetTempPath(), AuthoringSessionStore.Directory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_provider_file_is_never_aimed_at_the_real_one()
    {
        Assert.NotEqual(AiCodegenUserFile.DefaultPath, AiCodegenUserFile.Path);
        Assert.StartsWith(Path.GetTempPath(), AiCodegenUserFile.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_module_initializer_ran_before_any_test_did()
    {
        // The mechanism itself, asserted. A redirect that depends on a base class or a convention is
        // one a new test class can forget; a module initializer cannot be opted out of.
        //
        // Both statics must carry ITS root specifically, not merely some temp path — a class that
        // redirected to a temp folder of its own and never restored would satisfy a looser check while
        // leaving the next class pointed wherever it liked.
        Assert.Contains("daxalgo-ui-tests-", TestAuthoringRoot.Directory, StringComparison.Ordinal);
        Assert.StartsWith(TestAuthoringRoot.Directory, TestAuthoringRoot.ProviderFile, StringComparison.Ordinal);
    }
}
