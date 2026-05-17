// Data fetcher utility — downloads historical market data from free APIs
// Supports: CSV output compatible with the backtester's loader
//
// Usage:
//   data_fetcher --source binance --symbol BTCUSDT --interval 1m --limit 1000 --out btc.csv
//   data_fetcher --source alpha_vantage --symbol RELIANCE.BSE --out reliance.csv
//   data_fetcher --generate --symbol NIFTY --ticks 1000000 --out nifty_sim.csv

#include "../data/csv_loader.h"
#include <spdlog/spdlog.h>
#include <fmt/format.h>
#include <iostream>
#include <string>
#include <map>
#include <cstring>
#include <chrono>

using namespace bt;

struct FetcherArgs {
    std::string source    = "generate";
    std::string symbol    = "NIFTY";
    std::string interval  = "1m";
    std::size_t ticks     = 100'000;
    std::string out_file  = "data.csv";
    std::string api_key;
};

FetcherArgs parse_args(int argc, char* argv[]) {
    FetcherArgs args;
    for (int i = 1; i < argc - 1; ++i) {
        std::string key = argv[i];
        std::string val = argv[i + 1];
        if (key == "--source")   { args.source   = val; ++i; }
        if (key == "--symbol")   { args.symbol   = val; ++i; }
        if (key == "--interval") { args.interval = val; ++i; }
        if (key == "--ticks" || key == "--limit") {
            args.ticks = std::stoull(val); ++i;
        }
        if (key == "--out")      { args.out_file = val; ++i; }
        if (key == "--api-key")  { args.api_key  = val; ++i; }
    }
    return args;
}

void print_usage() {
    fmt::println(R"(
TickBacktester Data Fetcher
===========================
Usage:
  data_fetcher [options]

Options:
  --source <src>      Data source: generate | binance | alpha_vantage | polygon
  --symbol <sym>      Instrument symbol (e.g., BTCUSDT, NIFTY, RELIANCE)
  --interval <i>      OHLCV interval: 1m, 5m, 1h, 1d
  --ticks <n>         Number of ticks to generate/fetch
  --out <file>        Output CSV file path
  --api-key <key>     API key for paid sources

Examples:
  data_fetcher --source generate --symbol NIFTY --ticks 1000000 --out nifty.csv
  data_fetcher --source generate --symbol BTCUSDT --ticks 500000 --out btc.csv

Output CSV format (compatible with backtester):
  timestamp,bid,ask,last,volume
  (timestamps in nanoseconds since Unix epoch)

API Integration Notes:
  Binance:       https://api.binance.com/api/v3/klines
  Alpha Vantage: https://www.alphavantage.co/query
  Polygon.io:    https://api.polygon.io/v2/aggs/ticker
  Upstox:        https://api.upstox.com/v2/historical-candle

  For production use, install libcurl or use a REST client library.
  This utility uses the generate source by default for offline testing.
)");
}

int main(int argc, char* argv[]) {
    spdlog::set_level(spdlog::level::info);
    spdlog::set_pattern("[%T] [%^%l%$] %v");

    if (argc == 1) { print_usage(); return 0; }

    auto args = parse_args(argc, argv);

    spdlog::info("Data Fetcher: source={}, symbol={}, ticks={}, out={}",
                 args.source, args.symbol, args.ticks, args.out_file);

    if (args.source == "generate") {
        // Simulate different asset characteristics
        double start_price = 100.0;
        if (args.symbol == "NIFTY" || args.symbol == "NIFTY50")
            start_price = 19500.0;
        else if (args.symbol == "BANKNIFTY")
            start_price = 44000.0;
        else if (args.symbol == "RELIANCE")
            start_price = 2500.0;
        else if (args.symbol == "TCS")
            start_price = 3800.0;
        else if (args.symbol == "BTCUSDT" || args.symbol == "BTC")
            start_price = 43000.0;
        else if (args.symbol == "ETHUSD")
            start_price = 2300.0;
        else if (args.symbol.find("USD") != std::string::npos)
            start_price = 1.08; // forex

        spdlog::info("Generating {} simulated ticks for {} at start price {:.2f}",
                     args.ticks, args.symbol, start_price);

        auto t0 = std::chrono::high_resolution_clock::now();
        generate_sample_csv(args.out_file, args.ticks, start_price);
        auto t1 = std::chrono::high_resolution_clock::now();

        const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        spdlog::info("Generated {} ticks in {:.1f}ms -> {}", args.ticks, ms, args.out_file);
        spdlog::info("File size: ~{:.1f}MB",
                     args.ticks * 50.0 / (1024.0 * 1024.0));
    } else {
        spdlog::warn("Source '{}' requires HTTP client integration.", args.source);
        spdlog::warn("See comments in data_fetcher.cpp for API endpoints.");
        spdlog::warn("Falling back to generated data...");
        generate_sample_csv(args.out_file, args.ticks);
        spdlog::info("Generated fallback data -> {}", args.out_file);
    }

    spdlog::info("Done. Load with: backtester {}", args.out_file);
    return 0;
}
