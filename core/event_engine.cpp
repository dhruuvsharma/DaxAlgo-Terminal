#include "event_engine.h"
#include <stdexcept>

namespace bt {

EventEngine::EventEngine(std::size_t reserve_capacity) {
    // Pre-allocate underlying vector of priority queue
    // (std::priority_queue doesn't expose reserve directly, so we use a workaround)
    (void)reserve_capacity; // will grow as needed
}

void EventEngine::publish(Timestamp ts, MarketTickEvent e) {
    queue_.push({ts, sequence_++, std::move(e)});
}
void EventEngine::publish(Timestamp ts, OrderSubmitEvent e) {
    queue_.push({ts, sequence_++, std::move(e)});
}
void EventEngine::publish(Timestamp ts, OrderCancelEvent e) {
    queue_.push({ts, sequence_++, std::move(e)});
}
void EventEngine::publish(Timestamp ts, OrderFillEvent e) {
    queue_.push({ts, sequence_++, std::move(e)});
}
void EventEngine::publish(Timestamp ts, SessionEvent e) {
    queue_.push({ts, sequence_++, std::move(e)});
}

void EventEngine::register_tick_handler(ITickHandler* h) {
    tick_handlers_.push_back(h);
}
void EventEngine::register_order_handler(IOrderHandler* h) {
    order_handlers_.push_back(h);
}
void EventEngine::register_session_handler(ISessionHandler* h) {
    session_handlers_.push_back(h);
}

void EventEngine::run() {
    while (!queue_.empty()) {
        const auto& te = queue_.top();
        dispatch(te);
        queue_.pop();
        ++total_processed_;
    }
}

std::size_t EventEngine::run_batch(std::size_t max_events) {
    std::size_t processed = 0;
    while (!queue_.empty() && processed < max_events) {
        const auto& te = queue_.top();
        dispatch(te);
        queue_.pop();
        ++total_processed_;
        ++processed;
    }
    return processed;
}

bool EventEngine::has_events() const noexcept {
    return !queue_.empty();
}

std::size_t EventEngine::queued_events() const noexcept {
    return queue_.size();
}

void EventEngine::reset() {
    while (!queue_.empty()) queue_.pop();
    sequence_ = 0;
    total_processed_ = 0;
}

void EventEngine::dispatch(const TimestampedEvent& te) {
    std::visit([this](const auto& ev) {
        using T = std::decay_t<decltype(ev)>;

        if constexpr (std::is_same_v<T, MarketTickEvent>) {
            for (auto* h : tick_handlers_) h->on_tick(ev.tick);
        } else if constexpr (std::is_same_v<T, OrderFillEvent>) {
            for (auto* h : order_handlers_) h->on_order_fill(ev.fill);
        } else if constexpr (std::is_same_v<T, OrderCancelEvent>) {
            for (auto* h : order_handlers_) h->on_order_cancel(ev.order_id);
        } else if constexpr (std::is_same_v<T, SessionEvent>) {
            for (auto* h : session_handlers_) h->on_session(ev);
        }
        // OrderSubmitEvent is handled by OMS directly, not dispatched here
    }, te.event);
}

} // namespace bt
