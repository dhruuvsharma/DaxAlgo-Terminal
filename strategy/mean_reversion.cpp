#include "mean_reversion.h"
#include <spdlog/spdlog.h>
#include <cmath>

namespace bt {

MeanReversionStrategy::MeanReversionStrategy(MeanReversionParams params)
    : Strategy("MeanReversion"), params_(std::move(params)) {}

void MeanReversionStrategy::on_start(StrategyContext& ctx) {
    ctx.market_data.buffer(params_.instrument); // ensure buffer exists
    spdlog::info("[{}] Strategy started. Lookback={}, Entry Z={}, Exit Z={}",
                 name(), params_.lookback, params_.entry_z, params_.exit_z);
}

void MeanReversionStrategy::on_tick(const Tick& tick, StrategyContext& ctx) {
    if (tick.instrument_id != params_.instrument) return;

    const TickBuffer* buf = ctx.market_data.buffer(params_.instrument);
    if (!buf || buf->size() < params_.lookback) return;

    const double mean  = buf->sma(params_.lookback);
    const double sd    = buf->stdev(params_.lookback);
    if (sd < 1e-9) return;

    const double mid    = tick.mid_price();
    const double z      = (mid - mean) / sd;

    if (!in_position_) {
        if (z < -params_.entry_z) {
            // Price far below mean — go long
            active_order_ = buy_market(params_.instrument, params_.trade_qty, ctx);
            in_position_  = true;
            position_side_= Side::Buy;
            ++signal_count_;
            spdlog::debug("[{}] Long entry at {:.4f}, z={:.2f}", name(), mid, z);
        } else if (params_.allow_short && z > params_.entry_z) {
            // Price far above mean — go short
            active_order_ = sell_market(params_.instrument, params_.trade_qty, ctx);
            in_position_  = true;
            position_side_= Side::Sell;
            ++signal_count_;
            spdlog::debug("[{}] Short entry at {:.4f}, z={:.2f}", name(), mid, z);
        }
    } else {
        // Check exit condition
        const bool exit_long  = (position_side_ == Side::Buy  && z > -params_.exit_z);
        const bool exit_short = (position_side_ == Side::Sell && z < params_.exit_z);

        if (exit_long) {
            sell_market(params_.instrument, params_.trade_qty, ctx);
            in_position_ = false;
            spdlog::debug("[{}] Long exit at {:.4f}, z={:.2f}", name(), mid, z);
        } else if (exit_short) {
            buy_market(params_.instrument, params_.trade_qty, ctx);
            in_position_ = false;
            spdlog::debug("[{}] Short exit at {:.4f}, z={:.2f}", name(), mid, z);
        }
    }
}

void MeanReversionStrategy::on_order_fill(const Fill& fill, StrategyContext& ctx) {
    (void)ctx;
    spdlog::debug("[{}] Fill: {} @ {:.4f} x {:.0f}",
                  name(),
                  fill.side == Side::Buy ? "BUY" : "SELL",
                  fill.fill_price, fill.fill_qty);
}

void MeanReversionStrategy::on_finish(StrategyContext& ctx) {
    spdlog::info("[{}] Finished. Signals generated: {}", name(), signal_count_);
    (void)ctx;
}

} // namespace bt
