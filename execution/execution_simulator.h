#pragma once

#include "../include/types.h"
#include "../core/order_manager.h"
#include <functional>
#include <vector>

namespace bt {

struct SlippageModel {
    double fixed_bps        = 0.5;   // fixed slippage in basis points
    double market_impact_k  = 0.1;   // linear market impact coefficient
    double volatility_mult  = 0.0;   // vol-adjusted slippage (optional)

    Price compute(Price base_price, Quantity qty, Side side,
                  double volatility = 0.0) const noexcept {
        const double sign = (side == Side::Buy) ? 1.0 : -1.0;
        const double fixed_slip = base_price * fixed_bps * 0.0001;
        const double impact     = market_impact_k * qty * base_price * 0.0001;
        const double vol_slip   = volatility * volatility_mult * base_price;
        return base_price + sign * (fixed_slip + impact + vol_slip);
    }
};

struct LatencyModel {
    std::int64_t base_ns         = 50'000;   // 50 microseconds base
    std::int64_t jitter_ns       = 10'000;   // up to 10us jitter
    bool         enabled         = true;

    std::int64_t sample() const noexcept {
        if (!enabled) return 0;
        // Deterministic pseudo-random jitter (no overhead of RNG state)
        return base_ns + (jitter_ns / 2);
    }
};

// ── Execution Simulator ───────────────────────────────────────────────────
// Simulates realistic order execution given market conditions.
// Plugged into the event loop — called on every tick to check limit orders.

class ExecutionSimulator {
public:
    explicit ExecutionSimulator(OrderManager& oms,
                                SlippageModel slippage = {},
                                LatencyModel  latency  = {});

    // Called on every market tick — simulates fills for eligible orders
    void on_tick(const Tick& tick);

    // Immediately try to fill a market order
    void try_fill_market(OrderId oid, const Tick& tick);

    // Configuration
    void set_slippage(SlippageModel m) { slippage_ = m; }
    void set_latency(LatencyModel m)   { latency_  = m; }

    // Statistics
    [[nodiscard]] std::uint64_t fills_simulated() const noexcept { return fills_; }
    [[nodiscard]] double total_slippage_cost() const noexcept { return total_slippage_; }

private:
    void try_fill_limit(OrderId oid, Order& order, const Tick& tick);

    OrderManager&  oms_;
    SlippageModel  slippage_;
    LatencyModel   latency_;
    std::uint64_t  fills_{0};
    double         total_slippage_{0.0};
};

} // namespace bt
