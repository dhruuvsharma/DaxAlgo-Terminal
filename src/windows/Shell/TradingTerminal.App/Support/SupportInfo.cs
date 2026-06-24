using System.Reflection;

namespace TradingTerminal.App.Support;

/// <summary>
/// Single source of truth for the "support the developer" surface — developer contact, product
/// name, and the running version (read from the entry assembly's informational version, falling
/// back to the assembly version, then a hard-coded default). No secrets: the only contact is a
/// public mailto: address, safe to ship in a public repo.
/// </summary>
internal static class SupportInfo
{
    /// <summary>The developer's inbox. Feedback is delivered via a <c>mailto:</c> the user's own
    /// mail client sends — there is no server-side send and no stored credential.</summary>
    public const string DeveloperEmail = "dhruvsha.info@gmail.com";

    public const string ProductName = "DaxAlgo Terminal";

    public const string GitHubUrl = "https://github.com/dhruuvsharma/DaxAlgo-Terminal";

    /// <summary>Version string for display ("v1.0.0"). Prefers the informational version (set in
    /// Directory.Build.props), then the assembly version, then a literal fallback.</summary>
    public static string DisplayVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip the "+<git sha>" build-metadata suffix the SDK appends, if present.
                var plus = info.IndexOf('+');
                return "v" + (plus >= 0 ? info[..plus] : info);
            }

            var ver = asm.GetName().Version;
            return ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";
        }
    }
}
