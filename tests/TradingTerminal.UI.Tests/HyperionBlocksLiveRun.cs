using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.App.Login;
using TradingTerminal.Blocks.WebHost;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// One brief through Hyperion's Blocks pipeline, against TokenRouter, with everything left on disk: the
/// unit and its page, a photograph of the page, the package, the trajectory, and a saved session that
/// opens in the builder.
///
/// <para><b>Off unless <c>HYPERION_BLOCKS_LIVE=1</c>.</b> It spends a provider's tokens and takes minutes.
/// Everything below the harness is the code the pane runs for a Blocks turn — the same session in Blocks
/// mode, the same runner, dialect, gate and critics — so what it measures is the product.</para>
/// </summary>
public sealed class HyperionBlocksLiveRun(ITestOutputHelper output)
{
    /// <summary>
    /// <c>HYPERION_PROVIDER</c>, <c>HYPERION_BASE_URL</c> and <c>HYPERION_MODEL</c> point the run at any
    /// OpenAI-compatible endpoint. Unset is OpenCode Zen on <c>union-alpha</c> — the owner's choice for
    /// testing from 2026-09-17. The two before it are kept as named rows because the comparison runs on
    /// disk were measured against them: Token Harbor's free DeepSeek V4 Flash built the first delivered
    /// V4, and TokenRouter's free GLM (whose channel closed on 2026-09-15) built none.
    /// </summary>
    private static string ProviderId => Env("HYPERION_PROVIDER") ?? "opencode";

    private static string ProviderName => ProviderId switch
    {
        "opencode" => "OpenCode Zen",
        "tokenharbor" => "Token Harbor",
        "tokenrouter" => "TokenRouter",
        _ => ProviderId,
    };

    private static string BaseUrl => Env("HYPERION_BASE_URL") ?? ProviderId switch
    {
        "opencode" => "https://opencode.ai/zen/v1",
        "tokenharbor" => "https://tokenharbor.ai/v1",
        _ => "https://api.tokenrouter.com/v1",
    };

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary><c>HYPERION_MODEL</c>, or what the provider is being tested on.</summary>
    private static string Model => Env("HYPERION_MODEL") ?? ProviderId switch
    {
        "opencode-cli" => "opencode/union-alpha",
        "opencode" => "union-alpha",
        "tokenharbor" => "deepseek-v4-flash:free",
        _ => "z-ai/glm-5.3-free",
    };

    /// <summary>The widget-SDK run this one is compared with, when its trajectory is on disk.</summary>
    private const string BaselineRun = "volume.graph.v3";

    private const string DefaultBrief = """
        Build a volume footprint chart, called Volume Graph V4.

        A volume footprint shows what traded at each price inside each bar, split into the volume that hit the bid and the volume that lifted the ask, so a trader can see where the buying and the selling actually happened rather than only where the price ended up.

        THE PICTURE IS ENTIRELY YOURS. Nothing about how it looks is specified and nothing is off limits. You decide the layout, the proportions, the colours, the type sizes, what counts as a bar, how much history is on screen, what the axes are and where they go, which numbers are drawn and which are implied by shape, and what the viewer can do with the mouse. If you think the standard footprint layout is wrong, build the one you think is right.

        The only things fixed are the ones that are not yours:

        - the C# unit does the data and the maths: it subscribes to the trades and quotes of the instrument the viewer picks, classifies each print as hitting the bid or lifting the ask, and aggregates the footprint (the maths blocks already bucket footprints)
        - the page in ui/ does all of the drawing, from the whole state the unit sends it
        - values the viewer can change are settings, and every setting you declare is read
        - memory is bounded: trim what you keep rather than appending for ever
        - it is a visualizer: it takes no positions

        Make it something a trader would want to look at.
        """;

    private static string Root => Environment.GetEnvironmentVariable("HYPERION_OUT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgo Terminal", "hyperion-runs");

    [Fact]
    public async Task Build_one_blocks_brief()
    {
        if (Environment.GetEnvironmentVariable("HYPERION_BLOCKS_LIVE") != "1") return;

        // HYPERION_INSTALL names a package a run wrote. It is installed the way Compile & Register installs
        // one — the same installer, the sandbox scan profile the loader uses, consent recorded in the units
        // folder's state — so the app shows it on its next start. A run's package sits in its run folder
        // until then, and the app never looks there.
        if (Env("HYPERION_INSTALL") is { } package)
        {
            var root = AuthoredUnitsRoot.Ensure();
            Assert.NotNull(root);

            var store = new AuthoredUnitStore(new PluginHostContext(
                AuthoredUnitsRoot.Path,
                PluginTrustPolicy.Permissive,
                LoadedPlugins: [],
                State: new PluginStateStore(AuthoredUnitsRoot.Path)));

            var installed = store.Install(package, root!);
            output.WriteLine(installed.Message);
            Assert.True(installed.Success, installed.Message);
            return;
        }

        // HYPERION_CHECK_UNITS=curated|permissive loads a COPY of the units folder the way the app does at
        // start under that trust policy (shipped appsettings: Curated; the New User and Testing launch
        // profiles: Permissive), sandbox profile, and lists what reaches the Blocks catalog. A copy, because loading the
        // real one writes quarantine records into its state file.
        if (Env("HYPERION_CHECK_UNITS") is { } checkPolicy)
        {
            var copy = Path.Combine(Path.GetTempPath(), "daxalgo-units-check-" + Guid.NewGuid().ToString("N"));
            foreach (var source in Directory.GetFiles(AuthoredUnitsRoot.Path, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(AuthoredUnitsRoot.Path, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            var report = PluginLoader.LoadSandboxedWithReport(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection(), copy, DaxAlgo.Sdk.SdkInfo.Version,
                checkPolicy == "permissive" ? PluginTrustPolicy.Permissive : PluginTrustPolicy.Curated([]), new PluginStateStore(copy));

            var lines = new List<string>();
            lines.AddRange(report.Loaded.Select(loaded => $"loaded: {loaded.Name}"));
            lines.AddRange(report.Problems.Select(problem => $"PROBLEM: {problem.PluginFolderName} - {problem.Outcome}: {problem.Reason}"));

            var catalog = new TradingTerminal.Blocks.Runtime.BlocksUnitRegistry();
            BlocksPackage.Register(report.Loaded, catalog);
            lines.AddRange(catalog.All.Select(unit => $"blocks catalog: {unit}"));

            Directory.CreateDirectory(Root);
            File.WriteAllLines(Path.Combine(Root, "units-check.txt"), lines);
            return;
        }

        var id = Environment.GetEnvironmentVariable("HYPERION_ID") is { Length: > 0 } given ? given : "volume.graph.v4";
        var name = Environment.GetEnvironmentVariable("HYPERION_NAME") is { Length: > 0 } named ? named : "Volume Graph V4";
        var text = Environment.GetEnvironmentVariable("HYPERION_TEXT") is { Length: > 0 } custom ? custom : DefaultBrief;
        const AuthoringKind kind = AuthoringKind.Visualizer;

        var directory = Path.Combine(Root, id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        using var log = new StreamWriter(
            new FileStream(Path.Combine(directory, "run.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };

        var clock = Stopwatch.StartNew();
        var pen = new Lock();
        void Say(string line)
        {
            var stamped = $"[{clock.Elapsed:hh\\:mm\\:ss}] {line}";
            lock (pen)
            {
                log.WriteLine(stamped);
                output.WriteLine(stamped);
            }
        }

        Say($"{name} ({kind}) — Blocks SDK");

        // HYPERION_API_KEY_FILE names a file holding the key; otherwise it comes from the app's key store,
        // where Settings → AI providers saves it. Never the key itself in a variable: a command line is
        // logged far more readily than a file. With HYPERION_SAVE_KEY=1 the file's key is saved to the
        // store (encrypted for this Windows user) and the run ends there.
        // An installed agent CLI carries its own sign-in, so it needs neither key nor base URL.
        var cli = AgentCliAdapter.All.FirstOrDefault(a => a.ProviderId == ProviderId);

        var keys = new AiKeyStore(NullLogger<AiKeyStore>.Instance);
        var key = Env("HYPERION_API_KEY_FILE") is { } keyFile
            ? File.ReadAllText(keyFile).Trim()
            : keys.Get(ProviderId);

        if (Env("HYPERION_SAVE_KEY") == "1")
        {
            Assert.False(string.IsNullOrWhiteSpace(key), "HYPERION_SAVE_KEY needs HYPERION_API_KEY_FILE.");
            keys.Set(ProviderId, key!);
            Assert.True(keys.HasKey(ProviderId), $"The key for '{ProviderId}' did not persist.");
            Say($"key saved for '{ProviderId}'");
            return;
        }

        if (cli is null && string.IsNullOrWhiteSpace(key))
        {
            Say($"NO KEY for '{ProviderId}'. Add it in Settings → AI providers and run again.");
            Assert.Fail($"No stored key for '{ProviderId}'.");
            return;
        }

        // The effort the product sends for this provider and model — what Research resolves to. Where the
        // catalog knows the provider takes one, it goes through the client; HYPERION_REASONING overrides it,
        // and on a provider the catalog does not trust with one it is injected on the wire as an experiment.
        var reasoning = Env("HYPERION_REASONING");
        var drop = Env("HYPERION_DROP");

        var effort = reasoning is not null ? CodegenEfforts.Parse(reasoning) : AiModelCatalog.ResearchEffort(ProviderId, Model);
        var inject = reasoning is not null && !AiModelCatalog.SupportsEffort(ProviderId);
        using var http = inject || drop is not null
            ? new HttpClient(new WithReasoningEffort(inject ? reasoning : null, drop)) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        IStrategyCodegenClient client = new KeepsReplies(
            cli is not null
                // The vendor's own program answers the prompt, with the sign-in it already holds. For
                // OpenCode's free models it is the only client their gateway serves at all.
                ? new AgentCliCodegenClient(
                    cli,
                    model: Model,
                    effort: effort,
                    timeout: TimeSpan.FromMinutes(Count(Env("HYPERION_CALL_MINUTES")) ?? 30))
                : new OpenAiCompatibleCodegenClient(
                    http, ProviderId, Env("HYPERION_PROVIDER_NAME") ?? ProviderName, BaseUrl, Model, key, effort: effort),
            Path.Combine(directory, "replies"));

        // What this key can use, and nothing else: no model is called, so it costs nothing.
        if (Environment.GetEnvironmentVariable("HYPERION_LIST_MODELS") == "1")
        {
            var models = await client.ListModelsAsync();
            Say($"{models.Count} model(s) on this key:");
            foreach (var model in models) Say("  " + model);
            return;
        }

        Say($"provider: {client.ProviderId} · {client.Model}" + (effort == CodegenEffort.Default ? string.Empty : $" · reasoning_effort={effort.ToString().ToLowerInvariant()}"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Count(Environment.GetEnvironmentVariable("HYPERION_MINUTES")) ?? 120));

        var blocks = new BlocksAuthoring(new BlocksUnitCompiler(), registry: null, probe: new WebPageProbe());
        var profile = StrategyBuildProfile.For(CodegenMode.Research, AiModelCatalog.ResearchEffort(ProviderId, Model));

        var session = new StrategyCodegenOrchestrator(new RoslynStrategyCompiler())
            .CreateSession(client, blocks.Catalog.SharedContext, id, name, profile.MaxFixAttempts, profile: profile, kind: kind);
        session.UseBlocks(blocks.Catalog.SharedContext);

        // A free endpoint rate-limits a wide fan-out; two in flight and four tasks is what finishes.
        var budget = SwarmBudget.For(profile) with
        {
            MaxParallel = Count(Environment.GetEnvironmentVariable("HYPERION_PARALLEL")) ?? 2,
            MaxTasks = Math.Max(2, Count(Environment.GetEnvironmentVariable("HYPERION_TASKS")) ?? 4),
        };
        if (Count(Environment.GetEnvironmentVariable("HYPERION_ROUNDS")) is { } rounds) budget = budget with { MaxRounds = rounds };
        Say($"budget: {budget.MaxParallel} parallel · {budget.MaxRounds} round(s) · {budget.MaxTasks} task(s) max");

        var gate = blocks.Gate(id);
        var trajectory = Path.Combine(directory, "trajectory.jsonl");
        var beats = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        StrategyBuildTurn turn;

        try
        {
            turn = await session.SendToSwarmAsync(
                text,
                new SwarmRunner(
                    client,
                    gate,
                    new TrajectoryLog(trajectory),
                    logger: null,
                    gauntlet: GauntletLoop.For(client, vision: null, session.SystemContext, kind, definitions: BlocksAuthoring.Critics(kind)),
                    rasterizer: null,
                    dialect: blocks.Dialect),
                budget,
                bar: null,
                images: null,
                mayAsk: false,
                activity: null,
                swarm: new Progress<SwarmEvent>(evt =>
                {
                    if (evt is SwarmEvent.TaskProgress beat)
                    {
                        lock (pen)
                        {
                            if (beats.TryGetValue(beat.Task.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(15)) return;
                            beats[beat.Task.Id] = DateTime.UtcNow;
                        }
                    }

                    Say(Describe(evt));
                }),
                events: null,
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Say($"THREW: {ex.GetType().Name}: {ex.Message}");
            Save(id, name, text, session.Files, session, gate, directory, clock.Elapsed, "did not finish: " + ex.Message, trajectory, Say);
            throw;
        }

        Say($"turn: {turn.Kind} · {turn.Files.Count} file(s)");

        // Judged again from what is on disk, with the page open, so the picture and the report describe
        // exactly the files handed over.
        var verdict = turn.Files.Count > 0 ? await gate.RunAsync(turn.Files) : null;
        if (verdict is not null)
        {
            Say($"gate: passed={verdict.Passed} rungs={verdict.Report.RungsCleared} failed at {verdict.Report.FailedAt?.ToString() ?? "nothing"}");
            foreach (var finding in verdict.Report.Findings) Say($"  {finding}");
        }

        Save(id, name, text, turn.Files, session, gate, directory, clock.Elapsed, turn.AssistantText, trajectory, Say);
        Say($"written to {directory}");

        // A LIVE RUN THAT BUILT NOTHING IS A FAILED TEST. Everything is on disk by now, so failing costs no
        // evidence — and a green result over a unit that never compiled is how a run with no unit and no
        // page was reported as "Test Run Successful".
        // And a visualizer brief is not delivered without its page: the gate passes a unit that has none.
        Assert.True(
            turn.Files.Any(f => CodegenCodeExtractor.IsPageFile(f.Name)),
            $"{name} has no page: {string.Join(", ", turn.Files.Select(f => f.Name))}. See {Path.Combine(directory, "summary.md")}.");
        Assert.True(
            verdict?.Passed == true,
            $"{name} was not delivered ({turn.Kind}): "
            + (verdict is null
                ? "no files came back."
                : $"stopped at {verdict.Report.FailedAt?.ToString() ?? "nothing"} — "
                  + string.Join(" | ", verdict.Report.Findings.Take(3).Select(f => f.Code)))
            + $" See {Path.Combine(directory, "summary.md")}.");
    }

    private static void Save(
        string id, string name, string brief, IReadOnlyList<StrategyFile> files, StrategyBuildSession session, BlocksGate gate,
        string directory, TimeSpan elapsed, string reply, string trajectory, Action<string> say)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(directory, "unit", file.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }

        if (gate.LatestPicture is { } picture) File.WriteAllBytes(Path.Combine(directory, $"{id}.png"), picture.Png);

        string package = "not written";
        if (gate.Latest is { Success: true } compiled)
        {
            var artifact = AuthoredArtifact.Write(new StrategyScript(id, name, files), compiled, directory);
            package = artifact.Success ? Path.GetFileName(artifact.Path!) : artifact.Message;
        }

        var calls = Calls(trajectory);
        var baseline = Calls(Path.Combine(Root, BaselineRun, "trajectory.jsonl"));

        var summary = new StringBuilder()
            .AppendLine($"# {name}")
            .AppendLine()
            .AppendLine("- sdk: Blocks (cards per task, page in ui/)")
            .AppendLine($"- provider: {ProviderId} · {Model}")
            .AppendLine($"- elapsed: {elapsed:hh\\:mm\\:ss}")
            .AppendLine($"- tokens: {session.TotalUsage.InputTokens} in / {session.TotalUsage.OutputTokens} out")
            .AppendLine($"- model calls: {calls}")
            .AppendLine($"- baseline {BaselineRun} (widget SDK): {baseline}"
                        + (baseline.Unanswered == 0 ? " — unanswered calls are only logged since 2026-09-15, so a run logged earlier shows none" : string.Empty))
            .AppendLine($"- compiled: {(gate.Latest?.Success == true ? "yes" : "NO")}")
            .AppendLine($"- package: {package}")
            .AppendLine()
            .AppendLine("## brief").AppendLine().AppendLine(brief).AppendLine()
            .AppendLine("## files").AppendLine();

        foreach (var file in files) summary.AppendLine($"- `{file.Name}` — {file.Content.Length} chars");
        summary.AppendLine().AppendLine("## reply").AppendLine().AppendLine(reply);
        File.WriteAllText(Path.Combine(directory, "summary.md"), summary.ToString());

        // The builder's real session list, so the unit opens in Hyperion ready for Compile & Register.
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        var now = DateTime.Now;
        AuthoringSessionStore.Save(new AuthoringSessionSnapshot(
            id, name,
            [new AuthoringChatEntry(AuthoringChatEntry.User, brief, now), new AuthoringChatEntry(AuthoringChatEntry.Assistant, reply, now)],
            session.Transcript, files, ProviderId, Model, CodegenEffort.Default.Wire(), StrategyBuildEffort.Max.Wire(),
            session.TotalUsage.InputTokens, session.TotalUsage.OutputTokens));

        say($"tokens: {session.TotalUsage.InputTokens} in / {session.TotalUsage.OutputTokens} out · {calls}");
        say($"baseline {BaselineRun}: {baseline}");
    }

    /// <summary>What a trajectory says about the model calls in it.</summary>
    /// <param name="Calls">Every request to the model, answered or not. Gate rows are not calls.</param>
    /// <param name="Unanswered">Calls that came back with no answer.</param>
    /// <param name="UnansweredOutput">Output tokens billed to those calls.</param>
    /// <param name="Billed">Calls that reported input — the denominator for input per call, so a gateway
    /// error billed nothing does not drag the average down.</param>
    private sealed record CallCount(int Calls, int Unanswered, long UnansweredOutput, int Billed, long Input, long Largest)
    {
        // Invariant: the machine's own grouping printed "1,43,814", which reads as a different number.
        public override string ToString()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Create(inv, $"{Calls} call(s)")
                + (Unanswered > 0 ? string.Create(inv, $", {Unanswered} of them unanswered ({UnansweredOutput:N0} output tokens spent on those)") : string.Empty)
                + string.Create(inv, $" · input per billed call: {(Billed == 0 ? 0 : Input / Billed):N0} average, {Largest:N0} largest");
        }
    }

    /// <summary>Counts the model calls in a trajectory log.</summary>
    private static CallCount Calls(string path)
    {
        if (!File.Exists(path)) return new CallCount(0, 0, 0, 0, 0, 0);

        var calls = new TrajectoryLog(path).Read().Where(e => e.IsModelCall).ToArray();
        var billed = calls.Where(e => e.InputTokens > 0).ToArray();

        return new CallCount(
            calls.Length,
            calls.Count(e => e.Unanswered),
            calls.Where(e => e.Unanswered).Sum(e => (long)e.OutputTokens),
            billed.Length,
            billed.Sum(e => (long)e.InputTokens),
            billed.Length == 0 ? 0 : billed.Max(e => e.InputTokens));
    }

    private static string Describe(SwarmEvent evt) => evt switch
    {
        SwarmEvent.Planning => "planning…",
        SwarmEvent.Planned p => $"plan ({p.Origin}): " + string.Join(" · ", p.Plan.Tasks.Select(t => $"{t.Kind} {t.OwnedFile} [{string.Join(",", t.Cards)}]")),
        SwarmEvent.MilestoneStarted m => $"milestone: {m.Milestone.Title}",
        SwarmEvent.TaskStarted s => $"  → {(s.IsRepair ? "repair" : "build")} {s.Task.OwnedFile}",
        SwarmEvent.TaskProgress p => $"     {p.Task.OwnedFile}: {p.Thinking} thought / {p.Written} written / {p.Usage.TotalTokens} tok",
        SwarmEvent.TaskFinished f => $"  ← {f.Task.OwnedFile}: {(f.Wrote ? "written" : "NOTHING — " + (f.Note ?? "no file"))} · {f.Usage.TotalTokens} tok",
        SwarmEvent.Dropped d => $"  dropped {string.Join(", ", d.Files)} — {d.Why}",
        SwarmEvent.Gated g => g.Report.Passed
            ? $"gate round {g.Round}: PASSED, {g.Report.RungsCleared} rung(s)"
            : $"gate round {g.Round}: {g.Report.Findings.Count} problem(s) at {g.Report.FailedAt} — " + string.Join(" | ", g.Report.Findings.Take(4).Select(f => f.ToString())),
        SwarmEvent.Reviewed r => $"critics round {r.Round}: {r.Result.Summary}",
        SwarmEvent.Finished f => $"FINISHED {f.Outcome}: {f.Summary}",
        _ => evt.GetType().Name,
    };

    /// <summary>
    /// Writes every reply the model sends back — its text, or the error — to <c>replies/</c> in the run
    /// folder, one file per call.
    ///
    /// <para>A developer's run folder, not the product's trajectory, which by design keeps no text. Added
    /// after a page builder "returned no file" four times and the only evidence was a token count.</para>
    /// </summary>
    private sealed class KeepsReplies(IStrategyCodegenClient inner, string folder) : IStrategyCodegenClient
    {
        private int _calls;

        public string ProviderId => inner.ProviderId;
        public string DisplayName => inner.DisplayName;
        public bool IsAvailable => inner.IsAvailable;
        public string Model => inner.Model;
        public CodegenEffort Effort => inner.Effort;
        public IReadOnlyList<string> KnownModels => inner.KnownModels;

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => inner.ListModelsAsync(ct);

        public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default) =>
            Keep(request, await inner.GenerateAsync(request, ct));

        public async IAsyncEnumerable<CodegenEvent> StreamAsync(
            StrategyCodegenRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in inner.StreamAsync(request, ct))
            {
                if (evt is CodegenEvent.Completed done) Keep(request, done.Response);
                yield return evt;
            }
        }

        private StrategyCodegenResponse Keep(StrategyCodegenRequest request, StrategyCodegenResponse response)
        {
            try
            {
                var n = Interlocked.Increment(ref _calls);
                var role = (request.RoleInstruction ?? "call").Split('\n')[0];
                var slug = new string(role.Where(char.IsLetterOrDigit).Take(40).ToArray());
                Directory.CreateDirectory(folder);
                File.WriteAllText(
                    Path.Combine(folder, $"{n:00}-{slug}.md"),
                    $"# {role}\n\nsuccess: {response.Success} · usage: {response.Usage?.InputTokens} in / {response.Usage?.OutputTokens} out\n"
                    + (response.Error is { } error ? $"\nerror: {error}\n" : string.Empty)
                    + $"\n---\n\n{response.RawText}");
            }
            catch (IOException)
            {
                // Diagnostics only.
            }

            return response;
        }
    }

    /// <summary>
    /// Rewrites every chat-completions body on its way out: adds <c>reasoning_effort</c>
    /// (<c>HYPERION_REASONING</c>), and removes the fields named in <c>HYPERION_DROP</c>.
    ///
    /// <para>Dropping is a diagnostic. A gateway that answers <c>500 Internal server error</c> says nothing
    /// about which field it choked on, and the way to find out is to send the same brief without one —
    /// <c>temperature</c> and <c>stream_options</c> are the usual suspects on a new model.</para>
    /// </summary>
    private sealed class WithReasoningEffort(string? effort, string? drop = null) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true)
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
                if (body is System.Text.Json.Nodes.JsonObject json)
                {
                    if (effort is { Length: > 0 }) json["reasoning_effort"] = effort;
                    foreach (var field in (drop ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        json.Remove(field);

                    request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
                }
            }

            return await base.SendAsync(request, ct);
        }
    }

    private static int? Count(string? value) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}
