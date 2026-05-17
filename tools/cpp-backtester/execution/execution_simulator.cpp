#include "execution_simulator.h"

namespace bt {

ExecutionSimulator::ExecutionSimulator(OrderManager& oms,
                                       SlippageModel slippage,
                                       LatencyModel latency)
    : oms_(oms), slippage_(slippage), latency_(latency) {}

void ExecutionSimulator::on_tick(const Tick& tick) {
    // Check all open limit orders for the instrument
    auto open = oms_.open_orders(tick.instrument_id);
    for (const auto* ord_ptr : open) {
        if (ord_ptr->type == OrderType::Market) {
            try_fill_market(ord_ptr->order_id, tick);
        } else if (ord_ptr->type == OrderType::Limit) {
            // We need non-const access — cast safely since OMS owns the order
            auto* mutable_ord = const_cast<Order*>(ord_ptr);
            try_fill_limit(ord_ptr->order_id, *mutable_ord, tick);
        }
    }
}

void ExecutionSimulator::try_fill_market(OrderId oid, const Tick& tick) {
    const Order* ord = oms_.get_order(oid);
    if (!ord || !ord->is_active()) return;

    // Execute at best bid/ask with slippage
    Price base_price = (ord->side == Side::Buy) ? tick.ask_price : tick.bid_price;
    Price fill_price = slippage_.compute(base_price, ord->remaining_qty(), ord->side);

    const Timestamp fill_ts = tick.timestamp + latency_.sample();
    const double slippage_cost = std::abs(fill_price - base_price) * ord->remaining_qty();
    total_slippage_ += slippage_cost;

    oms_.apply_fill(oid, fill_price, ord->remaining_qty(), fill_ts);
    ++fills_;
}

void ExecutionSimulator::try_fill_limit(OrderId oid, Order& order, const Tick& tick) {
    if (!order.is_active()) return;

    bool triggered = false;
    if (order.side == Side::Buy) {
        // Buy limit fills when ask_price drops to or below limit
        triggered = tick.ask_price <= order.price;
    } else {
        // Sell limit fills when bid_price rises to or above limit
        triggered = tick.bid_price >= order.price;
    }

    if (!triggered) return;

    // Fill at the limit price (no additional slippage for passive fills)
    const Timestamp fill_ts = tick.timestamp + latency_.sample();
    oms_.apply_fill(oid, order.price, order.remaining_qty(), fill_ts);
    ++fills_;
}

} // namespace bt
