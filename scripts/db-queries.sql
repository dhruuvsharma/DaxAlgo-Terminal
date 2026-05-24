-- DaxAlgo Terminal — canonical market-data store query book
-- ---------------------------------------------------------------------------
-- Works against both backends: PostgreSQL/TimescaleDB and embedded SQLite.
-- The schema is identical; only the Postgres-specific section at the bottom
-- depends on TimescaleDB internals.
--
-- Tables (one row per real-world thing — never per-broker):
--   instruments         canonical broker-neutral identity (one row per instrument)
--   instrument_aliases  per-broker symbology mapping → instrument id
--   quotes              normalized L1 quotes (every Tick the pipeline saw, with provenance)
--   trades              normalized trade prints (last)
--   bars                normalized OHLCV bars (historical cache + live aggregates)
--
-- BrokerKind enum (the `source` / `broker` columns):
--   0 = InteractiveBrokers, 1 = NinjaTrader, 2 = CTrader, 3 = Alpaca
--
-- AssetClass enum (instruments.asset_class):
--   0 = Unknown, 1 = Equity, 2 = Forex, 3 = Future, 4 = Option, 5 = Crypto, 6 = Index
--
-- BarSize enum (bars.bar_size):
--   0 = OneMinute, 1 = ThreeMinutes, 2 = FiveMinutes, 3 = FifteenMinutes,
--   4 = OneHour, 5 = OneDay
-- ---------------------------------------------------------------------------


-- ===========================================================================
-- 1. PIPELINE HEALTH — is data actually landing?
-- ===========================================================================

-- Headcount across every table. Run this first.
SELECT 'instruments' AS table, count(*) AS rows FROM instruments
UNION ALL SELECT 'instrument_aliases', count(*) FROM instrument_aliases
UNION ALL SELECT 'quotes',             count(*) FROM quotes
UNION ALL SELECT 'trades',             count(*) FROM trades
UNION ALL SELECT 'bars',               count(*) FROM bars;


-- Postgres: are quotes still arriving? Run twice 10s apart; the second should be larger.
-- (SQLite: replace `now() - interval '1 minute'` with `unixepoch('now', '-1 minute') * 1000000`
--  since SQLite stores event_time as epoch microseconds, not timestamptz.)
SELECT count(*) AS quotes_last_minute
FROM quotes
WHERE event_time > now() - interval '1 minute';


-- ===========================================================================
-- 2. INSTRUMENT REGISTRY — what's the canonical universe?
-- ===========================================================================

-- All canonical instruments, newest first.
SELECT id, canonical_symbol, asset_class, exchange, currency, tick_size, multiplier
FROM instruments
ORDER BY id DESC
LIMIT 100;


-- How many instruments per asset class.
SELECT
    CASE asset_class
        WHEN 0 THEN 'Unknown' WHEN 1 THEN 'Equity'  WHEN 2 THEN 'Forex'
        WHEN 3 THEN 'Future'  WHEN 4 THEN 'Option'  WHEN 5 THEN 'Crypto'
        WHEN 6 THEN 'Index'
    END AS asset_class,
    count(*) AS instruments
FROM instruments
GROUP BY asset_class
ORDER BY instruments DESC;


-- Per-broker alias count — how much of each broker's universe the discovery service has registered.
SELECT
    CASE broker
        WHEN 0 THEN 'InteractiveBrokers' WHEN 1 THEN 'NinjaTrader'
        WHEN 2 THEN 'CTrader'            WHEN 3 THEN 'Alpaca'
    END AS broker,
    count(*) AS aliases
FROM instrument_aliases
GROUP BY broker
ORDER BY aliases DESC;


-- Find a specific symbol (case-insensitive). Replace 'AAPL' as needed.
SELECT i.id, i.canonical_symbol, i.asset_class, i.exchange,
       a.broker, a.broker_symbol, a.broker_native_id
FROM instruments i
LEFT JOIN instrument_aliases a ON a.instrument_id = i.id
WHERE upper(i.canonical_symbol) = upper('AAPL')
ORDER BY a.broker;


-- ===========================================================================
-- 3. LIVE QUOTES — the canonical Tick stream the pipeline persisted
-- ===========================================================================

-- Last 20 quotes for a given symbol. Replace 'AAPL'.
SELECT q.event_time, q.bid, q.ask, q.bid_size, q.ask_size,
       q.source AS broker, q.seq, q.approx_time
FROM quotes q JOIN instruments i ON i.id = q.instrument_id
WHERE i.canonical_symbol = 'AAPL'
ORDER BY q.event_time DESC
LIMIT 20;


-- Per-symbol activity summary — counts + time window.
SELECT i.canonical_symbol, count(*) AS quotes,
       min(q.event_time) AS first_seen, max(q.event_time) AS last_seen,
       max(q.event_time) - min(q.event_time) AS span
FROM quotes q JOIN instruments i ON i.id = q.instrument_id
GROUP BY i.canonical_symbol
ORDER BY quotes DESC
LIMIT 25;


-- Quote provenance — how many quotes carry approximate event time (broker stamps arrival,
-- not exchange time). High approx_time fraction means the broker isn't giving us exchange
-- timestamps; consumers should treat event_time as a lower bound rather than exact.
SELECT
    CASE source
        WHEN 0 THEN 'InteractiveBrokers' WHEN 1 THEN 'NinjaTrader'
        WHEN 2 THEN 'CTrader'            WHEN 3 THEN 'Alpaca'
    END AS broker,
    count(*) AS total,
    sum(CASE WHEN approx_time THEN 1 ELSE 0 END) AS approximate,
    round(100.0 * sum(CASE WHEN approx_time THEN 1 ELSE 0 END) / count(*), 1) AS pct_approximate
FROM quotes
GROUP BY source
ORDER BY total DESC;


-- Ingest latency — gap between exchange event time and our local ingest time.
-- Anything sustained > 200ms means something is buffering (slow broker, network, GC).
-- (Postgres only — SQLite stores micros, do `(ingest_time - event_time) / 1000.0` for ms.)
SELECT i.canonical_symbol,
       count(*) AS quotes,
       round(avg(extract(epoch FROM q.ingest_time - q.event_time) * 1000)::numeric, 1) AS avg_latency_ms,
       round(percentile_cont(0.5)  WITHIN GROUP (ORDER BY extract(epoch FROM q.ingest_time - q.event_time) * 1000)::numeric, 1) AS p50_ms,
       round(percentile_cont(0.99) WITHIN GROUP (ORDER BY extract(epoch FROM q.ingest_time - q.event_time) * 1000)::numeric, 1) AS p99_ms
FROM quotes q JOIN instruments i ON i.id = q.instrument_id
WHERE q.event_time > now() - interval '10 minutes'
  AND NOT q.approx_time            -- skip brokers stamping arrival time
GROUP BY i.canonical_symbol
ORDER BY quotes DESC
LIMIT 25;


-- ===========================================================================
-- 4. HISTORICAL BARS — the Step 2 cache the repository writes through
-- ===========================================================================

-- Most recent 30 bars for a given symbol + size. Replace 'AAPL' + bar_size = 0 (1-min).
SELECT b.open_time, b.open, b.high, b.low, b.close, b.volume, b.is_final
FROM bars b JOIN instruments i ON i.id = b.instrument_id
WHERE i.canonical_symbol = 'AAPL' AND b.bar_size = 0
ORDER BY b.open_time DESC
LIMIT 30;


-- Cache coverage — for every (symbol, size) the cache holds, count + freshness.
SELECT i.canonical_symbol,
       CASE b.bar_size
           WHEN 0 THEN '1m' WHEN 1 THEN '3m' WHEN 2 THEN '5m'
           WHEN 3 THEN '15m' WHEN 4 THEN '1h' WHEN 5 THEN '1d'
       END AS size,
       count(*) AS bars,
       min(b.open_time) AS oldest, max(b.open_time) AS newest
FROM bars b JOIN instruments i ON i.id = b.instrument_id
GROUP BY i.canonical_symbol, b.bar_size
ORDER BY i.canonical_symbol, b.bar_size;


-- In-progress bars (is_final = false). These are the streaming bars the live ingest
-- upserts on each tick — should be very few rows (latest of each (symbol, size, bucket)).
SELECT i.canonical_symbol, b.bar_size, b.open_time, b.close, b.volume
FROM bars b JOIN instruments i ON i.id = b.instrument_id
WHERE b.is_final = false
ORDER BY b.open_time DESC;


-- ===========================================================================
-- 5. TRADE PRINTS — populated by brokers that publish a trade stream (Alpaca today)
-- ===========================================================================

SELECT t.event_time, t.price, t.size,
       CASE t.aggressor WHEN 0 THEN 'Unknown' WHEN 1 THEN 'Buy' WHEN 2 THEN 'Sell' END AS aggressor,
       t.source, t.seq, t.approx_time
FROM trades t JOIN instruments i ON i.id = t.instrument_id
WHERE i.canonical_symbol = 'AAPL'
ORDER BY t.event_time DESC
LIMIT 50;


-- ===========================================================================
-- 6. POSTGRES / TIMESCALEDB ONLY — hypertable internals + storage
-- ===========================================================================

-- Hypertable inventory: chunk count + total size per table.
SELECT hypertable_name, num_chunks,
       pg_size_pretty(hypertable_size(
           format('%I.%I', hypertable_schema, hypertable_name)::regclass)) AS size
FROM timescaledb_information.hypertables
ORDER BY hypertable_name;


-- Chunk-level detail for quotes — useful when investigating why a query is slow.
SELECT chunk_name, range_start, range_end,
       pg_size_pretty(pg_total_relation_size(format('%I.%I', chunk_schema, chunk_name)::regclass)) AS size
FROM timescaledb_information.chunks
WHERE hypertable_name = 'quotes'
ORDER BY range_start DESC
LIMIT 20;


-- Index usage — which indexes are doing actual work vs sitting cold.
SELECT schemaname || '.' || relname AS table,
       indexrelname AS index,
       idx_scan, idx_tup_read
FROM pg_stat_user_indexes
WHERE schemaname = 'public'
ORDER BY idx_scan DESC;
