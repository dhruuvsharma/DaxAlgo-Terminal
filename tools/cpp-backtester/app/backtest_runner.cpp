#include "../core/event_engine.h"
#include "../core/market_data.h"
#include "../core/order_manager.h"
#include "../core/portfolio.h"
#include "../execution/execution_simulator.h"
#include "../metrics/performance_metrics.h"
#include "../strategy/mean_reversion.h"
#include "../data/csv_loader.h"
#include <spdlog/spdlog.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <fmt/format.h>
#include <fmt/chrono.h>
#include <chrono>
#include <string>
#include <vector>
#include <iostream>
#include <fstream>

using namespace bt;

// Adapter: makes Strategy a tick handler and order handler
class StrategyAdapter : public ITickHandler, public IOrderHandler, public ISessionHandler {
public:
    StrategyAdapter(Strategy& strat, StrategyContext& ctx, MarketDataBook& book)
        : strat_(strat), ctx_(ctx), book_(book) {}

    void on_tick(const Tick& tick) override {
        ctx_.current_time = tick.timestamp;
        book_.on_tick(tick); // update book first
        strat_.on_tick(tick, ctx_);
    }

    void on_order_fill(const Fill& fill) override {
        ctx_.portfolio.apply_fill(fill);
        strat_.on_order_fill(fill, ctx_);
        ctx_.portfolio.record_equity(fill.fill_time);
    }

    void on_order_cancel(OrderId oid) override {
        strat_.on_order_cancel(oid, ctx_);
    }

    void on_session(const SessionEvent& ev) override {
        if (ev.type == SessionEvent::Type::Open)
            spdlog::info("Session open: {}", ev.session_name);
        else if (ev.type == SessionEvent::Type::Close)
            spdlog::info("Session close: {}", ev.session_name);
    }

private:
    Strategy&        strat_;
    StrategyContext& ctx_;
    MarketDataBook&  book_;
};

// ExecutionSimulator also needs to be a tick handler
class ExecAdapter : public ITickHandler {
public:
    explicit ExecAdapter(ExecutionSimulator& sim) : sim_(sim) {}
    void on_tick(const Tick& tick) override { sim_.on_tick(tick); }
private:
    ExecutionSimulator& sim_;
};

int main(int argc, char* argv[]) {
    // Setup logging
    auto console = spdlog::stdout_color_mt("console");
    spdlog::set_default_logger(console);
    spdlog::set_level(spdlog::level::info);
    spdlog::set_pattern("[%T.%e] [%^%l%$] %v");

    std::string data_file = "sample_ticks.csv";
    if (argc > 1) data_file = argv[1];

    spdlog::info("TickBacktester v1.0.0 -- High Performance Backtesting Engine");
    spdlog::info("Loading data from: {}", data_file);

    // ── 1. Generate sample data if file doesn't exist ─────────────────────
    {
        std::ifstream test(data_file);
        if (!test.is_open()) {
            spdlog::info("Generating sample data: 100,000 ticks...");
            generate_sample_csv(data_file, 100'000, 19500.0);
            spdlog::info("Sample data written to {}", data_file);
        }
    }

    // ── 2. Load tick data ─────────────────────────────────────────────────
    CsvLoaderConfig loader_cfg;
    loader_cfg.timestamp_fmt = "epoch_ns";
    loader_cfg.default_instrument_id = 1;
    CsvLoader loader(loader_cfg);

    auto t0 = std::chrono::high_resolution_clock::now();
    auto ticks = loader.load(data_file);
    auto t1 = std::chrono::high_resolution_clock::now();

    const double load_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
    spdlog::info("Loaded {} ticks in {:.1f}ms ({:.1f}M ticks/sec)",
                 ticks.size(), load_ms,
                 ticks.size() / (load_ms * 1000.0));

    if (ticks.empty()) {
        spdlog::error("No ticks loaded. Exiting.");
        return 1;
    }

    // ── 3. Setup engine components ────────────────────────────────────────
    constexpr double INITIAL_CASH = 1'000'000.0;

    OrderManager   oms(0.0001);        // 1bp commission
    Portfolio      portfolio(INITIAL_CASH);
    MarketDataBook market_book;

    market_book.register_instrument(1, 200); // buffer 200 ticks

    SlippageModel slippage{.fixed_bps = 0.5, .market_impact_k = 0.05};
    LatencyModel  latency{.base_ns = 50'000, .enabled = true};
    ExecutionSimulator exec_sim(oms, slippage, latency);

    // Wire fill callbacks
    std::vector<Fill> all_fills;
    oms.on_fill([&](const Fill& f) {
        all_fills.push_back(f);
    });

    // ── 4. Setup strategy ─────────────────────────────────────────────────
    MeanReversionParams strat_params{
        .lookback    = 50,
        .entry_z     = 2.0,
        .exit_z      = 0.5,
        .trade_qty   = 10.0,
        .allow_short = true,
        .instrument  = 1
    };
    MeanReversionStrategy strategy(strat_params);

    StrategyContext ctx{
        .market_data   = market_book,
        .oms           = oms,
        .portfolio     = portfolio,
        .current_time  = ticks.front().timestamp
    };

    // ── 5. Setup event engine ─────────────────────────────────────────────
    EventEngine engine;
    ExecAdapter exec_adapter(exec_sim);
    StrategyAdapter strat_adapter(strategy, ctx, market_book);

    // Execution simulator runs BEFORE strategy to ensure fills from previous
    // tick are processed before strategy logic on current tick
    engine.register_tick_handler(&exec_adapter);
    engine.register_tick_handler(&strat_adapter);
    engine.register_order_handler(&strat_adapter);
    engine.register_session_handler(&strat_adapter);

    // ── 6. Publish tick events ────────────────────────────────────────────
    spdlog::info("Queueing {} market events...", ticks.size());
    for (const auto& tick : ticks) {
        engine.publish(tick.timestamp, MarketTickEvent{tick});
    }

    // Session events
    engine.publish(ticks.front().timestamp, SessionEvent{
        SessionEvent::Type::Open, ticks.front().timestamp, "BACKTEST"});
    engine.publish(ticks.back().timestamp, SessionEvent{
        SessionEvent::Type::Close, ticks.back().timestamp, "BACKTEST"});

    // ── 7. Run the backtest ───────────────────────────────────────────────
    strategy.on_start(ctx);
    portfolio.record_equity(ticks.front().timestamp);

    auto run_start = std::chrono::high_resolution_clock::now();
    engine.run();
    auto run_end = std::chrono::high_resolution_clock::now();

    strategy.on_finish(ctx);
    portfolio.record_equity(ticks.back().timestamp);

    // ── 8. Report results ─────────────────────────────────────────────────
    const double run_ms = std::chrono::duration<double, std::milli>(run_end - run_start).count();
    const double ticks_per_sec = ticks.size() / (run_ms / 1000.0);

    spdlog::info("-----------------------------------------");
    spdlog::info("Backtest completed in {:.2f}ms", run_ms);
    spdlog::info("Throughput: {:.2f}M ticks/sec", ticks_per_sec / 1e6);
    spdlog::info("Events processed: {}", engine.total_processed());
    spdlog::info("Orders submitted: {}", oms.total_orders_submitted());
    spdlog::info("Fills executed: {}", oms.total_fills());
    spdlog::info("Signals generated: {}", strategy.signal_count());

    PerformanceCalculator perf_calc(0.05); // 5% risk-free rate
    auto report = perf_calc.compute(
        portfolio.equity_curve(), all_fills,
        INITIAL_CASH, portfolio.total_commission());

    std::cout << '\n' << report.to_string() << '\n';

    spdlog::info("Final cash:         {:.2f}", portfolio.cash());
    spdlog::info("Final equity:       {:.2f}", portfolio.equity());
    spdlog::info("Realized PnL:       {:.2f}", portfolio.realized_pnl());
    spdlog::info("Total commission:   {:.2f}", portfolio.total_commission());

    return 0;
}
