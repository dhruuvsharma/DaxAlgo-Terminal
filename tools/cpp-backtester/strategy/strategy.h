#pragma once

#include "../include/types.h"
#include "../core/market_data.h"
#include "../core/order_manager.h"
#include "../core/portfolio.h"
#include <string>
#include <memory>

namespace bt {

// Context object passed to strategy on each event
struct StrategyContext {
    const MarketDataBook& market_data;
    OrderManager&         oms;
    Portfolio&            portfolio;
    Timestamp             current_time;
};

// Abstract base strategy — all user strategies derive from this
class Strategy {
public:
    explicit Strategy(std::string name) : name_(std::move(name)) {}
    virtual ~Strategy() = default;

    // Lifecycle hooks
    virtual void on_start(StrategyContext& ctx) { (void)ctx; }
    virtual void on_tick(const Tick& tick, StrategyContext& ctx) = 0;
    virtual void on_order_fill(const Fill& fill, StrategyContext& ctx) { (void)fill; (void)ctx; }
    virtual void on_order_cancel(OrderId oid, StrategyContext& ctx) { (void)oid; (void)ctx; }
    virtual void on_finish(StrategyContext& ctx) { (void)ctx; }

    [[nodiscard]] const std::string& name() const noexcept { return name_; }

    // Convenience helpers
    OrderId buy_market(InstrumentId inst, Quantity qty, StrategyContext& ctx) {
        return ctx.oms.submit_market(inst, Side::Buy, qty, ctx.current_time);
    }
    OrderId sell_market(InstrumentId inst, Quantity qty, StrategyContext& ctx) {
        return ctx.oms.submit_market(inst, Side::Sell, qty, ctx.current_time);
    }
    OrderId buy_limit(InstrumentId inst, Quantity qty, Price price, StrategyContext& ctx) {
        return ctx.oms.submit_limit(inst, Side::Buy, qty, price, ctx.current_time);
    }
    OrderId sell_limit(InstrumentId inst, Quantity qty, Price price, StrategyContext& ctx) {
        return ctx.oms.submit_limit(inst, Side::Sell, qty, price, ctx.current_time);
    }

private:
    std::string name_;
};

} // namespace bt
