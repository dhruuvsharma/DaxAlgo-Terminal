using System.IO;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Where locally-authored units are installed so they come back after a restart — a root of its own,
/// separate from the marketplace plugins folder.
///
/// <para>Separate because the two hold different things under different rules. The plugins root is for
/// packages from other people, gated by publisher trust; this holds units the user authored on this
/// machine and reviewed themselves, loaded under the strict, non-relaxable sandbox scan profile. Mixing
/// them would mean one policy has to cover both, and whichever policy won would be wrong for the other
/// half.</para>
///
/// <para>Under the user's profile rather than beside the application, because an installed build lives
/// somewhere unwritable and a strategy the user wrote is theirs, not the installation's — it should
/// outlive a reinstall.</para>
/// </summary>
public static class AuthoredUnitsRoot
{
    /// <summary>The installed-units folder: <c>%LocalAppData%\DaxAlgo Terminal\units</c>. Each unit gets
    /// a subfolder holding its assembly, its <c>plugin.json</c> and its source, which is the layout the
    /// plugin loader already expects.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "units");

    /// <summary>Creates the folder if it is missing and returns it, or returns null when it cannot be
    /// created. Null is a real answer: a locked-down profile means no persistence, and the caller says
    /// so rather than failing to start.</summary>
    public static string? Ensure()
    {
        try
        {
            Directory.CreateDirectory(Path);
            return Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
