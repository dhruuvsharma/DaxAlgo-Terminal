using System.Net.Http;
using DaxAlgo.StrategyTool;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;

// daxalgo strategy <action> [options]. A thin dev CLI over the template + dotnet + the packaging
// script, plus an AI build loop that reuses the lean DaxAlgo.Codegen assembly.
if (args.Length < 2 || !args[0].Equals("strategy", StringComparison.OrdinalIgnoreCase))
{
    PrintUsage();
    return args.Length == 0 ? 0 : 1;
}

var action = args[1].ToLowerInvariant();
var opts = ParseOptions(args.Skip(2));

try
{
    return action switch
    {
        "new" => New(opts),
        "build" => Wrap("build", opts),
        "test" => Wrap("test", opts),
        "package" => Package(opts),
        "install" => Install(opts),
        "ai" => await AiAsync(opts),
        _ => Unknown(action),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ── commands ────────────────────────────────────────────────────────────────────────────────────

int New(Dictionary<string, string> o)
{
    var name = Required(o, "name");
    var output = o.GetValueOrDefault("output", name);
    var ui = o.ContainsKey("ui") ? " --ui" : string.Empty;
    return ProcessRunner.Run("dotnet", $"new daxalgo-strategy -n {name} -o \"{output}\"{ui}");
}

// build / test: run dotnet on the scaffold's solution.
int Wrap(string verb, Dictionary<string, string> o)
{
    var project = ResolveSolution(o);
    return ProcessRunner.Run("dotnet", $"{verb} \"{project}\"");
}

int Package(Dictionary<string, string> o)
{
    // Absolute path: the script runs with its own folder as the working dir, so a relative path would
    // resolve doubled (dir/dir/pack-plugin.ps1).
    var dir = Path.GetFullPath(o.GetValueOrDefault("project", "."));
    var script = Path.Combine(dir, "pack-plugin.ps1");
    if (!File.Exists(script)) { Console.Error.WriteLine($"pack-plugin.ps1 not found in '{dir}'."); return 1; }
    return ProcessRunner.Run(ProcessRunner.PowerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", dir);
}

// install: build, then copy the plugin files into a host shell's plugins folder.
int Install(Dictionary<string, string> o)
{
    var dir = o.GetValueOrDefault("project", ".");
    var into = Required(o, "into");
    var name = o.GetValueOrDefault("name", new DirectoryInfo(Path.GetFullPath(dir)).Name);

    if (Wrap("build", o) != 0) return 1;

    var bin = Path.Combine(dir, name, "bin", o.GetValueOrDefault("configuration", "Release"), "net9.0-windows7.0");
    if (!Directory.Exists(bin)) { Console.Error.WriteLine($"build output not found at '{bin}'."); return 1; }

    var target = Path.Combine(into, name);
    Directory.CreateDirectory(target);
    foreach (var file in new[] { $"{name}.dll", "plugin.json", $"{name}.pdb", $"{name}.deps.json" })
    {
        var src = Path.Combine(bin, file);
        if (File.Exists(src)) File.Copy(src, Path.Combine(target, file), overwrite: true);
    }
    Console.WriteLine($"Installed '{name}' -> {target}. Restart the terminal to load it.");
    return 0;
}

// ai: scaffold if needed, then drive the build loop. --provider fake keeps the scaffold's (valid)
// kernel so CI proves the scaffold->build->test->package plumbing without a network.
async Task<int> AiAsync(Dictionary<string, string> o)
{
    var name = Required(o, "name");
    var output = o.GetValueOrDefault("output", name);
    var providerId = o.GetValueOrDefault("provider", "fake");
    var prompt = o.GetValueOrDefault("prompt", $"a simple, sensible starting strategy called {name}");

    if (!Directory.Exists(output))
    {
        var ui = o.ContainsKey("ui") ? " --ui" : string.Empty;
        if (ProcessRunner.Run("dotnet", $"new daxalgo-strategy -n {name} -o \"{output}\"{ui}") != 0) return 1;
    }

    var kernelPath = Path.Combine(output, name, "Engine", $"{name}Kernel.cs");

    if (providerId.Equals("fake", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Provider 'fake': using the scaffolded kernel unchanged (CI/plumbing check).");
    }
    else
    {
        var client = ResolveProvider(providerId);
        if (client is null || !client.IsAvailable)
        {
            Console.Error.WriteLine($"Provider '{providerId}' is not available (missing key/CLI). Set its API key env var, or use --provider fake.");
            return 1;
        }

        var pack = StrategyContextPack.Load().SystemPrompt;
        var instruction =
            $"Write the complete kernel file for {kernelPath} — namespace {name}.Engine, a public class " +
            $"{name}Kernel implementing IBacktestStrategy with a public ({{Contract}}) constructor, plus the " +
            $"usings it needs. {prompt}. Return only the file, in a ```csharp fence.";
        if (!await GenerateKernelAsync(client, pack, instruction, kernelPath, MaxAttempts(o), output, name)) return 1;
    }

    if (ProcessRunner.Run("dotnet", $"build \"{ResolveSolution(o, output)}\"") != 0) return 1;
    if (ProcessRunner.Run("dotnet", $"test \"{ResolveSolution(o, output)}\" --no-build") != 0) return 1;
    return Package(new Dictionary<string, string> { ["project"] = output });
}

// The file-based generate/build/fix loop for real providers.
async Task<bool> GenerateKernelAsync(
    IStrategyCodegenClient client, string pack, string instruction, string kernelPath, int maxAttempts, string projectDir, string name)
{
    var messages = new List<CodegenMessage> { new(CodegenRole.User, instruction) };
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        Console.WriteLine($"[ai] generating kernel (attempt {attempt}/{maxAttempts}) via {client.DisplayName}…");
        var response = await client.GenerateAsync(new StrategyCodegenRequest(pack, messages));
        if (!response.Success || string.IsNullOrWhiteSpace(response.Code))
        {
            Console.Error.WriteLine($"[ai] provider failed: {response.Error}");
            return false;
        }

        File.WriteAllText(kernelPath, response.Code);
        messages.Add(new CodegenMessage(CodegenRole.Assistant, response.RawText ?? response.Code));

        var (exit, output) = ProcessRunner.Capture("dotnet", $"build \"{ResolveSolution([], projectDir)}\"");
        if (exit == 0) { Console.WriteLine($"[ai] compiled on attempt {attempt}."); return true; }

        Console.WriteLine("[ai] build failed — feeding errors back.");
        var errors = string.Join('\n', output.Split('\n').Where(l => l.Contains(": error")).Take(20));
        messages.Add(new CodegenMessage(CodegenRole.User, $"The build failed. Fix these errors and return the COMPLETE file:\n{errors}"));
    }
    Console.Error.WriteLine($"[ai] could not produce compiling code in {maxAttempts} attempts.");
    return false;
}

// ── helpers ─────────────────────────────────────────────────────────────────────────────────────

IStrategyCodegenClient? ResolveProvider(string providerId)
{
    // Config from env: AiCodegen providers use the app defaults; keys come from {PROVIDER}_API_KEY.
    var options = DefaultOptions();
    string? Key(string id) => Environment.GetEnvironmentVariable($"{id.ToUpperInvariant()}_API_KEY");
    var factory = new StrategyCodegenClientFactory(() => new HttpClient(), options, Key);
    return factory.BuildAll().FirstOrDefault(c => c.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
}

static AiCodegenOptions DefaultOptions() => new()
{
    Providers =
    {
        ["openai"] = new() { BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
        ["deepseek"] = new() { BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat" },
        ["xai"] = new() { BaseUrl = "https://api.x.ai/v1", Model = "grok-2-latest" },
        ["openrouter"] = new() { BaseUrl = "https://openrouter.ai/api/v1", Model = "anthropic/claude-3.5-sonnet" },
        ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", Model = "llama3.1" },
        ["anthropic"] = new() { BaseUrl = "https://api.anthropic.com", Model = "claude-3-5-sonnet-latest", Kind = AiCodegenProviderKind.Anthropic },
    },
};

static int MaxAttempts(Dictionary<string, string> o) =>
    int.TryParse(o.GetValueOrDefault("max-attempts"), out var n) && n > 0 ? n : 4;

// The scaffold's solution: <dir>/<Name>.slnx.
static string ResolveSolution(Dictionary<string, string> o, string? dir = null)
{
    dir ??= o.GetValueOrDefault("project", ".");
    var sln = Directory.EnumerateFiles(dir, "*.slnx").FirstOrDefault();
    return sln ?? throw new InvalidOperationException($"no .slnx found in '{dir}' — is it a scaffolded plugin?");
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> tokens)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var list = tokens.ToList();
    for (var i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--")) continue;
        var key = list[i][2..];
        var value = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : "true";
        result[key] = value;
    }
    return result;
}

static string Required(Dictionary<string, string> o, string key) =>
    o.TryGetValue(key, out var v) && v != "true" ? v : throw new InvalidOperationException($"--{key} is required.");

int Unknown(string a) { Console.Error.WriteLine($"unknown action '{a}'."); PrintUsage(); return 1; }

void PrintUsage() => Console.WriteLine(
    """
    daxalgo strategy <action> [options]

      new      --name <N> [--ui] [--output <dir>]         scaffold a plugin
      build    [--project <dir>]                          dotnet build the scaffold
      test     [--project <dir>]                          dotnet test the scaffold
      package  [--project <dir>]                          build a .daxplugin
      install  --into <plugins-dir> [--project <dir>]     build + copy into a shell's plugins folder
      ai       --name <N> [--provider <id>] [--prompt "…"] [--ui] [--output <dir>] [--max-attempts <n>]
                                                          scaffold + AI-write the kernel + build/test/package
                                                          (--provider fake keeps the scaffold kernel; for CI)

    AI providers: fake (default) · claude-cli · codex-cli · openai · deepseek · xai · openrouter · ollama · anthropic
    Keys come from {PROVIDER}_API_KEY env vars; agent CLIs use their own login.
    """);
