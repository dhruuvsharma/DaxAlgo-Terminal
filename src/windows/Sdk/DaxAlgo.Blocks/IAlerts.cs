namespace DaxAlgo.Blocks;

/// <summary>Tells the user something happened.</summary>
[BlockCard(
    "alerts",
    "notify the user",
    Does = "Shows an alert to the user and records it in the activity log.",
    Needs = "Nothing.",
    Limits = "Messages over 512 characters are cut. Alerts with the same dedupeKey shown close together are collapsed, so pass one for anything that can fire repeatedly.",
    Types = [typeof(AlertSeverity)],
    Order = 90)]
public interface IAlerts
{
    /// <summary>Raises an alert; repeats with the same <paramref name="dedupeKey"/> are collapsed.</summary>
    void Raise(string message, AlertSeverity severity = AlertSeverity.Info, string? dedupeKey = null);
}

/// <summary>How loudly an alert is shown.</summary>
public enum AlertSeverity
{
    /// <summary>Worth knowing.</summary>
    Info,

    /// <summary>Worth looking at.</summary>
    Warning,

    /// <summary>Needs attention now.</summary>
    Critical,
}
