using System.IO;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Polyglot;

/// <summary>Builds <see cref="StrategyKernelDescriptor"/>s for Python-authored strategies and
/// discovers them from a folder, so a researcher drops a <c>.py</c> file (using the daxalgo_bt SDK)
/// and it appears in the Studio catalog. Discovered strategies use an empty schema for now (they run
/// with their in-script defaults); a per-strategy parameter manifest is a later refinement.</summary>
public static class PythonStrategyDescriptors
{
    public static StrategyKernelDescriptor For(
        string id, string name, string scriptPath, StrategyParameterSchema schema, string pythonExe = "python") =>
        new(id, name, $"Python strategy: {Path.GetFileName(scriptPath)}", schema,
            () => new PythonStrategyKernel(pythonExe, scriptPath));

    public static IEnumerable<StrategyKernelDescriptor> Discover(string folder, string pythonExe = "python")
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(folder, "*.py"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            yield return For($"py:{name}", $"Python: {name}", file, StrategyParameterSchema.Empty, pythonExe);
        }
    }
}
