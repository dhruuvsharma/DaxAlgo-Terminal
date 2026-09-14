namespace DaxAlgo.Blocks;

/// <summary>Hands text to the user to save.</summary>
[BlockCard(
    "export",
    "let the user save text or CSV",
    Does = "Offers text (CSV, JSON, a report) to the user, who chooses whether and where to save it.",
    Needs = "Nothing.",
    Limits = "Text up to 262,144 characters, label up to 64. Returns false when the host cannot offer it right now.",
    Order = 120)]
public interface IExport
{
    /// <summary>Offers <paramref name="text"/> to the user under <paramref name="label"/>; false when it cannot be offered.</summary>
    bool Offer(string label, string text);
}
