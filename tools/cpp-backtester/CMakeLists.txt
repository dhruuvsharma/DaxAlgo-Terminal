cmake_minimum_required(VERSION 3.20)
project(TickBacktester VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Build type
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# Compiler flags
if(MSVC)
  add_compile_options(/W4 /O2 /arch:AVX2 /GL)
  add_link_options(/LTCG)
else()
  add_compile_options(-Wall -Wextra -O3 -march=native -flto)
  add_link_options(-flto)
endif()

# Find packages
find_package(Eigen3 REQUIRED NO_MODULE)
find_package(fmt REQUIRED)
find_package(spdlog REQUIRED)
find_package(Arrow REQUIRED)
find_package(Parquet REQUIRED)
find_package(Threads REQUIRED)

# JSON for the tick_backtester (--json) polyglot bridge. Header-only; pulled
# from upstream at configure time so users don't need a vcpkg install.
include(FetchContent)
FetchContent_Declare(
    json
    URL https://github.com/nlohmann/json/releases/download/v3.11.3/json.tar.xz
    URL_HASH SHA256=d6c65aca6b1ed68e7a182f4757257b107ae403032760ed6ef121c9d55e81757d
)
FetchContent_MakeAvailable(json)

# Optional Boost
find_package(Boost OPTIONAL_COMPONENTS program_options)

# Core library
add_library(backtester_core STATIC
    core/event_engine.cpp
    core/market_data.cpp
    core/order_manager.cpp
    core/portfolio.cpp
    execution/execution_simulator.cpp
    metrics/performance_metrics.cpp
    data/csv_loader.cpp
    data/parquet_loader.cpp
    strategy/mean_reversion.cpp
)

target_include_directories(backtester_core PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_link_libraries(backtester_core PUBLIC
    Eigen3::Eigen
    fmt::fmt
    spdlog::spdlog
    Arrow::arrow_shared
    Parquet::parquet_shared
    Threads::Threads
)

if(Boost_FOUND)
  target_link_libraries(backtester_core PUBLIC Boost::program_options)
  target_compile_definitions(backtester_core PUBLIC HAS_BOOST)
endif()

# Main executable
add_executable(backtester app/backtest_runner.cpp)
target_link_libraries(backtester PRIVATE backtester_core)

# Benchmark executable
add_executable(benchmark app/benchmark.cpp)
target_link_libraries(benchmark PRIVATE backtester_core)

# Data fetcher utility
add_executable(data_fetcher app/data_fetcher.cpp)
target_link_libraries(data_fetcher PRIVATE backtester_core fmt::fmt spdlog::spdlog)

# JSON-mode runner — polyglot bridge for the DaxAlgo Terminal C# parent.
# Reads FastBacktestRequest on stdin, writes FastBacktestResult on stdout.
add_executable(tick_backtester app/backtest_json.cpp)
target_link_libraries(tick_backtester PRIVATE backtester_core nlohmann_json::nlohmann_json)

# Install
install(TARGETS backtester benchmark tick_backtester DESTINATION bin)

# Enable testing
enable_testing()
add_subdirectory(tests EXCLUDE_FROM_ALL)
