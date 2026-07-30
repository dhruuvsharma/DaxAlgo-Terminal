using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Core.Accounts;

internal static class AccountContractGuards
{
    public static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static string RequireOpaque(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static DateTimeOffset AsUtc(DateTimeOffset value) => value.ToUniversalTime();

    public static void ValidateEdition(AppEdition edition, string parameterName)
    {
        if (!Enum.IsDefined(typeof(AppEdition), edition))
            throw new ArgumentOutOfRangeException(parameterName, edition, "Unknown product edition.");
    }
}
