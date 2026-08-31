namespace DaxAlgo.Sdk;

/// <summary>
/// A verb a unit offers: a named thing the viewer can ask it to do, rendered by the host as a button
/// beside the parameters.
///
/// <para><b>Why this exists.</b> A unit could declare a parameter and nothing else, so everything the
/// hand-written windows do on a button — reset the profile, clear the tape, re-centre, snapshot the
/// levels — had no expression at all. A parameter is a value you set; some things are not values.
/// Bending them into one produces a toggle the user has to flip twice to mean "now", which reads as a
/// setting and behaves as a command.</para>
///
/// <para><b>It is data; the running of it is <c>OnActionAsync</c>.</b> Nothing here is executable —
/// see that method for why, and for the threading rule that follows from it.</para>
///
/// <para><b>An action cannot reach outside the unit.</b> The sandbox denies file and network access to
/// authored code, so "export this as CSV" is not writable as one: a unit can compute what to export
/// and cannot save it.</para>
/// </summary>
/// <param name="Id">
/// Stable identifier passed back to <c>OnActionAsync</c>. Not shown. Keep it constant across versions:
/// it is what the unit switches on, and renaming it silently stops the button working.
/// </param>
/// <param name="Label">
/// What the button says. A verb, and a short one — "Reset profile", not "Reset the volume profile
/// accumulated so far". The explanation belongs in <paramref name="Detail"/>.
/// </param>
/// <param name="Detail">One line of tooltip, or null. Say what will happen, not what the button is.</param>
public readonly record struct UnitAction(string Id, string Label, string? Detail = null)
{
    /// <summary>
    /// The most actions a unit may offer.
    ///
    /// <para>The list is untrusted input like the layout tree, and a strip of forty buttons is not a
    /// window anyone can use. Over the limit is refused whole rather than truncated, for the reason the
    /// layout bound already gives: half a set of controls, silently, is worse than a set that visibly
    /// did not apply.</para>
    /// </summary>
    public const int Maximum = 8;

    /// <summary>True when this action is well-formed enough to render.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Label);

    /// <summary>
    /// The declared list, or empty when it is malformed — over the limit, missing an id or a label, or
    /// naming one id twice.
    ///
    /// <para>A duplicate id is rejected rather than resolved: two buttons that both mean the same call
    /// is a unit whose author has lost track of which is which, and picking one for them hides it.</para>
    /// </summary>
    public static IReadOnlyList<UnitAction> Sanitise(IReadOnlyList<UnitAction>? declared)
    {
        if (declared is null || declared.Count == 0) return [];
        if (declared.Count > Maximum) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in declared)
        {
            if (!action.IsValid || !seen.Add(action.Id)) return [];
        }

        return declared;
    }
}
