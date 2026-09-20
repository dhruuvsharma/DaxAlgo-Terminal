using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What the 2026-09-19 Battlefield comparison taught Hyperion, held in place:
///
/// <para>A RICH PAGE IS SPLIT across builders — a shell and its modules, each owning its files, meeting
/// through exports fixed in the contract. A REPLY CUT OFF mid-file is continued rather than thrown away.
/// The PICTURE CRITIC looks when something can see and reads the source when nothing can, and a critic
/// that thinks without answering is asked once more at a medium effort. And the clients send what a
/// gateway needs (an output cap), keep what a cut reply wrote, and name the real reason a call failed.</para>
/// </summary>
public sealed class HyperionBuildsRichPagesTests
{
    // ── a page split across builders ────────────────────────────────────────────────────────────

    private const string SplitPlan = """
        {
          "contract": {
            "typeName": "Battlefield",
            "topics": [ { "name": "battle", "direction": "to-page", "payload": "{ price: number }" } ],
            "pageModules": [
              { "file": "ui/scene.js", "purpose": "the 3D field", "exports": "export function mountScene(el): { update(s): void }" },
              { "file": "ui/depth.js", "purpose": "the depth chart", "exports": "export function mountDepth(el): { update(d): void }" }
            ]
          },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "Battlefield.cs", "blocks": ["unit", "ui"], "intent": "the unit", "dependsOn": [] },
              { "id": "t2", "title": "Shell", "kind": "Ui", "ownedFile": "ui/index.html", "blocks": ["ui"], "intent": "the shell", "dependsOn": [] },
              { "id": "t3", "title": "Scene", "kind": "Ui", "ownedFile": "ui/scene.js", "blocks": ["ui"], "intent": "the scene", "dependsOn": [] },
              { "id": "t4", "title": "Depth", "kind": "Ui", "ownedFile": "ui/depth.js", "blocks": ["ui"], "intent": "the depth chart", "dependsOn": [] }
            ]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    [Fact]
    public void A_split_page_gives_each_module_its_file_and_the_shell_everything_else()
    {
        var plan = BuildPlanReader.Read("```json\n" + SplitPlan + "\n```", AuthoringKind.Visualizer)!;
        var shell = plan.Tasks.Single(t => t.Id == "t2");
        var scene = plan.Tasks.Single(t => t.Id == "t3");

        plan.Contract.PageModules.Select(m => m.File).Should().Equal("ui/scene.js", "ui/depth.js");

        shell.OwnsPageShell.Should().BeTrue();
        shell.Owns("ui/index.html").Should().BeTrue();
        shell.Owns("ui/app.js").Should().BeTrue("a page file no module owns is the shell's");
        shell.Owns("ui/style.css").Should().BeTrue();
        shell.Owns("ui/scene.js").Should().BeFalse("the scene module owns it");

        scene.PageModuleOnly.Should().BeTrue();
        scene.Owns("ui/scene.js").Should().BeTrue();
        scene.Owns("ui/index.html").Should().BeFalse();
        scene.Owns("ui/depth.js").Should().BeFalse();

        // A module cut from the budget hands its file back to the shell.
        var trimmed = plan with { Milestones = [plan.Milestones[0] with { Tasks = [.. plan.Milestones[0].Tasks.Where(t => t.Id != "t4")] }] };
        trimmed.WithPageOwnership().Tasks.Single(t => t.Id == "t2").Owns("ui/depth.js").Should().BeTrue();

        // And a page written by ONE task still owns the whole folder.
        var single = BuildPlanReader.Read("```json\n" + SplitPlan.Replace("\"ownedFile\": \"ui/scene.js\"", "\"ownedFile\": \"Scene.cs\"")
            .Replace("\"ownedFile\": \"ui/depth.js\"", "\"ownedFile\": \"Depth.cs\"") + "\n```", AuthoringKind.Visualizer)!;
        single.Tasks.Single(t => t.Id == "t2").Owns("ui/scene.js").Should().BeTrue();
    }

    [Fact]
    public void A_module_keeps_only_its_own_file_and_cannot_overwrite_the_shell()
    {
        var plan = BuildPlanReader.Read("```json\n" + SplitPlan + "\n```", AuthoringKind.Visualizer)!;
        var context = new SwarmContext("battlefield");

        context.Accept(plan.Tasks.Single(t => t.Id == "t2"),
        [
            new StrategyFile("ui/index.html", "<html>shell</html>"),
            new StrategyFile("ui/scene.js", "// the shell tried to write the scene"),
        ]).Should().BeTrue();

        context.Accept(plan.Tasks.Single(t => t.Id == "t3"),
        [
            new StrategyFile("ui/scene.js", "export function mountScene() {}"),
            new StrategyFile("ui/index.html", "<html>the module tried to write the shell</html>"),
        ]).Should().BeTrue();

        context.File("ui/index.html")!.Content.Should().Be("<html>shell</html>");
        context.File("ui/scene.js")!.Content.Should().Be("export function mountScene() {}");

        // One file under another name is still the module's file.
        context.Accept(plan.Tasks.Single(t => t.Id == "t4"), [new StrategyFile("ui/depthchart.js", "export function mountDepth() {}")])
            .Should().BeTrue();
        context.File("ui/depth.js")!.Content.Should().Contain("mountDepth");
    }

    [Fact]
    public async Task Every_module_is_built_by_its_own_builder_and_a_module_fault_goes_to_that_module()
    {
        var client = new ByRole(role =>
            role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + SplitPlan + "\n```"
            : role.Contains("ONE MODULE of the unit's page: ui/scene.js", StringComparison.Ordinal) ? "```js\n// file: ui/scene.js\nexport function mountScene(el) { return { update() {} }; }\n```"
            : role.Contains("ONE MODULE of the unit's page: ui/depth.js", StringComparison.Ordinal) ? "```js\n// file: ui/depth.js\nexport function mountDepth(el) { return { update() {} }; }\n```"
            : role.Contains("PAGE SHELL", StringComparison.Ordinal) ? "```html\n<!-- file: ui/index.html -->\n<script type=\"module\" src=\"scene.js\"></script>\n```"
            : role.Contains("ONE page file, ui/scene.js", StringComparison.Ordinal) ? "```js\n// file: ui/scene.js\nexport function mountScene(el) { return { update() {} }; } // fixed\n```"
            : "```csharp\n// file: Battlefield.cs\npublic sealed class Battlefield { }\n```");

        await new SwarmRunner(client, new SceneThrowsOnce(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest("battlefield", "PACK", AuthoringKind.Visualizer, new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 8)));

        var roles = client.Roles.ToArray();
        roles.Should().Contain(r => r.Contains("ONE MODULE of the unit's page: ui/scene.js", StringComparison.Ordinal));
        roles.Should().Contain(r => r.Contains("ONE MODULE of the unit's page: ui/depth.js", StringComparison.Ordinal));
        roles.Should().Contain(r => r.Contains("PAGE SHELL", StringComparison.Ordinal));
        roles.Should().Contain(r => r.Contains("ONE page file, ui/scene.js", StringComparison.Ordinal),
            "a script error in scene.js is the scene module's to fix");
        roles.Should().NotContain(r => r.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal) && r.Contains("the unit's page", StringComparison.Ordinal),
            "the shell is not asked to repair the scene");
    }

    /// <summary>Fails the first round with a script error in ui/scene.js, then passes.</summary>
    private sealed class SceneThrowsOnce : IUnitGate
    {
        private int _runs;

        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(Interlocked.Increment(ref _runs) == 1
                ? new GateResult(new VerificationReport(
                [
                    VerificationStep.Pass(VerificationRung.Compile),
                    VerificationStep.Fail(VerificationRung.DrawProbe,
                        new VerificationFinding("page.threw", "1 script error(s) on the page; the first: boom (scene.js:3)", "Fix the script.", "ui/scene.js")),
                ]), Compile: null)
                : new GateResult(new VerificationReport([VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Pass(VerificationRung.DrawProbe)]), Compile: null));
    }

    // ── a reply cut off mid-file ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_cut_off_inside_a_file_is_continued_and_the_file_arrives_whole()
    {
        // Measured 2026-09-19: GLM 5.3 Flash's page reply stopped inside its stylesheet with no finish
        // reason, the half block parsed as nothing, and its script was never written.
        var calls = 0;
        var client = new ByRole((role, request) =>
        {
            if (role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal)) return "no plan";
            return Interlocked.Increment(ref calls) switch
            {
                1 => "```csharp\n// file: Unit.cs\npublic sealed class Unit\n{\n    public int A => 1;\n",
                _ => request.Messages.Count == 3 ? "    public int B => 2;\n}\n```" : "```csharp\n// file: Unit.cs\npublic sealed class Unit { }\n```",
            };
        });

        var run = await new SwarmRunner(client, new SceneThrowsOnce())
            .RunAsync(new SwarmRequest("x", "PACK", AuthoringKind.Visualizer, new SwarmBudget(1, 0, 1)) with { MayAsk = false });

        run.Files.Single(f => f.Name == "Unit.cs").Content.Should().Contain("public int A => 1;").And.Contain("public int B => 2;");
    }

    [Fact]
    public async Task A_builder_that_thinks_without_answering_is_asked_once_more_at_a_medium_effort()
    {
        // Measured 2026-09-20 at each model's maximum effort: five of Nex N2.5 Pro's nine calls returned
        // nothing after 225,335 output tokens, and DeepSeek V4 Flash spent 1h52m on one planning call.
        var efforts = new ConcurrentQueue<CodegenEffort?>();
        var client = new ByRole((role, request) =>
        {
            if (role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal)) return "I need to think about this.";
            efforts.Enqueue(request.Effort);
            return request.Effort is null ? null : "```csharp\n// file: Unit.cs\npublic sealed class Unit { }\n```";
        }, provider: "nvidia", model: "deepseek-ai/deepseek-v4-flash-0731", effort: CodegenEffort.High,
           failure: "NVIDIA NIM spent the whole generation reasoning and never started an answer.");

        var run = await new SwarmRunner(client, new AlwaysPasses())
            .RunAsync(new SwarmRequest("a battlefield", "PACK", AuthoringKind.Visualizer, new SwarmBudget(1, 1, 1)) with { MayAsk = false });

        efforts.Should().Equal(null, CodegenEffort.Medium);
        run.Files.Should().ContainSingle().Which.Name.Should().Be("Unit.cs");
    }

    /// <summary>Passes whatever it is shown.</summary>
    private sealed class AlwaysPasses : IUnitGate
    {
        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(new GateResult(
                new VerificationReport([VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Pass(VerificationRung.Lifecycle)]),
                Compile: null));
    }

    [Theory]
    [InlineData("```css\n/* file: ui/style.css */\nbody{", "color:red}\n```", "```css\n/* file: ui/style.css */\nbody{color:red}\n```")]
    [InlineData("```css\n/* file: ui/style.css */\nbody{", "```css\ncolor:red}\n```", "```css\n/* file: ui/style.css */\nbody{color:red}\n```")]
    [InlineData("intro\n```css\n/* file: ui/style.css */\nbody{", "```css\n/* file: ui/style.css */\nbody{color:red}\n```", "intro\n```css\n/* file: ui/style.css */\nbody{color:red}\n```")]
    public void A_continuation_is_joined_whether_it_carries_on_reopens_or_restarts(string partial, string continuation, string joined)
    {
        SwarmRunner.Stitch(partial, continuation).Should().Be(joined);
    }

    [Fact]
    public void Files_named_by_a_header_are_taken_even_when_nothing_was_fenced()
    {
        // Measured 2026-09-20 on NVIDIA NIM's Nemotron 3 Ultra: both page replies opened straight at their
        // header and ran to a complete file, with no fence anywhere — 24,000 tokens read as "no file".
        const string reply = """
            <!-- file: ui/index.html -->
            <!DOCTYPE html>
            <html><body><div id="app"></div></body></html>

            // file: ui/battlefield.js
            import * as THREE from "three";
            export function mountScene(el) { return { update() {} }; }
            """;

        var files = CodegenCodeExtractor.ExtractUnitFiles(reply);

        files.Select(f => f.Name).Should().Equal("ui/index.html", "ui/battlefield.js");
        files[0].Content.Should().StartWith("<!DOCTYPE html>").And.NotContain("battlefield.js");
        files[1].Content.Should().Contain("mountScene");

        // A module written as plain JavaScript is a page file, not a C# file that happens to look like code.
        const string module = """
            // file: ui/scene.js
            import * as THREE from "three";
            const BULL = 0x22c55e;
            export function mountScene(el) { return { update(state) {}, dispose() {} }; }
            """;

        CodegenCodeExtractor.ExtractUnitFiles(module).Should().ContainSingle()
            .Which.Name.Should().Be("ui/scene.js");
        CodegenCodeExtractor.ExtractFiles(module).Should().BeEmpty("it declares no C#");

        // A fenced reply still wins, and prose alone is still prose.
        CodegenCodeExtractor.ExtractUnitFiles("```html\n<!-- file: ui/index.html -->\n<p>fenced</p>\n```")
            .Should().ContainSingle().Which.Content.Should().Be("<p>fenced</p>");
        CodegenCodeExtractor.ExtractUnitFiles("I will write ui/index.html next, once you confirm the layout.")
            .Should().BeEmpty();
    }

    // ── the critic that looks, and the critic that reads ────────────────────────────────────────

    private static CriticDefinition PictureCritic => BlocksCritics.All.Single(c => c.Id == Critics.Picture);

    private static GauntletSubject Subject(bool withPicture) => new(
        [new StrategyFile("ui/index.html", "<html><body id='app'></body></html>"), new StrategyFile("Unit.cs", "public sealed class Unit { }")],
        withPicture ? new UnitRaster([1, 2, 3], 1, 1, "h") : null,
        "the page",
        new VerificationReport([VerificationStep.Pass(VerificationRung.Compile)]),
        null,
        AuthoringKind.Visualizer);

    [Fact]
    public async Task With_nothing_that_can_see_the_picture_critic_reads_the_page_source()
    {
        PictureCritic.SourceInstruction.Should().Contain("READING THE SOURCE");
        var build = new ByRole(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```", provider: "nvidia", model: "deepseek-ai/deepseek-v4-flash-0731");

        var verdict = await GauntletLoop.For(build, vision: null, "PACK", AuthoringKind.Visualizer, definitions: [PictureCritic])
            .RunAsync(Subject(withPicture: true), ReferenceBar.None);

        verdict.Verdicts.Single().Ran.Should().BeTrue("it read instead of being skipped");
        verdict.Verdicts.Single().Verdict.Should().Contain("read from the source");
        build.Roles.Single().Should().Contain("READING THE SOURCE");
        build.Messages.Single().Should().Contain("ui/index.html");
    }

    [Fact]
    public async Task A_vision_model_that_fails_hands_the_picture_to_the_build_model_to_read()
    {
        var vision = new ByRole(_ => null, provider: "nvidia", model: "meta/llama-3.2-90b-vision-instruct");
        var build = new ByRole(_ => "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```", provider: "nvidia", model: "z-ai/glm-5.3");

        var verdict = await GauntletLoop.For(build, vision, "PACK", AuthoringKind.Visualizer, definitions: [PictureCritic])
            .RunAsync(Subject(withPicture: true), ReferenceBar.None);

        vision.Roles.Should().ContainSingle("the picture went to the model that sees first");
        build.Roles.Single().Should().Contain("READING THE SOURCE");
        verdict.Verdicts.Single().Ran.Should().BeTrue();
    }

    [Fact]
    public async Task A_critic_that_thinks_without_answering_is_asked_once_more_at_a_medium_effort()
    {
        var efforts = new ConcurrentQueue<CodegenEffort?>();
        var critic = new ByRole((_, request) =>
        {
            efforts.Enqueue(request.Effort);
            return request.Effort is null ? null : "```json\n{ \"verdict\": \"fine\", \"findings\": [] }\n```";
        }, provider: "nvidia", model: "deepseek-ai/deepseek-v4-flash-0731", effort: CodegenEffort.High,
           failure: "NVIDIA NIM spent the whole generation reasoning and never started an answer.");

        var logic = BlocksCritics.All.Single(c => c.Id == Critics.MarketLogic);
        var verdict = await new ModelCritic(critic, logic, "PACK", canSeeImages: false).JudgeAsync(Subject(false), ReferenceBar.None);

        efforts.Should().Equal(null, CodegenEffort.Medium);
        verdict.Ran.Should().BeTrue();
    }

    // ── the catalog and the clients ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("nvidia", "meta/llama-3.2-90b-vision-instruct", true)]
    [InlineData("nvidia", "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning", true)]
    [InlineData("nvidia", "google/gemma-4-31b-it", true)]
    [InlineData("nvidia", "deepseek-ai/deepseek-v4-flash-0731", false)]
    [InlineData("opencode", "deepseek-v4-flash-vision-exp", true)]
    [InlineData("nvidia", "google/gemma-3-1b-it", false)]
    public void Vision_is_read_from_the_model_on_a_gateway_that_fronts_many(string provider, string model, bool sees)
    {
        AiModelCatalog.SupportsVision(provider, model).Should().Be(sees);
    }

    [Fact]
    public void Nim_gets_an_output_cap_and_its_measured_models_take_an_effort()
    {
        AiModelCatalog.MaxOutputTokens("nvidia", "z-ai/glm-5.3").Should().Be(131_072);
        AiModelCatalog.MaxOutputTokens("openrouter", "qwen/qwen3.8-27b:free").Should().BeNull("OpenRouter already defaults to the model's maximum");
        AiModelCatalog.SupportsEffort("nvidia", "z-ai/glm-5.3").Should().BeTrue();
        AiModelCatalog.SupportsEffort("nvidia", "mistralai/mistral-large").Should().BeFalse();
    }

    [Fact]
    public async Task The_request_carries_the_cap_and_a_per_call_effort()
    {
        var capture = new Capture();
        using var http = new HttpClient(capture);
        var client = new OpenAiCompatibleCodegenClient(http, "nvidia", "NVIDIA NIM", "https://example.invalid/v1", "z-ai/glm-5.3", "k", effort: CodegenEffort.High);

        await client.GenerateAsync(new StrategyCodegenRequest("ctx", [new CodegenMessage(CodegenRole.User, "hi")]) with { Effort = CodegenEffort.Medium });

        capture.Body.Should().Contain("\"max_tokens\":131072").And.Contain("\"reasoning_effort\":\"medium\"");
    }

    [Fact]
    public async Task A_reply_cut_off_at_the_limit_keeps_what_it_wrote()
    {
        var stream = "data: {\"choices\":[{\"delta\":{\"content\":\"```css\\n/* file: ui/style.css */\\nbody{\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Capture(stream));
        var client = new OpenAiCompatibleCodegenClient(http, "gateway", "Gateway", "https://example.invalid/v1", "m", "k");

        StrategyCodegenResponse? done = null;
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest("ctx", [])))
            if (evt is CodegenEvent.Completed completed) done = completed.Response;

        done!.Success.Should().BeFalse();
        done.Partial.Should().Contain("body{");
    }

    [Fact]
    public void A_model_kept_for_listed_apps_is_not_blamed_on_the_key()
    {
        OpenAiCompatibleCodegenClient.Hint(403,
                """{"error":{"message":"thinkingmachines/inkling:free is only available on agentic harnesses."}}""", "thinkingmachines/inkling:free")
            .Should().Contain("restricted to the provider's own listed apps").And.NotContain("check the API key");
    }

    [Fact]
    public void A_cli_failure_names_its_error_line_rather_than_its_banner()
    {
        const string stderr = """
            OpenAI Codex v0.151.0
            --------
            workdir: C:\scratch
            model: gpt-5.6-terra
            --------
            user
            # Writing a unit …
            ERROR: You've hit your usage limit. Upgrade to Plus to continue using Codex, or try again at Oct 14th, 2026 11:23 PM.
            ERROR: You've hit your usage limit. Upgrade to Plus to continue using Codex, or try again at Oct 14th, 2026 11:23 PM.
            """;

        AgentCliCodegenClient.ErrorLines(stderr).Should().ContainSingle().Which.Should().StartWith("ERROR: You've hit your usage limit");
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers by role; a null answer is a failed call.</summary>
    private sealed class ByRole(
        Func<string, StrategyCodegenRequest, string?> reply,
        string provider = "scripted",
        string model = "m",
        CodegenEffort effort = CodegenEffort.Default,
        string failure = "The provider returned nothing.") : IStrategyCodegenClient
    {
        public ByRole(Func<string, string?> reply, string provider = "scripted", string model = "m")
            : this((role, _) => reply(role), provider, model)
        {
        }

        public ConcurrentQueue<string> Roles { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();

        public string ProviderId => provider;
        public string DisplayName => provider;
        public bool IsAvailable => true;
        public string Model => model;
        public CodegenEffort Effort => effort;

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            Roles.Enqueue(role);
            Messages.Enqueue(request.Messages.Count > 0 ? request.Messages[0].Content : string.Empty);

            return Task.FromResult(reply(role, request) is { } text
                ? StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20))
                : StrategyCodegenResponse.Fail(failure));
        }
    }

    /// <summary>Records the request body and answers with a stream.</summary>
    private sealed class Capture(string? stream = null) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ?? "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"),
            };
        }
    }
}
