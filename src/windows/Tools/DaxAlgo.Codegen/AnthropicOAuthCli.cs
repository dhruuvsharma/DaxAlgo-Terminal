using System.Diagnostics;
using System.IO;
using System.Text;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What a sign-in attempt produced.</summary>
/// <param name="Success">Whether there is now an active profile.</param>
/// <param name="Message">What to show the user — the reason on failure, the account on success.</param>
public readonly record struct AnthropicSignInResult(bool Success, string Message);

/// <summary>
/// Sign-in to the Anthropic API through the <c>ant</c> CLI, so nobody has to paste a key.
///
/// <para><c>ant auth login</c> opens a browser, and stores a profile under <c>%APPDATA%\Anthropic</c>;
/// <c>ant auth print-credentials --access-token</c> then mints a short-lived bearer, refreshing it when
/// needed. This class is those three commands and nothing else — the CLI owns the browser, the profile
/// and the refresh, exactly as Claude Code owns its own login.</para>
///
/// <para><b>What this is NOT.</b> It is not a way to spend a Claude Pro or Max subscription. The token
/// is bound to a Console organisation and workspace and is billed per token like any API key —
/// a subscription reaches this application only through the installed Claude Code CLI
/// (<see cref="AgentCliAdapter.ClaudeCode"/>), which is a different provider entirely. Offering this as
/// "sign in and it is free" would be a lie the first bill corrects.</para>
///
/// <para>Every method fails quietly to "not signed in" rather than throwing: an authoring pane must
/// stay usable when a CLI is missing, half-installed, or hung.</para>
/// </summary>
public sealed class AnthropicOAuthCli(
    Func<string, string?>? resolveOnPath = null,
    TimeSpan? timeout = null,
    Func<IEnumerable<string>>? searchDirectories = null)
{
    private const string Executable = "ant";

    private readonly Func<string, string?> _resolve = resolveOnPath ?? AgentCliCodegenClient.ResolveOnPath;

    /// <summary>Injectable so a test can say "nothing installed" on a machine that has it. Without this
    /// the well-known-directory search below reads the real filesystem, and a test asserting the
    /// not-installed state passes or fails depending on whose machine runs it.</summary>
    private readonly Func<IEnumerable<string>> _searchDirectories =
        searchDirectories ?? WellKnownDirectories;

    /// <summary>Generous: the login command waits for a human in a browser.</summary>
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(5);

    /// <summary>Whether the CLI is installed. Cheap — a PATH walk, no process launch.</summary>
    public bool IsInstalled => Locate() is not null;

    /// <summary>
    /// The CLI, on PATH or in a place the documented installers put it.
    ///
    /// <para><b>`go install` is the documented route on Windows, and it does not touch PATH.</b> It
    /// drops the binary in <c>%USERPROFILE%\goin</c>, which is not on PATH by default — so a user
    /// who had installed the CLI, signed in successfully, and could run it from a terminal was still
    /// told by this pane that it was not installed. Looking where the installer put it is the
    /// difference between "install this" and "install this, then go and edit your PATH".</para>
    /// </summary>
    private string? Locate()
    {
        if (_resolve(Executable) is { } onPath) return onPath;

        foreach (var directory in _searchDirectories())
        {
            var candidate = Path.Combine(directory, Executable + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> WellKnownDirectories()
    {
        // Where `go install` puts it: GOBIN if set, else GOPATH/bin, else ~/go/bin.
        if (Environment.GetEnvironmentVariable("GOBIN") is { Length: > 0 } gobin)
            yield return gobin;

        if (Environment.GetEnvironmentVariable("GOPATH") is { Length: > 0 } gopath)
            yield return Path.Combine(gopath, "bin");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, "go", "bin");
            yield return Path.Combine(home, ".local", "bin");
        }
    }

    /// <summary>Where to get it, for a pane that has to explain an unavailable button.</summary>
    public const string InstallHint =
        "Install the Anthropic CLI (`ant`) and press Recheck, or paste an API key instead.";

    /// <summary>
    /// Whether somebody is signed in right now.
    ///
    /// <para>Reads <c>ant auth status</c>, whose own documentation says it reports status only and must
    /// not be scripted against its exit code — so this reads what it printed instead.</para>
    /// </summary>
    public async Task<bool> IsSignedInAsync(CancellationToken ct = default)
    {
        var (ok, stdout, _) = await RunAsync(["auth", "status"], TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);

        return ok && ReportsSignedIn(stdout);
    }

    /// <summary>
    /// Whether <c>ant auth status</c> is describing a signed-in profile.
    ///
    /// <para><b>Reads the POSITIVE signal, and that is the whole point.</b> This looked for negatives —
    /// "not logged in", "no active" — and the real signed-out output contains neither of them:</para>
    /// <code>
    /// Credentials
    ///   (profile "default" not configured — run `ant auth login` to set it up)
    /// </code>
    /// <para>so it answered TRUE when nobody was signed in, and the pane offered a provider whose every
    /// request would fail. Signed in says <c>Logged in to &lt;org&gt; as &lt;email&gt;</c>; absence of
    /// that is not-signed-in, which is the safe direction to be wrong in. The exit code cannot help —
    /// it is 0 either way, which the CLI's own documentation warns about.</para>
    ///
    /// <para>Static and pure so it can be tested against real captured output rather than against a
    /// guess about what the CLI prints.</para>
    /// </summary>
    public static bool ReportsSignedIn(string? status) =>
        status is { Length: > 0 }
        && status.Contains("Logged in", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the browser sign-in and waits for it to finish.</summary>
    public async Task<AnthropicSignInResult> SignInAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return new AnthropicSignInResult(false, InstallHint);

        var (ok, stdout, stderr) = await RunAsync(["auth", "login"], _timeout, ct).ConfigureAwait(false);
        if (!ok)
        {
            var reason = First(stderr) ?? First(stdout) ?? "the CLI did not complete the sign-in.";
            return new AnthropicSignInResult(false, $"Sign-in failed: {reason}");
        }

        return await IsSignedInAsync(ct).ConfigureAwait(false)
            ? new AnthropicSignInResult(true, "Signed in. Requests are billed to that organisation.")
            : new AnthropicSignInResult(false, "The sign-in finished but no active profile was stored.");
    }

    /// <summary>Signs out, so a shared machine does not leave a token behind.</summary>
    public async Task<bool> SignOutAsync(CancellationToken ct = default)
    {
        var (ok, _, _) = await RunAsync(["auth", "logout"], TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        return ok;
    }

    /// <summary>
    /// A current access token, or null when nobody is signed in.
    ///
    /// <para><c>--access-token</c> is required: the bare form prints JSON, not a token, and a client
    /// that sent that JSON as a bearer would fail auth with nothing to explain it.</para>
    /// </summary>
    public async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return null;

        var (ok, stdout, _) =
            await RunAsync(["auth", "print-credentials", "--access-token"], TimeSpan.FromSeconds(60), ct)
                .ConfigureAwait(false);

        if (!ok) return null;

        var token = First(stdout);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string? First(string? text) => text?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private async Task<(bool Ok, string? Stdout, string? Stderr)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var exe = Locate();
        if (exe is null) return (false, null, null);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // UTF-8 on both pipes for the same reason the agent CLI sets it: a redirected pipe otherwise
            // inherits the console code page, and the failure is silent.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (false, null, null);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

            return (process.ExitCode == 0,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return (false, null, "the CLI did not answer in time.");
        }
        catch (Exception ex)
        {
            // A missing, half-installed or crashing CLI is "not signed in", never a broken pane.
            return (false, null, ex.Message);
        }
    }
}
