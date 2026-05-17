// tick_backtester (JSON-mode) — polyglot bridge for DaxAlgo Terminal.
//
// Reads a FastBacktestRequest as a single JSON object from stdin, runs the
// backtest using the in-process event engine, and writes a FastBacktestResult
// JSON object to stdout. All non-result output (progress, info, errors) goes
// to stderr — the C# parent treats stdout as a pure result channel.
//
// Strategy identity is a string id: today only "meanReversion" is wired (the
// stock strategy this repo ships). Add a case to the dispatch switch below
// when porting a second strategy.
//
// Parquet column schema matches DaxAlgo Terminal's ParquetTickWriter:
//   TimestampMicros (int64, microseconds since epoch UTC)
//   Bid, Ask        (float64)
//   BidSize, AskSize (int64)  — not consumed today; reserved for L2 work.
//
// Timestamps are converted µs → ns post-load so the engine's nanosecond
// priority queue keeps its semantics intact.

#include "../core/event_engine.h"
#include "../core/market_data.h"
#include "../core/order_manager.h"
#include "../core/portfolio.h"
#include "../execution/execution_simulator.h"
#include "../metrics/performance_metrics.h"
#include "../strategy/mean_reversion.h"
#include "../data/parquet_loader.h"
#include <nlohmann/json.hpp>
#include <spdlog/spdlog.h>
#include <spdlog/sinks/stderr_color_sinks.h>
#include <chrono>
#include <iostream>
#include <string>
#include <vector>

using json = nlohmann::json;
using namespace bt;

namespace {

// Adapter glue mirrors backtest_runner.cpp; kept inline since lifting them to a
// shared header is out of scope for this stage.
class StrategyAdapter : public ITickHandler, public IOrderHandler, public ISessionHandler {
public:
    StrategyAdapter(Strategy& s, StrategyContext& c, MarketDataBook& b)
        : strat_(s), ctx_(c), book_(b) {}

    void on_tick(const Tick& tick) override {
        ctx_.current_time = tick.timestamp;
        book_.on_tick(tick);
        strat_.on_tick(tick, ctx_);
    }
    void on_order_fill(const Fill& fill) override {
        ctx_.portfolio.apply_fill(fill);
        strat_.on_order_fill(fill, ctx_);
        ctx_.portfolio.record_equity(fill.fill_time);
    }
    void on_order_cancel(OrderId oid) override { strat_.on_order_cancel(oid, ctx_); }
    void on_session(const SessionEvent& /*ev*/) override {}

private:
    Strategy&        strat_;
    StrategyContext& ctx_;
    MarketDataBook&  book_;
};

class ExecAdapter : public ITickHandler {
public:
    explicit ExecAdapter(ExecutionSimulator& s) : sim_(s) {}
    void on_tick(const Tick& tick) override { sim_.on_tick(tick); }
private:
    ExecutionSimulator& sim_;
};

double get_param(const json& p, const std::string& key, double fallback) {
    if (!p.contains(key) || p[key].is_null()) return fallback;
    return p[key].get<double>();
}

} // namespace

int main(int argc, char** argv) {
    // Quiet stderr unless --verbose; the C# parent forwards stderr at Debug.
    auto err_sink = spdlog::stderr_color_mt("tick_backtester");
    spdlog::set_default_logger(err_sink);
    bool verbose = false;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--verbose" || a == "-v") verbose = true;
    }
    spdlog::set_level(verbose ? spdlog::level::debug : spdlog::level::warn);
    spdlog::set_pattern("[%T.%e] [%^%l%$] %v");

    // ── 1. Parse request from stdin ───────────────────────────────────────
    json req;
    try {
        std::cin >> req;
    } catch (const std::exception& e) {
        std::cerr << "tick_backtester: malformed JSON on stdin: " << e.what() << "\n";
        return 1;
    }

    std::string strategy_id;
    std::string tick_path;
    double tick_size      = 0.0;
    double multiplier     = 1.0;
    double starting_cash  = 100000.0;
    double taker_fee      = 0.0;
    try {
        strategy_id   = req.at("strategy_id").get<std::string>();
        tick_path     = req.at("tick_data_parquet_path").get<std::string>();
        tick_size     = req.at("tick_size").get<double>();
        multiplier    = req.at("contract_multiplier").get<double>();
        starting_cash = req.at("starting_cash").get<double>();
        taker_fee     = req.value("taker_fee_per_unit", 0.0);
    } catch (const std::exception& e) {
        std::cerr << "tick_backtester: required field missing: " << e.what() << "\n";
        return 1;
    }

    if (strategy_id != "meanReversion") {
        std::cerr << "tick_backtester: strategy '" << strategy_id
                  << "' is not implemented in the C++ engine yet. "
                  << "Only 'meanReversion' is supported today.\n";
        return 2;
    }

    const json params = req.value("params", json::object());

    // ── 2. Load ticks ────────────────────────────────────────────────────
    ParquetLoaderConfig cfg;
    cfg.timestamp_col   = "TimestampMicros";
    cfg.bid_col         = "Bid";
    cfg.ask_col         = "Ask";
    cfg.last_col        = "";  // synthesized from mid by the loader fallback
    cfg.volume_col      = "";
    cfg.default_instrument_id = 1;
    cfg.sort_by_time    = true;

    std::vector<Tick> ticks;
    try {
        ParquetLoader loader(cfg);
        ticks = loader.load(tick_path);
    } catch (const std::exception& e) {
        std::cerr << "tick_backtester: parquet load failed: " << e.what() << "\n";
        return 3;
    }

    if (ticks.empty()) {
        std::cerr << "tick_backtester: zero ticks in " << tick_path << "\n";
        return 3;
    }

    // C# writer emits microseconds; engine internals assume nanoseconds.
    for (auto& t : ticks) t.timestamp *= 1000;

    spdlog::debug("Loaded {} ticks from {}", ticks.size(), tick_path);

    // ── 3. Build engine ───────────────────────────────────────────────────
    // Approximate the C# taker fee as the C++ OMS's flat commission rate.
    // (Maker rebate / bps fee will need a richer fee model on the C++ side.)
    OrderManager   oms(taker_fee > 0 ? taker_fee : 0.0001);
    Portfolio      portfolio(starting_cash);
    MarketDataBook market_book;
    market_book.register_instrument(1, 200);

    SlippageModel slip{.fixed_bps = 0.5, .market_impact_k = 0.05};
    LatencyModel  latency{.base_ns = 50'000, .enabled = true};
    ExecutionSimulator exec_sim(oms, slip, latency);

    std::vector<Fill> all_fills;
    oms.on_fill([&](const Fill& f) { all_fills.push_back(f); });

    MeanReversionParams sp{
        .lookback    = static_cast<std::size_t>(get_param(params, "lookback", 50.0)),
        .entry_z     = get_param(params, "entry_z", 2.0),
        .exit_z      = get_param(params, "exit_z", 0.5),
        .trade_qty   = get_param(params, "trade_qty", 10.0),
        .allow_short = get_param(params, "allow_short", 1.0) != 0.0,
        .instrument  = 1
    };
    MeanReversionStrategy strategy(sp);

    StrategyContext ctx{
        .market_data  = market_book,
        .oms          = oms,
        .portfolio    = portfolio,
        .current_time = ticks.front().timestamp,
    };

    EventEngine engine;
    ExecAdapter exec_adapter(exec_sim);
    StrategyAdapter strat_adapter(strategy, ctx, market_book);
    engine.register_tick_handler(&exec_adapter);
    engine.register_tick_handler(&strat_adapter);
    engine.register_order_handler(&strat_adapter);
    engine.register_session_handler(&strat_adapter);

    for (const auto& t : ticks) engine.publish(t.timestamp, MarketTickEvent{t});

    strategy.on_start(ctx);
    portfolio.record_equity(ticks.front().timestamp);

    const auto t0 = std::chrono::high_resolution_clock::now();
    engine.run();
    const auto t1 = std::chrono::high_resolution_clock::now();

    strategy.on_finish(ctx);
    portfolio.record_equity(ticks.back().timestamp);

    const double engine_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();

    // ── 4. Performance metrics ────────────────────────────────────────────
    PerformanceCalculator perf(0.05);
    const auto rep = perf.compute(portfolio.equity_curve(), all_fills,
                                  starting_cash, portfolio.total_commission());

    // ── 5. Emit result JSON on stdout ─────────────────────────────────────
    // Field names must match the C# FastBacktestResult / BacktestStatistics
    // snake_case_lower naming policy. The C++ PerformanceReport reports
    // percentages and drawdown as positive percentage; the C# side stores
    // them as fractions (0.12 = 12%), so divide by 100 at the seam.
    json out;
    out["stats"] = {
        {"total_return",           rep.total_return_pct / 100.0},
        {"sharpe",                 rep.sharpe_ratio},
        {"sortino",                rep.sortino_ratio},
        {"max_drawdown",           rep.max_drawdown_pct / 100.0},
        {"trade_count",            static_cast<int>(rep.trade_count)},
        {"win_rate",               rep.win_rate},
        {"avg_win",                rep.avg_win},
        {"avg_loss",               rep.avg_loss},
        {"profit_factor",          rep.profit_factor},
        {"expectancy",             rep.avg_trade_pnl},
        {"calmar",                 rep.calmar_ratio},
        {"omega",                  0.0},
        {"downside_deviation",     0.0},
        {"recovery_factor",        0.0},
        {"max_consecutive_losses", 0},
        {"ulcer_index",            0.0},
    };
    out["ending_cash"]               = portfolio.cash();
    out["total_fees"]                = portfolio.total_commission();
    out["equity_curve_parquet_path"] = nullptr;
    out["trades_parquet_path"]       = nullptr;
    out["ticks_processed"]           = static_cast<std::int64_t>(ticks.size());
    out["engine_milliseconds"]       = engine_ms;

    // contract_multiplier is accepted by the C# request but doesn't change C++
    // engine state today (quantity is in lots, not notional). Stash it on
    // stderr at debug so the round-trip stays auditable.
    spdlog::debug("contract_multiplier accepted but not applied: {}", multiplier);
    spdlog::debug("tick_size accepted but not applied: {}", tick_size);

    std::cout << out.dump() << std::endl;
    return 0;
}
