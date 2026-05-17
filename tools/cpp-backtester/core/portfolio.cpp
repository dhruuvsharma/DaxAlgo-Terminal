#include "portfolio.h"
#include <cmath>

namespace bt {

Portfolio::Portfolio(double initial_cash)
    : cash_(initial_cash), initial_cash_(initial_cash) {}

void Portfolio::mark_to_market(const Tick& tick) {
    auto it = positions_.find(tick.instrument_id);
    if (it != positions_.end()) {
        it->second.last_price = tick.mid_price();
    }
}

void Portfolio::apply_fill(const Fill& fill) {
    total_commission_ += fill.commission;
    cash_ -= fill.commission;

    auto& pos = positions_[fill.instrument_id];
    pos.instrument_id = fill.instrument_id;
    pos.last_price    = fill.fill_price;

    const double sign = (fill.side == Side::Buy) ? 1.0 : -1.0;
    const double qty  = sign * fill.fill_qty;

    if (pos.is_flat()) {
        // Opening new position
        pos.quantity  = qty;
        pos.avg_cost  = fill.fill_price;
        pos.realized_pnl = 0.0;
    } else if (std::signbit(pos.quantity) == std::signbit(-qty)) {
        // Closing or reducing existing position
        const double close_qty = std::min(std::abs(qty), std::abs(pos.quantity));
        const double close_sign = (pos.quantity > 0) ? 1.0 : -1.0;
        pos.realized_pnl += close_sign * close_qty * (fill.fill_price - pos.avg_cost);
        pos.quantity     += qty;

        if (std::abs(pos.quantity) < 1e-9) {
            pos.quantity = 0.0;
        }
    } else {
        // Adding to existing position (pyramid)
        const double total_qty = pos.quantity + qty;
        pos.avg_cost = (pos.avg_cost * pos.quantity + fill.fill_price * qty) / total_qty;
        pos.quantity = total_qty;
    }

    // Adjust cash for the trade
    cash_ -= sign * fill.fill_qty * fill.fill_price;
}

double Portfolio::equity() const noexcept {
    return cash_ + unrealized_pnl();
}

double Portfolio::unrealized_pnl() const noexcept {
    double total = 0.0;
    for (const auto& [id, pos] : positions_) {
        total += pos.unrealized_pnl();
    }
    return total;
}

double Portfolio::realized_pnl() const noexcept {
    double total = 0.0;
    for (const auto& [id, pos] : positions_) {
        total += pos.realized_pnl;
    }
    return total;
}

const Position* Portfolio::position(InstrumentId id) const {
    auto it = positions_.find(id);
    return it == positions_.end() ? nullptr : &it->second;
}

std::vector<const Position*> Portfolio::all_positions() const {
    std::vector<const Position*> result;
    result.reserve(positions_.size());
    for (const auto& [id, pos] : positions_) {
        result.push_back(&pos);
    }
    return result;
}

void Portfolio::record_equity(Timestamp ts) {
    equity_curve_.push_back({ts, equity(), cash_});
}

void Portfolio::reset() {
    cash_             = initial_cash_;
    total_commission_ = 0.0;
    positions_.clear();
    equity_curve_.clear();
}

} // namespace bt
