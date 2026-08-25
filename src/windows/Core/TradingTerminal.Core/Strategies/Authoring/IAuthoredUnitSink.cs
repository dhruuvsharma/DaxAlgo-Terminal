namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Where a freshly compiled unit goes so a user can open it.
///
/// <para>Registration is what turns a verified strategy into a delivered one. Everything upstream —
/// compile, policy scan, the verification rungs, the live preview — was equally true of a unit nobody
/// could open, which is the state authored units were in until this existed.</para>
///
/// <para>It is a seam rather than a direct call because the registries live in the shell, alongside the
/// windows that host what they hold, and the authoring pane sits below that. The shell implements this;
/// the pane calls it and learns nothing about how a unit is hosted.</para>
///
/// <para>Optional by design. An edition with no implementation still compiles, verifies and previews —
/// it reports that it cannot put the result in a catalog, rather than failing at construction.</para>
/// </summary>
public interface IAuthoredUnitSink
{
    /// <summary>
    /// Registers <paramref name="unit"/> under <paramref name="id"/>, replacing any existing entry with
    /// that id — regenerating a strategy should update its card, not add a second one.
    /// </summary>
    /// <returns>What to tell the user. Never throws: the code compiled and was verified, so a failure
    /// here is worth reporting but must not cost the session.</returns>
    string Register(AuthoredUnit unit, string id, string? displayName);
}
