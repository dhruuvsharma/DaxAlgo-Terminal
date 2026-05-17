#include "../core/event_engine.h"
#include "../core/market_data.h"
#include "../core/order_manager.h"
#include "../core/portfolio.h"
#include "../execution/execution_simulator.h"
#include "../data/csv_loader.h"
#include <spdlog/spdlog.h>
#include <fmt/format.h>
#include <chrono>
#include <vector>
#include <iostream>

using namespace bt;

// Minimal no-op strategy for pure throughput measurement
class NullStrategy : public ITickHandler {
public:
    std::uint64_t count = 0;
    void on_tick(const Tick&) override { ++count; }
};

int main() {
    spdlog::set_level(spdlog::level::warn); // silence logs during benchmark

    constexpr std::size_t NUM_TICKS = 1'000'000;
    spdlog::warn("Generating {} ticks for benchmark...", NUM_TICKS);

    // Generate ticks in-memory
    std::vector<Tick> ticks;
    ticks.reserve(NUM_TICKS);

    double price = 19500.0;
    Timestamp ts = 1700000000000000000LL;

    for (std::size_t i = 0; i < NUM_TICKS; ++i) {
        Tick t{};
        t.timestamp     = ts;
        t.bid_price     = price - 0.5;
        t.ask_price     = price + 0.5;
        t.last_price    = price;
        t.volume        = 1000.0;
        t.instrument_id = 1;
        t.sequence_num  = static_cast<uint32_t>(i);
        ticks.push_back(t);
        ts    += 1'000'000; // 1ms
        price += (i % 3 == 0) ? 0.01 : -0.005;
    }

    fmt::println("=======================================================");
    fmt::println("     TICK BACKTEST ENGINE -- PERFORMANCE BENCHMARK     ");
    fmt::println("=======================================================");
    fmt::println("Tick count: {}", NUM_TICKS);

    // ── Test 1: Event Queue throughput ────────────────────────────────────
    {
        EventEngine engine;
        NullStrategy null_strat;
        engine.register_tick_handler(&null_strat);

        auto t0 = std::chrono::high_resolution_clock::now();
        for (const auto& tick : ticks) {
            engine.publish(tick.timestamp, MarketTickEvent{tick});
        }
        auto t1 = std::chrono::high_resolution_clock::now();
        engine.run();
        auto t2 = std::chrono::high_resolution_clock::now();

        const double pub_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        const double run_ms = std::chrono::duration<double, std::milli>(t2 - t1).count();
        const double total_ms = std::chrono::duration<double, std::milli>(t2 - t0).count();

        fmt::println("\n[Event Engine Throughput]");
        fmt::println("  Publish {} events: {:.2f}ms ({:.1f}M/s)",
            NUM_TICKS, pub_ms, NUM_TICKS / (pub_ms * 1000.0));
        fmt::println("  Dispatch {} events: {:.2f}ms ({:.1f}M/s)",
            NUM_TICKS, run_ms, NUM_TICKS / (run_ms * 1000.0));
        fmt::println("  Total: {:.2f}ms | {:.1f}M ticks/sec",
            total_ms, NUM_TICKS / (total_ms * 1000.0));
    }

    // ── Test 2: Full pipeline (market data + OMS + exec) ─────────────────
    {
        OrderManager   oms(0.0001);
        Portfolio      portfolio(1'000'000.0);
        MarketDataBook book;
        book.register_instrument(1, 100);

        SlippageModel slippage{.fixed_bps = 0.5};
        ExecutionSimulator exec_sim(oms, slippage, {.enabled = false});

        // Submit a mix of limit orders to simulate real workload
        for (int i = 0; i < 100; ++i) {
            oms.submit_limit(1, Side::Buy,  10.0, 19490.0, ticks[0].timestamp);
            oms.submit_limit(1, Side::Sell, 10.0, 19510.0, ticks[0].timestamp);
        }

        EventEngine engine;
        struct PipelineHandler : public ITickHandler {
            MarketDataBook&    book;
            ExecutionSimulator& sim;
            Portfolio&          portfolio;
            void on_tick(const Tick& t) override {
                book.on_tick(t);
                sim.on_tick(t);
                portfolio.mark_to_market(t);
            }
        } handler{book, exec_sim, portfolio};

        engine.register_tick_handler(&handler);

        for (const auto& tick : ticks) {
            engine.publish(tick.timestamp, MarketTickEvent{tick});
        }

        auto t0 = std::chrono::high_resolution_clock::now();
        engine.run();
        auto t1 = std::chrono::high_resolution_clock::now();

        const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        fmt::println("\n[Full Pipeline (MarketData + OMS + Exec)]");
        fmt::println("  {} ticks in {:.2f}ms = {:.1f}M ticks/sec",
            NUM_TICKS, ms, NUM_TICKS / (ms * 1000.0));
        fmt::println("  Fills executed: {}", exec_sim.fills_simulated());
    }

    // ── Test 3: Memory usage ──────────────────────────────────────────────
    {
        fmt::println("\n[Memory Footprint]");
        fmt::println("  sizeof(Tick)     = {} bytes (cache line: {})",
            sizeof(Tick), alignof(Tick));
        fmt::println("  sizeof(Order)    = {} bytes", sizeof(Order));
        fmt::println("  sizeof(Fill)     = {} bytes", sizeof(Fill));
        fmt::println("  sizeof(Position) = {} bytes", sizeof(Position));
        fmt::println("  {} ticks in vector = {:.1f} MB",
            NUM_TICKS, NUM_TICKS * sizeof(Tick) / (1024.0 * 1024.0));
    }

    fmt::println("\n=======================================================\n");
    return 0;
}
