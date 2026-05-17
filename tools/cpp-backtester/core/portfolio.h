#pragma once

#include "../include/types.h"
#include <unordered_map>
#include <vector>

namespace bt {

struct EquityPoint {
    Timestamp timestamp;
    double    equity;    // cash + unrealized PnL
    double    cash;
};

class Portfolio {
public:
    explicit Portfolio(double initial_cash);

    // Called on each market tick to update mark-to-market prices
    void mark_to_market(const Tick& tick);

    // Called when a fill is confirmed
    void apply_fill(const Fill& fill);

    // Queries
    [[nodiscard]] double cash() const noexcept { return cash_; }
    [[nodiscard]] double equity() const noexcept;
    [[nodiscard]] double unrealized_pnl() const noexcept;
    [[nodiscard]] double realized_pnl() const noexcept;
    [[nodiscard]] double total_commission() const noexcept { return total_commission_; }

    [[nodiscard]] const Position* position(InstrumentId id) const;
    [[nodiscard]] std::vector<const Position*> all_positions() const;
    [[nodiscard]] std::vector<EquityPoint> equity_curve() const { return equity_curve_; }

    // Record equity snapshot (call periodically)
    void record_equity(Timestamp ts);

    [[nodiscard]] double initial_cash() const noexcept { return initial_cash_; }

    void reset();

private:
    double cash_;
    double initial_cash_;
    double total_commission_{0.0};
    std::unordered_map<InstrumentId, Position> positions_;
    std::vector<EquityPoint> equity_curve_;
};

} // namespace bt
