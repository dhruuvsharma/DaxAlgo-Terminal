#include "parquet_loader.h"
#include <arrow/api.h>
#include <arrow/io/api.h>
#include <parquet/arrow/reader.h>
#include <parquet/arrow/writer.h>
#include <parquet/exception.h>
#include <stdexcept>
#include <algorithm>
#include <spdlog/spdlog.h>

namespace bt {

ParquetLoader::ParquetLoader(ParquetLoaderConfig cfg) : cfg_(std::move(cfg)) {}

std::vector<Tick> ParquetLoader::load(const std::string& filepath) {
    std::vector<Tick> ticks;
    stream(filepath, [&](const Tick& t) { ticks.push_back(t); });

    if (cfg_.sort_by_time) {
        std::sort(ticks.begin(), ticks.end(),
                  [](const Tick& a, const Tick& b) {
                      return a.timestamp < b.timestamp;
                  });
    }
    return ticks;
}

void ParquetLoader::stream(const std::string& filepath,
                            std::function<void(const Tick&)> callback) {
    rows_loaded_ = 0;

    auto result = arrow::io::ReadableFile::Open(filepath);
    if (!result.ok())
        throw std::runtime_error("Cannot open parquet: " + filepath);

    std::shared_ptr<arrow::io::ReadableFile> infile = *result;

    std::unique_ptr<parquet::arrow::FileReader> reader;
    auto status = parquet::arrow::OpenFile(infile,
                                           arrow::default_memory_pool(),
                                           &reader);
    if (!status.ok())
        throw std::runtime_error("Parquet open failed: " + status.ToString());

    std::shared_ptr<arrow::Table> table;
    status = reader->ReadTable(&table);
    if (!status.ok())
        throw std::runtime_error("Parquet read failed: " + status.ToString());

    // Extract columns by name
    auto get_col = [&](const std::string& name) -> std::shared_ptr<arrow::ChunkedArray> {
        auto idx = table->schema()->GetFieldIndex(name);
        if (idx < 0) return nullptr;
        return table->column(idx);
    };

    auto ts_col   = get_col(cfg_.timestamp_col);
    auto bid_col  = get_col(cfg_.bid_col);
    auto ask_col  = get_col(cfg_.ask_col);
    auto last_col = get_col(cfg_.last_col);
    auto vol_col  = get_col(cfg_.volume_col);

    if (!ts_col || !bid_col || !ask_col)
        throw std::runtime_error("Required columns missing in parquet file");

    const std::int64_t nrows = table->num_rows();

    // Flatten chunked arrays for sequential access
    auto flatten = [&](std::shared_ptr<arrow::ChunkedArray> ca)
        -> std::shared_ptr<arrow::Array> {
        if (!ca) return nullptr;
        auto res = arrow::Concatenate(ca->chunks());
        if (!res.ok()) return nullptr;
        return *res;
    };

    auto ts_arr   = flatten(ts_col);
    auto bid_arr  = flatten(bid_col);
    auto ask_arr  = flatten(ask_col);
    auto last_arr = flatten(last_col);
    auto vol_arr  = flatten(vol_col);

    for (std::int64_t i = 0; i < nrows; ++i) {
        Tick t{};
        t.instrument_id = cfg_.default_instrument_id;

        if (ts_arr)   t.timestamp   = std::static_pointer_cast<arrow::Int64Array>(ts_arr)->Value(i);
        if (bid_arr)  t.bid_price   = std::static_pointer_cast<arrow::DoubleArray>(bid_arr)->Value(i);
        if (ask_arr)  t.ask_price   = std::static_pointer_cast<arrow::DoubleArray>(ask_arr)->Value(i);
        if (last_arr) t.last_price  = std::static_pointer_cast<arrow::DoubleArray>(last_arr)->Value(i);
        if (vol_arr)  t.volume      = std::static_pointer_cast<arrow::DoubleArray>(vol_arr)->Value(i);

        if (t.last_price == 0.0) t.last_price = t.mid_price();
        callback(t);
        ++rows_loaded_;
    }

    spdlog::debug("Parquet loader: read {} rows from {}", rows_loaded_, filepath);
}

void write_parquet(const std::string& filepath, const std::vector<Tick>& ticks) {
    if (ticks.empty()) return;

    // Build Arrow arrays
    arrow::Int64Builder  ts_builder;
    arrow::DoubleBuilder bid_builder, ask_builder, last_builder, vol_builder;

    for (const auto& t : ticks) {
        PARQUET_THROW_NOT_OK(ts_builder.Append(t.timestamp));
        PARQUET_THROW_NOT_OK(bid_builder.Append(t.bid_price));
        PARQUET_THROW_NOT_OK(ask_builder.Append(t.ask_price));
        PARQUET_THROW_NOT_OK(last_builder.Append(t.last_price));
        PARQUET_THROW_NOT_OK(vol_builder.Append(t.volume));
    }

    std::shared_ptr<arrow::Array> ts_arr, bid_arr, ask_arr, last_arr, vol_arr;
    PARQUET_THROW_NOT_OK(ts_builder.Finish(&ts_arr));
    PARQUET_THROW_NOT_OK(bid_builder.Finish(&bid_arr));
    PARQUET_THROW_NOT_OK(ask_builder.Finish(&ask_arr));
    PARQUET_THROW_NOT_OK(last_builder.Finish(&last_arr));
    PARQUET_THROW_NOT_OK(vol_builder.Finish(&vol_arr));

    auto schema = arrow::schema({
        arrow::field("timestamp",  arrow::int64()),
        arrow::field("bid_price",  arrow::float64()),
        arrow::field("ask_price",  arrow::float64()),
        arrow::field("last_price", arrow::float64()),
        arrow::field("volume",     arrow::float64()),
    });

    auto table = arrow::Table::Make(schema, {ts_arr, bid_arr, ask_arr, last_arr, vol_arr});

    auto outfile = arrow::io::FileOutputStream::Open(filepath);
    PARQUET_THROW_NOT_OK(outfile.status());

    PARQUET_THROW_NOT_OK(parquet::arrow::WriteTable(
        *table, arrow::default_memory_pool(), *outfile, 1 << 20));
}

} // namespace bt
