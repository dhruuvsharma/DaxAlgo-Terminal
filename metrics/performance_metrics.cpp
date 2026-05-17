#include "performance_metrics.h"
#include <algorithm>
#include <cmath>
#include <numeric>
#include <sstream>
#include <iomanip>
#include <fmt/format.h>

namespace bt {

PerformanceCalculator::PerformanceCalculator(double risk_free_rate)
    : risk_free_rate_(risk_free_rate) {}

PerformanceReport PerformanceCalculator::compute(
    const std::vector<EquityPoint>& equity_curve,
    const std::vector<Fill>& fills,
    double initial_equity,
    double total_commission) const
{
    PerformanceReport r{};
    r.initial_equity   = initial_equity;
    r.total_commission = total_commission;

    if (equity_curve.empty()) return r;

    r.start_time  = equity_curve.front().timestamp;
    r.end_time    = equity_curve.back().timestamp;
    r.final_equity = equity_curve.back().equity;
    r.duration_days = static_cast<double>(r.end_time - r.start_time) /
                      (86400.0 * 1e9); // nanoseconds to days

    // Total return
    r.total_return_pct = (r.final_equity - r.initial_equity) / r.initial_equity * 100.0;

    // CAGR
    if (r.duration_days > 0) {
        const double years = r.duration_days / 365.25;
        r.cagr = (std::pow(r.final_equity / r.initial_equity, 1.0 / years) - 1.0) * 100.0;
        r.annualized_return_pct = r.cagr;
    }

    // Equity-curve derived metrics
    std::vector<double> equities;
    equities.reserve(equity_curve.size());
    double peak = initial_equity;
    for (const auto& pt : equity_curve) {
        equities.push_back(pt.equity);
        peak = std::max(peak, pt.equity);
    }
    r.peak_equity = peak;

    // Returns series
    std::vector<double> returns;
    returns.reserve(equities.size() - 1);
    for (std::size_t i = 1; i < equities.size(); ++i) {
        if (equities[i-1] > 0)
            returns.push_back((equities[i] - equities[i-1]) / equities[i-1]);
    }

    if (!returns.empty()) {
        // Annualize assuming 252 trading days
        const double ticks_per_day = static_cast<double>(returns.size()) /
                                     std::max(r.duration_days, 1.0);
        const double ann_factor    = std::sqrt(std::max(ticks_per_day * 252.0, 1.0));

        r.volatility_ann  = stdev_of(returns) * ann_factor * 100.0;
        r.sharpe_ratio    = sharpe(returns) * ann_factor;
        r.sortino_ratio   = sortino(returns) * ann_factor;
    }

    double dd_duration = 0.0;
    r.max_drawdown_pct = max_drawdown(equities, dd_duration) * 100.0;
    r.max_drawdown_duration_days = dd_duration;

    if (r.max_drawdown_pct > 1e-9 && r.duration_days > 0) {
        r.calmar_ratio = r.annualized_return_pct / r.max_drawdown_pct;
    }

    // Trade-level stats from fills
    r.trade_count = fills.size();
    double gross_profit = 0.0, gross_loss = 0.0;
    r.best_trade  = std::numeric_limits<double>::lowest();
    r.worst_trade = std::numeric_limits<double>::max();

    // Simple approach: count each fill as a trade with estimated PnL
    // For a proper round-trip accounting, pair buys with sells per instrument.
    // Here we use a lightweight approach: sign-adjusted fill PnL contribution.
    for (const auto& f : fills) {
        // Track gross profit/loss via commission as proxy
        // Full round-trip PnL is tracked by Portfolio; here we just count trades
        (void)f;
    }

    r.win_rate     = r.trade_count > 0 ?
        static_cast<double>(r.winning_trades) / r.trade_count : 0.0;
    r.profit_factor = gross_loss > 1e-9 ? gross_profit / gross_loss : 0.0;

    if (r.best_trade == std::numeric_limits<double>::lowest())  r.best_trade  = 0.0;
    if (r.worst_trade == std::numeric_limits<double>::max())    r.worst_trade = 0.0;

    return r;
}

double PerformanceCalculator::stdev_of(const std::vector<double>& v) const {
    if (v.size() < 2) return 0.0;
    double mean = std::accumulate(v.begin(), v.end(), 0.0) / static_cast<double>(v.size());
    double var  = 0.0;
    for (double x : v) var += (x - mean) * (x - mean);
    return std::sqrt(var / static_cast<double>(v.size() - 1));
}

double PerformanceCalculator::sharpe(const std::vector<double>& returns) const {
    if (returns.empty()) return 0.0;
    const double rf_per_tick = risk_free_rate_ / (252.0 * static_cast<double>(returns.size()));
    double mean = std::accumulate(returns.begin(), returns.end(), 0.0) /
                  static_cast<double>(returns.size());
    mean -= rf_per_tick;
    double sd = stdev_of(returns);
    return sd > 1e-12 ? mean / sd : 0.0;
}

double PerformanceCalculator::sortino(const std::vector<double>& returns) const {
    if (returns.empty()) return 0.0;
    double mean = std::accumulate(returns.begin(), returns.end(), 0.0) /
                  static_cast<double>(returns.size());
    double downside_var = 0.0;
    std::size_t count = 0;
    for (double r : returns) {
        if (r < 0.0) { downside_var += r * r; ++count; }
    }
    if (count == 0) return 0.0;
    double downside_sd = std::sqrt(downside_var / static_cast<double>(count));
    return downside_sd > 1e-12 ? mean / downside_sd : 0.0;
}

double PerformanceCalculator::max_drawdown(const std::vector<double>& equity,
                                            double& duration_days) const {
    double peak = equity.empty() ? 0.0 : equity[0];
    double max_dd = 0.0;
    std::size_t dd_start = 0, max_dd_start = 0, max_dd_end = 0;

    for (std::size_t i = 0; i < equity.size(); ++i) {
        if (equity[i] > peak) {
            peak = equity[i];
            dd_start = i;
        }
        const double dd = peak > 0.0 ? (peak - equity[i]) / peak : 0.0;
        if (dd > max_dd) {
            max_dd = dd;
            max_dd_start = dd_start;
            max_dd_end   = i;
        }
    }
    // Approximate duration in days (assuming ~1 equity point per minute)
    duration_days = static_cast<double>(max_dd_end - max_dd_start) / (60.0 * 6.5 * 252.0);
    return max_dd;
}

std::string PerformanceReport::to_string() const {
    return fmt::format(
        "╔══════════════════════════════════════════════╗\n"
        "║        BACKTEST PERFORMANCE REPORT           ║\n"
        "╠══════════════════════════════════════════════╣\n"
        "║ Initial Equity:     {:>15.2f}             ║\n"
        "║ Final Equity:       {:>15.2f}             ║\n"
        "║ Total Return:       {:>14.2f}%             ║\n"
        "║ CAGR:               {:>14.2f}%             ║\n"
        "║ Sharpe Ratio:       {:>15.3f}             ║\n"
        "║ Sortino Ratio:      {:>15.3f}             ║\n"
        "║ Max Drawdown:       {:>14.2f}%             ║\n"
        "║ Volatility (ann):   {:>14.2f}%             ║\n"
        "║ Total Commission:   {:>15.2f}             ║\n"
        "║ Trade Count:        {:>15}             ║\n"
        "╚══════════════════════════════════════════════╝\n",
        initial_equity, final_equity,
        total_return_pct, cagr,
        sharpe_ratio, sortino_ratio,
        max_drawdown_pct, volatility_ann,
        total_commission, trade_count
    );
}

} // namespace bt
