#include <gtest/gtest.h>
#include "../core/order_manager.h"

using namespace bt;

TEST(OrderManager, SubmitMarketOrder) {
    OrderManager oms(0.0001);
    OrderId id = oms.submit_market(1, Side::Buy, 100.0, 1000);
    ASSERT_NE(id, INVALID_ORDER_ID);

    const Order* ord = oms.get_order(id);
    ASSERT_NE(ord, nullptr);
    EXPECT_EQ(ord->type, OrderType::Market);
    EXPECT_EQ(ord->side, Side::Buy);
    EXPECT_DOUBLE_EQ(ord->quantity, 100.0);
    EXPECT_EQ(ord->status, OrderStatus::New);
}

TEST(OrderManager, FillOrder) {
    OrderManager oms(0.0);
    bool fill_received = false;
    oms.on_fill([&](const Fill& f) {
        fill_received = true;
        EXPECT_DOUBLE_EQ(f.fill_price, 19500.0);
        EXPECT_DOUBLE_EQ(f.fill_qty, 10.0);
    });

    OrderId id = oms.submit_market(1, Side::Buy, 10.0, 1000);
    oms.apply_fill(id, 19500.0, 10.0, 2000);

    EXPECT_TRUE(fill_received);
    const Order* ord = oms.get_order(id);
    EXPECT_EQ(ord->status, OrderStatus::Filled);
}

TEST(OrderManager, CancelOrder) {
    OrderManager oms;
    OrderId id = oms.submit_limit(1, Side::Buy, 5.0, 100.0, 1000);
    EXPECT_TRUE(oms.cancel(id, 2000));
    EXPECT_FALSE(oms.cancel(id, 3000)); // already cancelled
    EXPECT_EQ(oms.open_order_count(), 0u);
}

TEST(OrderManager, PartialFill) {
    OrderManager oms;
    OrderId id = oms.submit_limit(1, Side::Buy, 100.0, 50.0, 1000);
    oms.apply_fill(id, 50.0, 40.0, 2000); // partial
    const Order* ord = oms.get_order(id);
    EXPECT_EQ(ord->status, OrderStatus::PartialFill);
    EXPECT_DOUBLE_EQ(ord->remaining_qty(), 60.0);

    oms.apply_fill(id, 50.0, 60.0, 3000); // complete
    EXPECT_EQ(ord->status, OrderStatus::Filled);
}
