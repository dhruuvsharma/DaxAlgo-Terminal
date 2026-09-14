namespace DaxAlgo.Blocks;

/// <summary>Writes to the terminal's activity log.</summary>
[BlockCard(
    "log",
    "write to the activity log",
    Does = "Adds a line to the terminal's activity log, tagged with the unit's name.",
    Needs = "Nothing.",
    Limits = "Lines over 512 characters are cut. The log is for the user, not for per-tick tracing — use it for decisions and failures.",
    Order = 100)]
public interface ILog
{
    /// <summary>Logs an informational line.</summary>
    void Info(string message);

    /// <summary>Logs a warning.</summary>
    void Warn(string message);

    /// <summary>Logs an error.</summary>
    void Error(string message);
}
