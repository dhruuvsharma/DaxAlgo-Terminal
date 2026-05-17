#pragma once
#include "../include/types.h"
#include <string>
#include <vector>
#include <functional>

namespace bt {

struct ParquetLoaderConfig {
    std::string timestamp_col   = "timestamp";
    std::string bid_col         = "bid_price";
    std::string ask_col         = "ask_price";
    std::string last_col        = "last_price";
    std::string volume_col      = "volume";
    std::string instrument_col;  // empty = use default_instrument_id
    InstrumentId default_instrument_id = 1;
    bool         sort_by_time   = true;
};

class ParquetLoader {
public:
    explicit ParquetLoader(ParquetLoaderConfig cfg = {});

    std::vector<Tick> load(const std::string& filepath);
    void stream(const std::string& filepath,
                std::function<void(const Tick&)> callback);

    [[nodiscard]] std::size_t rows_loaded() const noexcept { return rows_loaded_; }

private:
    ParquetLoaderConfig cfg_;
    std::size_t rows_loaded_{0};
};

// Write ticks to parquet format
void write_parquet(const std::string& filepath, const std::vector<Tick>& ticks);

} // namespace bt
