#include <gtest/gtest.h>
#include "../core/portfolio.h"

using namespace bt;

static Fill make_fill(Side side, double price, double qty, InstrumentId inst = 1) {
    Fill f{};
    f.order_id      = 1;
    f.trade_id      = 1;
    f.fill_time     = 1000;
    f.fill_price    = price;
    f.fill_qty      = qty;
    f.instrument_id = inst;
    f.side          = side;
    f.commission    = 0.0;
    return f;
}

TEST(Portfolio, InitialState) {
    Portfolio port(1'000'000.0);
    EXPECT_DOUBLE_EQ(port.cash(), 1'000'000.0);
    EXPECT_DOUBLE_EQ(port.equity(), 1'000'000.0);
    EXPECT_DOUBLE_EQ(port.unrealized_pnl(), 0.0);
}

TEST(Portfolio, BuyPosition) {
    Portfolio port(1'000'000.0);
    port.apply_fill(make_fill(Side::Buy, 100.0, 10.0));
    EXPECT_DOUBLE_EQ(port.cash(), 1'000'000.0 - 1000.0);
    const Position* pos = port.position(1);
    ASSERT_NE(pos, nullptr);
    EXPECT_DOUBLE_EQ(pos->quantity, 10.0);
    EXPECT_DOUBLE_EQ(pos->avg_cost, 100.0);
}

TEST(Portfolio, RoundTrip) {
    Portfolio port(1'000'000.0);
    port.apply_fill(make_fill(Side::Buy,  100.0, 10.0));
    port.apply_fill(make_fill(Side::Sell, 110.0, 10.0));
    // Realized PnL = 10 * (110 - 100) = 100
    EXPECT_NEAR(port.realized_pnl(), 100.0, 1e-9);
    EXPECT_NEAR(port.cash(), 1'000'000.0 + 100.0, 1e-9);
}

TEST(Portfolio, MarkToMarket) {
    Portfolio port(1'000'000.0);
    port.apply_fill(make_fill(Side::Buy, 100.0, 10.0));

    Tick t{};
    t.instrument_id = 1;
    t.bid_price     = 104.5;
    t.ask_price     = 105.5;
    t.last_price    = 105.0;
    port.mark_to_market(t);

    // Unrealized PnL = 10 * (105 - 100) = 50 (using mid price 105)
    EXPECT_NEAR(port.unrealized_pnl(), 50.0, 1e-9);
    EXPECT_NEAR(port.equity(), 1'000'000.0 - 1000.0 + 1050.0, 1e-9);
}
