find_package(GTest QUIET)

if(GTest_FOUND)
  add_executable(test_core
      test_tick_buffer.cpp
      test_order_manager.cpp
      test_portfolio.cpp
      test_event_engine.cpp
  )
  target_link_libraries(test_core PRIVATE backtester_core GTest::gtest_main)
  add_test(NAME CoreTests COMMAND test_core)
endif()
