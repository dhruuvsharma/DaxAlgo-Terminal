#pragma once

#include "../include/types.h"
#include <functional>
#include <memory>
#include <queue>
#include <variant>
#include <vector>
#include <cstdint>

namespace bt {

// ── Event Types ────────────────────────────────────────────────────────────

struct MarketTickEvent {
    Tick tick;
};

struct OrderSubmitEvent {
    Order order;
};

struct OrderCancelEvent {
    OrderId order_id;
    Timestamp cancel_time;
};

struct OrderFillEvent {
    Fill fill;
};

struct SessionEvent {
    enum class Type : uint8_t { Open, Close, EndOfData } type;
    Timestamp timestamp;
    std::string session_name;
};

// Discriminated union of all event types
using Event = std::variant<
    MarketTickEvent,
    OrderSubmitEvent,
    OrderCancelEvent,
    OrderFillEvent,
    SessionEvent
>;

// ── Event Priority Queue ───────────────────────────────────────────────────

struct TimestampedEvent {
    Timestamp timestamp;
    std::uint32_t sequence; // tiebreaker for same-timestamp events
    Event event;

    bool operator>(const TimestampedEvent& o) const noexcept {
        if (timestamp != o.timestamp) return timestamp > o.timestamp;
        return sequence > o.sequence;
    }
};

using EventQueue = std::priority_queue<
    TimestampedEvent,
    std::vector<TimestampedEvent>,
    std::greater<TimestampedEvent>
>;

// ── Handlers ───────────────────────────────────────────────────────────────

class ITickHandler {
public:
    virtual ~ITickHandler() = default;
    virtual void on_tick(const Tick& tick) = 0;
};

class IOrderHandler {
public:
    virtual ~IOrderHandler() = default;
    virtual void on_order_fill(const Fill& fill) = 0;
    virtual void on_order_cancel(OrderId oid) = 0;
};

class ISessionHandler {
public:
    virtual ~ISessionHandler() = default;
    virtual void on_session(const SessionEvent& event) = 0;
};

// ── Event Engine ───────────────────────────────────────────────────────────

class EventEngine {
public:
    explicit EventEngine(std::size_t reserve_capacity = 1 << 20);

    // Event publishing
    void publish(Timestamp ts, MarketTickEvent e);
    void publish(Timestamp ts, OrderSubmitEvent e);
    void publish(Timestamp ts, OrderCancelEvent e);
    void publish(Timestamp ts, OrderFillEvent e);
    void publish(Timestamp ts, SessionEvent e);

    // Handler registration
    void register_tick_handler(ITickHandler* h);
    void register_order_handler(IOrderHandler* h);
    void register_session_handler(ISessionHandler* h);

    // Run the event loop — processes all queued events in timestamp order
    void run();

    // Drain a batch of N events (useful for streaming ingestion)
    std::size_t run_batch(std::size_t max_events = 4096);

    [[nodiscard]] bool has_events() const noexcept;
    [[nodiscard]] std::size_t queued_events() const noexcept;
    [[nodiscard]] std::uint64_t total_processed() const noexcept { return total_processed_; }

    void reset();

private:
    void dispatch(const TimestampedEvent& te);

    EventQueue queue_;
    std::vector<ITickHandler*>    tick_handlers_;
    std::vector<IOrderHandler*>   order_handlers_;
    std::vector<ISessionHandler*> session_handlers_;
    std::uint32_t sequence_{0};
    std::uint64_t total_processed_{0};
};

} // namespace bt
