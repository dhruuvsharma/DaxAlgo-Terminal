#include <gtest/gtest.h>
#include "../core/event_engine.h"

using namespace bt;

struct CountingHandler : public ITickHandler {
    std::size_t count = 0;
    Timestamp last_ts = 0;
    bool ordered = true;
    void on_tick(const Tick& t) override {
        if (t.timestamp < last_ts) ordered = false;
        last_ts = t.timestamp;
        ++count;
    }
};

TEST(EventEngine, ProcessesEventsInOrder) {
    EventEngine engine;
    CountingHandler h;
    engine.register_tick_handler(&h);

    // Publish out of order
    Tick t{};
    t.instrument_id = 1;

    t.timestamp = 3000; engine.publish(3000, MarketTickEvent{t});
    t.timestamp = 1000; engine.publish(1000, MarketTickEvent{t});
    t.timestamp = 2000; engine.publish(2000, MarketTickEvent{t});

    engine.run();
    EXPECT_EQ(h.count, 3u);
    EXPECT_TRUE(h.ordered);
}

TEST(EventEngine, CountsProcessed) {
    EventEngine engine;
    CountingHandler h;
    engine.register_tick_handler(&h);

    Tick t{};
    for (int i = 0; i < 100; ++i) {
        t.timestamp = i;
        engine.publish(i, MarketTickEvent{t});
    }

    engine.run();
    EXPECT_EQ(engine.total_processed(), 100u);
    EXPECT_EQ(h.count, 100u);
}

TEST(EventEngine, BatchProcessing) {
    EventEngine engine;
    CountingHandler h;
    engine.register_tick_handler(&h);

    Tick t{};
    for (int i = 0; i < 1000; ++i) {
        t.timestamp = i;
        engine.publish(i, MarketTickEvent{t});
    }

    std::size_t batch1 = engine.run_batch(400);
    EXPECT_EQ(batch1, 400u);
    EXPECT_EQ(h.count, 400u);

    engine.run(); // process rest
    EXPECT_EQ(h.count, 1000u);
}
