#include "order_manager.h"
#include <stdexcept>

namespace bt {

OrderManager::OrderManager(double commission_rate)
    : commission_rate_(commission_rate) {}

OrderId OrderManager::submit_market(InstrumentId instrument, Side side,
                                     Quantity qty, Timestamp ts) {
    Order ord{};
    ord.order_id      = next_order_id();
    ord.submit_time   = ts;
    ord.fill_time     = 0;
    ord.price         = 0.0; // market — irrelevant
    ord.avg_fill_price= 0.0;
    ord.quantity      = qty;
    ord.filled_qty    = 0.0;
    ord.instrument_id = instrument;
    ord.side          = side;
    ord.type          = OrderType::Market;
    ord.status        = OrderStatus::New;
    ord.tif           = TimeInForce::IOC;

    ++order_count_;
    orders_[ord.order_id] = ord;
    return ord.order_id;
}

OrderId OrderManager::submit_limit(InstrumentId instrument, Side side,
                                    Quantity qty, Price price, Timestamp ts,
                                    TimeInForce tif) {
    Order ord{};
    ord.order_id      = next_order_id();
    ord.submit_time   = ts;
    ord.fill_time     = 0;
    ord.price         = price;
    ord.avg_fill_price= 0.0;
    ord.quantity      = qty;
    ord.filled_qty    = 0.0;
    ord.instrument_id = instrument;
    ord.side          = side;
    ord.type          = OrderType::Limit;
    ord.status        = OrderStatus::New;
    ord.tif           = tif;

    ++order_count_;
    orders_[ord.order_id] = ord;
    return ord.order_id;
}

bool OrderManager::cancel(OrderId oid, Timestamp /*ts*/) {
    auto it = orders_.find(oid);
    if (it == orders_.end() || !it->second.is_active()) return false;
    it->second.status = OrderStatus::Cancelled;
    if (cancel_cb_) cancel_cb_(oid);
    return true;
}

void OrderManager::apply_fill(OrderId oid, Price fill_price,
                               Quantity fill_qty, Timestamp ts) {
    auto it = orders_.find(oid);
    if (it == orders_.end()) return;
    Order& ord = it->second;

    // Update weighted average fill price
    const double prev_notional = ord.avg_fill_price * ord.filled_qty;
    const double new_notional  = fill_price * fill_qty;
    ord.filled_qty += fill_qty;
    ord.avg_fill_price = (prev_notional + new_notional) / ord.filled_qty;

    if (ord.filled_qty >= ord.quantity - 1e-9) {
        ord.status    = OrderStatus::Filled;
        ord.fill_time = ts;
    } else {
        ord.status = OrderStatus::PartialFill;
    }

    ++fill_count_;

    if (fill_cb_) {
        Fill f{};
        f.order_id      = oid;
        f.trade_id      = next_trade_id();
        f.fill_time     = ts;
        f.fill_price    = fill_price;
        f.fill_qty      = fill_qty;
        f.instrument_id = ord.instrument_id;
        f.side          = ord.side;
        f.commission    = fill_price * fill_qty * commission_rate_;
        fill_cb_(f);
    }
}

const Order* OrderManager::get_order(OrderId oid) const {
    auto it = orders_.find(oid);
    return it == orders_.end() ? nullptr : &it->second;
}

std::vector<const Order*> OrderManager::open_orders() const {
    std::vector<const Order*> result;
    result.reserve(orders_.size() / 4);
    for (const auto& [id, ord] : orders_) {
        if (ord.is_active()) result.push_back(&ord);
    }
    return result;
}

std::vector<const Order*> OrderManager::open_orders(InstrumentId id) const {
    std::vector<const Order*> result;
    for (const auto& [oid, ord] : orders_) {
        if (ord.is_active() && ord.instrument_id == id)
            result.push_back(&ord);
    }
    return result;
}

std::size_t OrderManager::open_order_count() const {
    std::size_t count = 0;
    for (const auto& [id, ord] : orders_) {
        if (ord.is_active()) ++count;
    }
    return count;
}

void OrderManager::reset() {
    orders_.clear();
    order_id_seq_ = 0;
    trade_id_seq_ = 0;
    order_count_  = 0;
    fill_count_   = 0;
}

} // namespace bt
