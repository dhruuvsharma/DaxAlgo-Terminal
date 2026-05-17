#include "csv_loader.h"
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <charconv>
#include <random>
#include <cmath>
#include <unordered_map>
#include <fmt/format.h>

namespace bt {

CsvLoader::CsvLoader(CsvLoaderConfig cfg) : cfg_(std::move(cfg)) {}

std::vector<std::string> CsvLoader::split(const std::string& line, char delim) {
    std::vector<std::string> tokens;
    std::string token;
    std::istringstream ss(line);
    while (std::getline(ss, token, delim)) {
        tokens.push_back(token);
    }
    return tokens;
}

Timestamp CsvLoader::parse_timestamp(const std::string& s) {
    long long val = 0;
    std::from_chars(s.data(), s.data() + s.size(), val);
    if (cfg_.timestamp_fmt == "epoch_ns")  return val;
    if (cfg_.timestamp_fmt == "epoch_us")  return val * 1000LL;
    if (cfg_.timestamp_fmt == "epoch_ms")  return val * 1'000'000LL;
    if (cfg_.timestamp_fmt == "epoch_s")   return val * 1'000'000'000LL;
    // iso8601 parsing would go here
    return val;
}

Tick CsvLoader::parse_row(const std::vector<std::string>& fields,
                           const std::unordered_map<std::string, int>& col_map) {
    Tick t{};
    t.instrument_id = cfg_.default_instrument_id;

    auto get = [&](const std::string& col) -> double {
        auto it = col_map.find(col);
        if (it == col_map.end() || it->second >= static_cast<int>(fields.size()))
            return 0.0;
        double v = 0.0;
        std::from_chars(fields[it->second].data(),
                        fields[it->second].data() + fields[it->second].size(), v);
        return v;
    };

    auto ts_it = col_map.find(cfg_.timestamp_col);
    if (ts_it != col_map.end() && ts_it->second < static_cast<int>(fields.size()))
        t.timestamp = parse_timestamp(fields[ts_it->second]);

    t.bid_price  = get(cfg_.bid_col);
    t.ask_price  = get(cfg_.ask_col);
    t.last_price = get(cfg_.last_col);
    t.volume     = get(cfg_.volume_col);

    // If last_price missing, use mid
    if (t.last_price == 0.0 && t.bid_price > 0.0)
        t.last_price = (t.bid_price + t.ask_price) * 0.5;

    return t;
}

std::vector<Tick> CsvLoader::load(const std::string& filepath) {
    std::vector<Tick> ticks;
    stream(filepath, [&](const Tick& t) { ticks.push_back(t); });
    return ticks;
}

void CsvLoader::stream(const std::string& filepath,
                        std::function<void(const Tick&)> callback) {
    std::ifstream file(filepath);
    if (!file.is_open())
        throw std::runtime_error(fmt::format("Cannot open file: {}", filepath));

    rows_loaded_  = 0;
    rows_skipped_ = 0;

    std::string line;
    std::unordered_map<std::string, int> col_map;

    if (cfg_.has_header && std::getline(file, line)) {
        auto headers = split(line, cfg_.delimiter);
        for (int i = 0; i < static_cast<int>(headers.size()); ++i) {
            col_map[headers[i]] = i;
        }
    } else {
        // Default column order
        col_map[cfg_.timestamp_col] = 0;
        col_map[cfg_.bid_col]       = 1;
        col_map[cfg_.ask_col]       = 2;
        col_map[cfg_.last_col]      = 3;
        col_map[cfg_.volume_col]    = 4;
    }

    while (std::getline(file, line)) {
        if (line.empty() || line[0] == '#') { ++rows_skipped_; continue; }
        try {
            auto fields = split(line, cfg_.delimiter);
            Tick t = parse_row(fields, col_map);
            callback(t);
            ++rows_loaded_;
        } catch (...) {
            ++rows_skipped_;
        }
    }
}

void generate_sample_csv(const std::string& filepath, std::size_t num_ticks,
                          double start_price, Timestamp start_ts) {
    std::ofstream f(filepath);
    if (!f.is_open())
        throw std::runtime_error("Cannot create sample CSV: " + filepath);

    f << "timestamp,bid,ask,last,volume\n";

    std::mt19937_64 rng(42);
    std::normal_distribution<double> price_dist(0.0, 0.01);
    std::uniform_real_distribution<double> volume_dist(100.0, 10000.0);
    std::uniform_real_distribution<double> spread_dist(0.01, 0.05);

    double price = start_price;
    Timestamp ts = start_ts;

    for (std::size_t i = 0; i < num_ticks; ++i) {
        price += price_dist(rng);
        price = std::max(price, 0.01);
        const double spread = spread_dist(rng);
        const double bid    = price - spread / 2.0;
        const double ask    = price + spread / 2.0;
        const double vol    = volume_dist(rng);
        ts += 1'000'000; // 1ms intervals

        f << ts << ',' << fmt::format("{:.4f}", bid)
          << ',' << fmt::format("{:.4f}", ask)
          << ',' << fmt::format("{:.4f}", price)
          << ',' << fmt::format("{:.0f}", vol) << '\n';
    }
}

} // namespace bt
