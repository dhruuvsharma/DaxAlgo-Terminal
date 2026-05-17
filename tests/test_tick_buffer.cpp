#include <gtest/gtest.h>
#include "../core/market_data.h"

using namespace bt;

static Tick make_tick(Timestamp ts, double bid, double ask, double last, double vol = 1000.0) {
    Tick t{};
    t.timestamp   = ts;
    t.bid_price   = bid;
    t.ask_price   = ask;
    t.last_price  = last;
    t.volume      = vol;
    t.instrument_id = 1;
    return t;
}

TEST(TickBuffer, BasicPushPop) {
    TickBuffer buf(10);
    EXPECT_EQ(buf.size(), 0u);

    buf.push(make_tick(1, 99.5, 100.5, 100.0));
    EXPECT_EQ(buf.size(), 1u);

    auto opt = buf.latest();
    ASSERT_TRUE(opt.has_value());
    EXPECT_DOUBLE_EQ(opt->last_price, 100.0);
}

TEST(TickBuffer, Overflow) {
    TickBuffer buf(3);
    for (int i = 0; i < 5; ++i) {
        buf.push(make_tick(i, 99.0 + i, 101.0 + i, 100.0 + i));
    }
    EXPECT_EQ(buf.size(), 3u); // capped at capacity
    EXPECT_DOUBLE_EQ(buf.latest()->last_price, 104.0); // most recent
}

TEST(TickBuffer, SMA) {
    TickBuffer buf(100);
    // Push 10 ticks with mid = 100, 101, ..., 109
    for (int i = 0; i < 10; ++i) {
        buf.push(make_tick(i, 99.5 + i, 100.5 + i, 100.0 + i));
    }
    // SMA of last 5 mids (104.5 to 108.5): mean = 106.5
    double sma = buf.sma(5);
    EXPECT_NEAR(sma, 106.5, 0.01);
}

TEST(TickBuffer, Stdev) {
    TickBuffer buf(100);
    for (int i = 0; i < 10; ++i) {
        buf.push(make_tick(i, 99.5, 100.5, 100.0)); // constant mid
    }
    double sd = buf.stdev(10);
    EXPECT_NEAR(sd, 0.0, 1e-9); // zero variance for constant series
}

TEST(TickBuffer, VWAP) {
    TickBuffer buf(10);
    buf.push(make_tick(1, 99.5, 100.5, 100.0, 1000.0));
    buf.push(make_tick(2, 101.5, 102.5, 102.0, 2000.0));
    // VWAP = (100*1000 + 102*2000) / 3000 = 304000/3000 = 101.333...
    double vwap = buf.vwap();
    EXPECT_NEAR(vwap, 101.333, 0.01);
}
