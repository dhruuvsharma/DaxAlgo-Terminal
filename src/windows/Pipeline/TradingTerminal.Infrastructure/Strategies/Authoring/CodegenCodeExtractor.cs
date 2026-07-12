using System.Text.RegularExpressions;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>Pulls the C# out of a model reply. Models usually wrap code in a <c>```csharp … ```</c>
/// fence, sometimes with prose around it; occasionally they return bare code. This takes the first
/// fenced block if there is one, otherwise the whole trimmed text.</summary>
public static partial class CodegenCodeExtractor
{
    [GeneratedRegex(@"```(?:csharp|cs|c#)?\s*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedBlock();

    public static string Extract(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

        var match = FencedBlock().Match(reply);
        return (match.Success ? match.Groups[1].Value : reply).Trim();
    }
}
