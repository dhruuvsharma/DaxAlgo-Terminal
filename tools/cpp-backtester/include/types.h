#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <chrono>
#include <limits>

namespace bt {

// Nanosecond precision timestamp
using Timestamp = std::int64_t; // nanoseconds since epoch
using Price     = double;
using Quantity  = double;
using OrderId   = std::uint64_t;
using TradeId   = std::uint64_t;
using InstrumentId = std::uint32_t;

constexpr Price    INVALID_PRICE    = std::numeric_limits<Price>::quiet_NaN();
constexpr Quantity INVALID_QTY      = -1.0;
constexpr OrderId  INVALID_ORDER_ID = 0;

enum class Side : std::uint8_t {
    Buy  = 0,
    Sell = 1
};

enum class OrderType : std::uint8_t {
    Market = 0,
    Limit  = 1,
    Stop   = 2
};

enum class OrderStatus : std::uint8_t {
    New         = 0,
    PartialFill = 1,
    Filled      = 2,
    Cancelled   = 3,
    Rejected    = 4
};

enum class TimeInForce : std::uint8_t {
    GTC = 0, // Good till cancel
    IOC = 1, // Immediate or cancel
    FOK = 2, // Fill or kill
    DAY = 3
};

// Tick data — cache-line friendly (fits in 64 bytes)
struct alignas(64) Tick {
    Timestamp    timestamp;     // 8 bytes
    Price        bid_price;     // 8 bytes
    Price        ask_price;     // 8 bytes
    Price        last_price;    // 8 bytes
    Quantity     volume;        // 8 bytes
    Quantity     bid_size;      // 8 bytes
    Quantity     ask_size;      // 8 bytes
    InstrumentId instrument_id; // 4 bytes
    std::uint32_t sequence_num; // 4 bytes
    // Total: 64 bytes

    [[nodiscard]] Price mid_price() const noexcept {
        return (bid_price + ask_price) * 0.5;
    }
    [[nodiscard]] Price spread() const noexcept {
        return ask_price - bid_price;
    }
};

static_assert(sizeof(Tick) == 64, "Tick must be exactly one cache line");
static_assert(alignof(Tick) == 64, "Tick must be cache-line aligned");

// Order structure
struct Order {
    OrderId     order_id;
    Timestamp   submit_time;
    Timestamp   fill_time;
    Price       price;
    Price       avg_fill_price;
    Quantity    quantity;
    Quantity    filled_qty;
    InstrumentId instrument_id;
    Side        side;
    OrderType   type;
    OrderStatus status;
    TimeInForce tif;

    [[nodiscard]] Quantity remaining_qty() const noexcept {
        return quantity - filled_qty;
    }
    [[nodiscard]] bool is_active() const noexcept {
        return status == OrderStatus::New || status == OrderStatus::PartialFill;
    }
};

// Fill report
struct Fill {
    OrderId   order_id;
    TradeId   trade_id;
    Timestamp fill_time;
    Price     fill_price;
    Quantity  fill_qty;
    InstrumentId instrument_id;
    Side      side;
    double    commission;
};

// Position state
struct Position {
    InstrumentId instrument_id;
    Quantity     quantity;       // positive = long, negative = short
    Price        avg_cost;       // weighted average cost
    double       realized_pnl;
    Price        last_price;

    [[nodiscard]] double unrealized_pnl() const noexcept {
        return quantity * (last_price - avg_cost);
    }
    [[nodiscard]] double total_pnl() const noexcept {
        return realized_pnl + unrealized_pnl();
    }
    [[nodiscard]] bool is_flat() const noexcept {
        return std::abs(quantity) < 1e-9;
    }
};

// Instrument metadata
struct Instrument {
    InstrumentId id;
    std::string  symbol;
    std::string  exchange;
    Price        tick_size;
    Quantity     lot_size;
    double       commission_rate; // fraction of notional
    double       margin_rate;
};

} // namespace bt
