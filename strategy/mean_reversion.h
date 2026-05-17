#pragma once

#include "strategy.h"

namespace bt {

// Tick-level mean reversion strategy:
// - Computes a rolling mean and standard deviation of mid prices.
// - Enters long when price is > entry_z_score std devs below mean.
// - Enters short when price is > entry_z_score std devs above mean.
// - Exits when price reverts within exit_z_score std devs of mean.

struct MeanReversionParams {
    std::size_t lookback     = 50;      // ticks to compute mean/std
    double      entry_z      = 2.0;     // z-score threshold to enter
    double      exit_z       = 0.5;     // z-score threshold to exit
    Quantity    trade_qty    = 100.0;
    bool        allow_short  = true;
    InstrumentId instrument  = 1;
};

class MeanReversionStrategy : public Strategy {
public:
    explicit MeanReversionStrategy(MeanReversionParams params = {});

    void on_start(StrategyContext& ctx) override;
    void on_tick(const Tick& tick, StrategyContext& ctx) override;
    void on_order_fill(const Fill& fill, StrategyContext& ctx) override;
    void on_finish(StrategyContext& ctx) override;

    [[nodiscard]] std::uint64_t signal_count() const noexcept { return signal_count_; }

private:
    MeanReversionParams params_;
    OrderId    active_order_{INVALID_ORDER_ID};
    bool       in_position_{false};
    Side       position_side_{Side::Buy};
    std::uint64_t signal_count_{0};
};

} // namespace bt
