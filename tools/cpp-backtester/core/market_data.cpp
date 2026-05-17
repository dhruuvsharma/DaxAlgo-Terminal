#include "market_data.h"
#include <stdexcept>
#include <cmath>
#include <numeric>

namespace bt {

// ── TickBuffer ─────────────────────────────────────────────────────────────

TickBuffer::TickBuffer(std::size_t capacity) : capacity_(capacity) {}

void TickBuffer::push(const Tick& tick) {
    if (buf_.size() >= capacity_) {
        buf_.pop_front();
    }
    buf_.push_back(tick);
}

std::optional<Tick> TickBuffer::latest() const noexcept {
    if (buf_.empty()) return std::nullopt;
    return buf_.back();
}

std::optional<Tick> TickBuffer::at(std::size_t idx) const noexcept {
    if (idx >= buf_.size()) return std::nullopt;
    return buf_[idx];
}

double TickBuffer::sma(std::size_t n) const {
    if (buf_.empty() || n == 0) return 0.0;
    const std::size_t count = std::min(n, buf_.size());
    double sum = 0.0;
    const std::size_t start = buf_.size() - count;
    for (std::size_t i = start; i < buf_.size(); ++i) {
        sum += buf_[i].mid_price();
    }
    return sum / static_cast<double>(count);
}

double TickBuffer::ema(std::size_t n) const {
    if (buf_.empty() || n == 0) return 0.0;
    const double alpha = 2.0 / (static_cast<double>(n) + 1.0);
    double result = buf_.front().mid_price();
    for (std::size_t i = 1; i < buf_.size(); ++i) {
        result = alpha * buf_[i].mid_price() + (1.0 - alpha) * result;
    }
    return result;
}

double TickBuffer::stdev(std::size_t n) const {
    if (buf_.size() < 2 || n < 2) return 0.0;
    const std::size_t count = std::min(n, buf_.size());
    const std::size_t start = buf_.size() - count;

    double mean = 0.0;
    for (std::size_t i = start; i < buf_.size(); ++i) {
        mean += buf_[i].mid_price();
    }
    mean /= static_cast<double>(count);

    double variance = 0.0;
    for (std::size_t i = start; i < buf_.size(); ++i) {
        const double diff = buf_[i].mid_price() - mean;
        variance += diff * diff;
    }
    return std::sqrt(variance / static_cast<double>(count - 1));
}

double TickBuffer::vwap() const {
    if (buf_.empty()) return 0.0;
    double pv = 0.0, vol = 0.0;
    for (const auto& t : buf_) {
        pv  += t.last_price * t.volume;
        vol += t.volume;
    }
    return vol > 0.0 ? pv / vol : 0.0;
}

void TickBuffer::clear() {
    buf_.clear();
}

// ── MarketDataBook ─────────────────────────────────────────────────────────

void MarketDataBook::on_tick(const Tick& tick) {
    latest_[tick.instrument_id] = tick;
    auto it = buffers_.find(tick.instrument_id);
    if (it != buffers_.end()) {
        it->second.push(tick);
    }
}

std::optional<Tick> MarketDataBook::latest(InstrumentId id) const {
    auto it = latest_.find(id);
    if (it == latest_.end()) return std::nullopt;
    return it->second;
}

TickBuffer* MarketDataBook::buffer(InstrumentId id) {
    auto it = buffers_.find(id);
    if (it == buffers_.end()) return nullptr;
    return &it->second;
}

const TickBuffer* MarketDataBook::buffer(InstrumentId id) const {
    auto it = buffers_.find(id);
    if (it == buffers_.end()) return nullptr;
    return &it->second;
}

void MarketDataBook::register_instrument(InstrumentId id, std::size_t buffer_size) {
    buffers_.emplace(id, TickBuffer{buffer_size});
}

std::vector<std::pair<InstrumentId, Tick>> MarketDataBook::snapshot() const {
    std::vector<std::pair<InstrumentId, Tick>> result;
    result.reserve(latest_.size());
    for (const auto& [id, tick] : latest_) {
        result.emplace_back(id, tick);
    }
    return result;
}

} // namespace bt
