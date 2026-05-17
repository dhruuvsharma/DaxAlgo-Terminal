#pragma once

#include "../include/types.h"
#include <deque>
#include <unordered_map>
#include <vector>
#include <optional>
#include <span>

namespace bt {

// Rolling window of ticks for a single instrument — used by strategies
class TickBuffer {
public:
    explicit TickBuffer(std::size_t capacity = 1024);

    void push(const Tick& tick);

    [[nodiscard]] std::optional<Tick> latest() const noexcept;
    [[nodiscard]] std::optional<Tick> at(std::size_t idx) const noexcept; // 0 = oldest
    [[nodiscard]] std::size_t size() const noexcept { return buf_.size(); }
    [[nodiscard]] bool full() const noexcept { return buf_.size() == capacity_; }

    // Compute simple moving average of mid prices over last N ticks
    [[nodiscard]] double sma(std::size_t n) const;
    // Compute exponential moving average
    [[nodiscard]] double ema(std::size_t n) const;
    // Rolling standard deviation
    [[nodiscard]] double stdev(std::size_t n) const;
    // VWAP over buffer
    [[nodiscard]] double vwap() const;

    void clear();

private:
    std::deque<Tick> buf_;
    std::size_t capacity_;
};

// Market data book — maintains latest quotes and tick history per instrument
class MarketDataBook {
public:
    void on_tick(const Tick& tick);

    [[nodiscard]] std::optional<Tick> latest(InstrumentId id) const;
    [[nodiscard]] TickBuffer* buffer(InstrumentId id);
    [[nodiscard]] const TickBuffer* buffer(InstrumentId id) const;

    void register_instrument(InstrumentId id, std::size_t buffer_size = 1024);

    // Snapshot of all current quotes
    [[nodiscard]] std::vector<std::pair<InstrumentId, Tick>> snapshot() const;

private:
    std::unordered_map<InstrumentId, Tick> latest_;
    std::unordered_map<InstrumentId, TickBuffer> buffers_;
};

} // namespace bt
