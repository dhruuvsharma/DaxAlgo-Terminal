namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Running statistics of an equity curve: drawdown as it happens, and the risk-adjusted return so
/// far.
///
/// <para>This is the panel a trader actually looks at, and the reason it belongs in the SDK rather
/// than in each strategy is that the definitions are where the disagreements are. Sortino divides by
/// the deviation of the <b>downside only</b>; a Sortino computed against total deviation is a Sharpe
/// with a different name and a better-looking number. Drawdown is measured from the running peak,
/// not from the start, so a strategy up thirty percent and then down ten reports the ten.</para>
///
/// <para>Feed it equity, on whatever cadence the strategy marks at — per bar is usual. The ratios are
/// per-sample; <see cref="AnnualizedSharpe"/> scales them once the caller says what a sample is.</para>
/// </summary>
public sealed class EquityStats
{
    private readonly Welford _returns = new();
    private readonly Welford _downside = new();

    private double _previous;
    private bool _seeded;

    /// <summary>The most recent equity mark.</summary>
    public double Equity { get; private set; }

    /// <summary>The highest equity seen.</summary>
    public double Peak { get; private set; }

    /// <summary>How far below the peak equity is now, as a fraction in [0, 1].</summary>
    public double Drawdown => Num.Clamp(Num.SafeDiv(Peak - Equity, Peak), 0d, 1d);

    /// <summary>The worst drawdown seen, as a fraction. The number that decides whether a curve was
    /// survivable, which the total return never says on its own.</summary>
    public double MaximumDrawdown { get; private set; }

    /// <summary>How many marks have been recorded.</summary>
    public long Count => _returns.Count;

    /// <summary>True from two marks, which is the first point at which a return exists.</summary>
    public bool IsReady => _returns.IsReady;

    /// <summary>Mean return per sample.</summary>
    public double MeanReturn => _returns.Mean;

    /// <summary>Standard deviation of return per sample.</summary>
    public double Volatility => _returns.StandardDeviation;

    /// <summary>Mean over standard deviation, per sample. Excess of a risk-free rate is not
    /// subtracted: at intraday cadence it is noise, and pretending otherwise invents precision.</summary>
    public double Sharpe => Num.SafeDiv(MeanReturn, Volatility);

    /// <summary>Mean return over the deviation of the losing samples only.</summary>
    public double Sortino => Num.SafeDiv(MeanReturn, _downside.StandardDeviation);

    /// <summary>Mean return over the worst drawdown — return per unit of the pain it took.</summary>
    public double Calmar => Num.SafeDiv(MeanReturn, MaximumDrawdown);

    /// <summary>The Sharpe ratio scaled by the square root of <paramref name="samplesPerYear"/>.</summary>
    public double AnnualizedSharpe(double samplesPerYear) =>
        samplesPerYear > 0d ? Sharpe * Math.Sqrt(samplesPerYear) : Sharpe;

    /// <summary>Records one equity mark.</summary>
    public void Update(double equity)
    {
        if (!double.IsFinite(equity)) return;

        Equity = equity;

        if (!_seeded)
        {
            _previous = equity;
            Peak = equity;
            _seeded = true;
            return;
        }

        // A simple return, not a log return: these are aggregated as an arithmetic mean and compared
        // against a drawdown, both of which are defined on simple returns.
        var change = Num.SafeDiv(equity - _previous, Math.Abs(_previous));
        _previous = equity;

        _returns.Update(change);
        if (change < 0d) _downside.Update(change);

        if (equity > Peak) Peak = equity;
        if (Drawdown > MaximumDrawdown) MaximumDrawdown = Drawdown;
    }

    /// <summary>Starts a new curve.</summary>
    public void Reset()
    {
        _returns.Reset();
        _downside.Reset();
        _previous = 0d;
        _seeded = false;
        Equity = 0d;
        Peak = 0d;
        MaximumDrawdown = 0d;
    }
}

/// <summary>
/// Per-trade statistics: how often the strategy is right, and what it makes when it is.
///
/// <para>Kept apart from <see cref="EquityStats"/> because they answer different questions and are
/// routinely confused. A curve can be excellent with a hit rate under a third, and a hit rate over
/// ninety percent is the signature of a strategy whose losses are the ones nobody counted.
/// <see cref="ProfitFactor"/> and <see cref="Expectancy"/> are the pair worth reading together: the
/// first says whether the edge exists, the second what one trade of it is worth.</para>
/// </summary>
public sealed class TradeStats
{
    private double _grossProfit;
    private double _grossLoss;

    /// <summary>Closed trades recorded.</summary>
    public int Count { get; private set; }

    /// <summary>Trades that made money.</summary>
    public int Wins { get; private set; }

    /// <summary>Trades that lost money. Scratches count as neither.</summary>
    public int Losses { get; private set; }

    /// <summary>Fraction of trades that made money, in [0, 1].</summary>
    public double HitRate => Num.SafeDiv(Wins, Count);

    /// <summary>Sum of the winners.</summary>
    public double GrossProfit => _grossProfit;

    /// <summary>Sum of the losers, as a positive number.</summary>
    public double GrossLoss => _grossLoss;

    /// <summary>Net profit and loss.</summary>
    public double NetProfit => _grossProfit - _grossLoss;

    /// <summary>Gross profit over gross loss. Above one is an edge; below one is a hobby.</summary>
    public double ProfitFactor => Num.SafeDiv(_grossProfit, _grossLoss);

    /// <summary>Average profit and loss per trade — the edge in the units it is actually collected in.</summary>
    public double Expectancy => Num.SafeDiv(NetProfit, Count);

    /// <summary>Average winner.</summary>
    public double AverageWin => Num.SafeDiv(_grossProfit, Wins);

    /// <summary>Average loser, as a positive number.</summary>
    public double AverageLoss => Num.SafeDiv(_grossLoss, Losses);

    /// <summary>Average winner over average loser — the payoff the hit rate has to clear.</summary>
    public double PayoffRatio => Num.SafeDiv(AverageWin, AverageLoss);

    /// <summary>Consecutive losers, ending now. What a risk cut-off is usually written against.</summary>
    public int LosingStreak { get; private set; }

    /// <summary>The longest run of losers seen.</summary>
    public int WorstLosingStreak { get; private set; }

    /// <summary>Records one closed trade's profit and loss.</summary>
    public void Record(double profitAndLoss)
    {
        if (!double.IsFinite(profitAndLoss)) return;

        Count++;

        if (profitAndLoss > 0d)
        {
            Wins++;
            _grossProfit += profitAndLoss;
            LosingStreak = 0;
            return;
        }

        if (profitAndLoss < 0d)
        {
            Losses++;
            _grossLoss += -profitAndLoss;
            LosingStreak++;
            if (LosingStreak > WorstLosingStreak) WorstLosingStreak = LosingStreak;
        }

        // A scratch is neither a win nor a loss, and it does not break a losing streak either — a
        // flat trade between two losers has not proved anything about the run.
    }

    /// <summary>Starts a new record.</summary>
    public void Reset()
    {
        Count = 0;
        Wins = 0;
        Losses = 0;
        _grossProfit = 0d;
        _grossLoss = 0d;
        LosingStreak = 0;
        WorstLosingStreak = 0;
    }
}
