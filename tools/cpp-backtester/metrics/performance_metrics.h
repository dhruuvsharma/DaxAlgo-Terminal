#pragma once

#include "../include/types.h"
#include "../core/portfolio.h"
#include <vector>
#include <string>

namespace bt {

struct PerformanceReport {
    // Returns
    double total_return_pct;
    double annualized_return_pct;
    double cagr;

    // Risk
    double volatility_ann;
    double sharpe_ratio;
    double sortino_ratio;
    double calmar_ratio;
    double max_drawdown_pct;
    double max_drawdown_duration_days;

    // Trade stats
    std::uint64_t trade_count;
    std::uint64_t winning_trades;
    std::uint64_t losing_trades;
    double        win_rate;
    double        avg_win;
    double        avg_loss;
    double        profit_factor;
    double        avg_trade_pnl;
    double        best_trade;
    double        worst_trade;

    // Portfolio
    double initial_equity;
    double final_equity;
    double peak_equity;
    double total_commission;

    // Timing
    Timestamp start_time;
    Timestamp end_time;
    double    duration_days;

    std::string to_string() const;
};

class PerformanceCalculator {
public:
    explicit PerformanceCalculator(double risk_free_rate = 0.05);

    // Compute full performance report from equity curve and fills
    PerformanceReport compute(const std::vector<EquityPoint>& equity_curve,
                              const std::vector<Fill>& fills,
                              double initial_equity,
                              double total_commission) const;

    // Helper exposed for tests
    double stdev_of(const std::vector<double>& v) const;

private:
    double sharpe(const std::vector<double>& returns) const;
    double sortino(const std::vector<double>& returns) const;
    double max_drawdown(const std::vector<double>& equity,
                        double& duration_days) const;

    double risk_free_rate_;  // annualized
};

} // namespace bt
