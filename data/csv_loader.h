#pragma once
#include "../include/types.h"
#include <string>
#include <vector>
#include <functional>
#include <unordered_map>

namespace bt {

struct CsvLoaderConfig {
    std::string timestamp_col = "timestamp";
    std::string bid_col       = "bid";
    std::string ask_col       = "ask";
    std::string last_col      = "last";
    std::string volume_col    = "volume";
    char        delimiter     = ',';
    bool        has_header    = true;
    // Timestamp format: "epoch_ns", "epoch_us", "epoch_ms", "epoch_s", "iso8601"
    std::string timestamp_fmt = "epoch_ns";
    InstrumentId default_instrument_id = 1;
};

class CsvLoader {
public:
    explicit CsvLoader(CsvLoaderConfig cfg = {});

    // Load all ticks from file into vector
    std::vector<Tick> load(const std::string& filepath);

    // Stream ticks to callback (memory-efficient for large files)
    void stream(const std::string& filepath,
                std::function<void(const Tick&)> callback);

    [[nodiscard]] std::size_t rows_loaded() const noexcept { return rows_loaded_; }
    [[nodiscard]] std::size_t rows_skipped() const noexcept { return rows_skipped_; }

private:
    Tick parse_row(const std::vector<std::string>& fields,
                   const std::unordered_map<std::string, int>& col_map);
    Timestamp parse_timestamp(const std::string& s);
    std::vector<std::string> split(const std::string& line, char delim);

    CsvLoaderConfig cfg_;
    std::size_t rows_loaded_{0};
    std::size_t rows_skipped_{0};
};

// Generate sample CSV data for testing
void generate_sample_csv(const std::string& filepath, std::size_t num_ticks,
                          double start_price = 100.0,
                          Timestamp start_ts = 1700000000000000000LL);

} // namespace bt
