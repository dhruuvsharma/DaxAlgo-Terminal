using TradingTerminal.Core.Time;

namespace TradingTerminal.Infrastructure.Time;

/// <summary>Production <see cref="IClock"/> — thin wrapper over <see cref="DateTime.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
