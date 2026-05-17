#pragma once

#include "../include/types.h"
#include <unordered_map>
#include <vector>
#include <functional>
#include <atomic>

namespace bt {

using FillCallback   = std::function<void(const Fill&)>;
using CancelCallback = std::function<void(OrderId)>;

class OrderManager {
public:
    explicit OrderManager(double commission_rate = 0.0001);

    // Submit an order — returns assigned OrderId
    OrderId submit_market(InstrumentId instrument, Side side,
                          Quantity qty, Timestamp ts);

    OrderId submit_limit(InstrumentId instrument, Side side,
                         Quantity qty, Price price, Timestamp ts,
                         TimeInForce tif = TimeInForce::GTC);

    // Cancel an open order
    bool cancel(OrderId oid, Timestamp ts);

    // Apply a fill to an existing order
    void apply_fill(OrderId oid, Price fill_price, Quantity fill_qty,
                    Timestamp ts);

    // Query
    [[nodiscard]] const Order* get_order(OrderId oid) const;
    [[nodiscard]] std::vector<const Order*> open_orders() const;
    [[nodiscard]] std::vector<const Order*> open_orders(InstrumentId id) const;
    [[nodiscard]] std::size_t open_order_count() const;

    // Callbacks
    void on_fill(FillCallback cb)   { fill_cb_ = std::move(cb); }
    void on_cancel(CancelCallback cb) { cancel_cb_ = std::move(cb); }

    // Statistics
    [[nodiscard]] std::uint64_t total_orders_submitted() const noexcept { return order_count_; }
    [[nodiscard]] std::uint64_t total_fills() const noexcept { return fill_count_; }

    void reset();

private:
    OrderId next_order_id() noexcept { return ++order_id_seq_; }
    TradeId next_trade_id() noexcept { return ++trade_id_seq_; }

    std::unordered_map<OrderId, Order> orders_;
    FillCallback   fill_cb_;
    CancelCallback cancel_cb_;
    double commission_rate_;

    std::uint64_t order_id_seq_{0};
    std::uint64_t trade_id_seq_{0};
    std::uint64_t order_count_{0};
    std::uint64_t fill_count_{0};
};

} // namespace bt
