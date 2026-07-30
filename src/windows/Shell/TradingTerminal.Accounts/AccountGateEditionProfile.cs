using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

internal sealed record AccountGateEditionProfile(
    AppEdition Edition,
    string PlanName,
    string Price,
    string Summary)
{
    public static AccountGateEditionProfile For(AppEdition edition) => edition switch
    {
        AppEdition.Basic => new(
            AppEdition.Basic,
            "Basic",
            "Free",
            "Core market data, charts, tools, and keyless broker connections."),
        AppEdition.Professional => new(
            AppEdition.Professional,
            "Professional",
            "$79 / month",
            "The complete terminal, including Professional research and experimental surfaces."),
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unknown product edition."),
    };
}
