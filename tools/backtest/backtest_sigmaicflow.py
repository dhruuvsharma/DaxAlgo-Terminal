#!/usr/bin/env python3
"""
Backtest the SigmaIcFlow (Σ⁻¹·IC Order-Flow Optimizer) strategy on crypto + index futures.

This is a thin Python *harness*: it fetches market data, writes it as parquet in the schema the
C# backtest engine reads, then drives the real engine through `daxalgo-backtest.exe run
--strategy sigmaIcFlow`. It does NOT reimplement the strategy — it tests the actual one.

Data paths
----------
* Crypto (BTC/ETH, default Binance): downloads the REAL aggregated trade tape via the public
  Binance REST API. Writes a quote parquet (bid/ask synthesised around each trade) + a trade
  parquet, both replayed by the engine → trade-tape quality q = 1.0 (the strategy's core flow
  signals get genuine aggressor flow). This is the strong, faithful path.

* Index futures (MES/MNQ/ES/NQ, via Yahoo Finance): there is no free real tick tape, so we pull
  1-minute OHLCV bars and synthesise a quote path (O→H→L→C) from each bar. No trade tape is
  written, so the engine runs its honest synthetic-L1 fallback (q ≈ 0.4). Treat futures numbers as
  INDICATIVE, not a real microstructure backtest. Yahoo only serves ~7 days of 1-minute history.

The strategy warms up slowly by design (500-sample isotonic calibration before it leaves bootstrap
mode), so use a long enough window — a day or more of crypto tape is recommended to see real trades.

Usage
-----
    python backtest_sigmaicflow.py --instruments BTC,ETH,MES,MNQ --hours 24 --days 5
    python backtest_sigmaicflow.py --list
    python backtest_sigmaicflow.py --instruments BTC --hours 6 --out ./bt

Crypto needs no third-party packages (stdlib urllib + CSV). Futures need yfinance. See requirements.txt.
The C# CLI must be built first:  dotnet build src/TradingTerminal.Backtest.Cli
"""
from __future__ import annotations

import argparse
import csv
import glob
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import urllib.error
import urllib.parse
import urllib.request

EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


@dataclass
class Instrument:
    key: str                 # short key used on the command line (BTC, MES, …)
    symbol: str              # symbol passed to the backtest CLI
    kind: str                # "crypto" | "future"
    source_symbol: str       # Binance pair or Yahoo ticker
    tick_size: float
    multiplier: float        # CME contract multiplier (1 for crypto)
    fee_bps: float           # flat taker fee in bps of notional
    size_scale: float = 1.0  # crypto: scales fractional qty → integer tape sizes (z-scored signals are scale-free)


# Defaults target CME micro/mini index futures + the two largest crypto pairs.
INSTRUMENTS: dict[str, Instrument] = {
    "BTC": Instrument("BTC", "BTCUSDT", "crypto", "BTCUSDT", tick_size=0.1,  multiplier=1.0, fee_bps=4.0, size_scale=1e4),
    "ETH": Instrument("ETH", "ETHUSDT", "crypto", "ETHUSDT", tick_size=0.01, multiplier=1.0, fee_bps=4.0, size_scale=1e3),
    # Micros / minis. Yahoo serves the front-month continuous via the =F suffix.
    "MES": Instrument("MES", "MES", "future", "MES=F", tick_size=0.25, multiplier=5.0,  fee_bps=0.2),
    "MNQ": Instrument("MNQ", "MNQ", "future", "MNQ=F", tick_size=0.25, multiplier=2.0,  fee_bps=0.2),
    "ES":  Instrument("ES",  "ES",  "future", "ES=F",  tick_size=0.25, multiplier=50.0, fee_bps=0.1),
    "NQ":  Instrument("NQ",  "NQ",  "future", "NQ=F",  tick_size=0.25, multiplier=20.0, fee_bps=0.1),
}


# ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

def to_micros(ms: int) -> int:
    """Epoch milliseconds → epoch microseconds (the parquet timestamp unit)."""
    return int(ms) * 1000


def log(msg: str) -> None:
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


def find_cli(explicit: str | None) -> str:
    if explicit:
        if not os.path.isfile(explicit):
            sys.exit(f"--cli path not found: {explicit}")
        return explicit
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.abspath(os.path.join(here, "..", ".."))
    pattern = os.path.join(repo, "src", "TradingTerminal.Backtest.Cli", "bin", "**", "daxalgo-backtest.exe")
    hits = glob.glob(pattern, recursive=True)
    # Prefer Release over Debug when both exist.
    hits.sort(key=lambda p: (0 if os.sep + "Release" + os.sep in p else 1, p))
    if not hits:
        sys.exit("daxalgo-backtest.exe not found. Build it first:\n"
                 "    dotnet build src/TradingTerminal.Backtest.Cli\n"
                 "or pass --cli <path to daxalgo-backtest.exe>.")
    return hits[0]


# ── Binance real trade tape ────────────────────────────────────────────────────────────────────

def _http_get_json(url: str, params: dict, retries: int = 6):
    q = urllib.parse.urlencode(params)
    full = f"{url}?{q}"
    for attempt in range(retries):
        try:
            req = urllib.request.Request(full, headers={"User-Agent": "daxalgo-backtest/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code in (418, 429):  # rate limited — back off
                wait = 2 ** attempt
                log(f"  rate-limited ({e.code}); backing off {wait}s")
                time.sleep(wait)
                continue
            raise
        except (urllib.error.URLError, TimeoutError) as e:
            wait = 1 + attempt
            log(f"  network error ({e}); retry in {wait}s")
            time.sleep(wait)
    raise RuntimeError(f"GET failed after {retries} retries: {full}")


def fetch_binance_aggtrades(base: str, symbol: str, start_ms: int, end_ms: int, max_trades: int):
    """Returns [(ts_ms, price, qty, is_buyer_maker)] over [start_ms, end_ms], paginated by aggId."""
    url = f"{base.rstrip('/')}/api/v3/aggTrades"
    out: list[tuple[int, float, float, bool]] = []
    params = {"symbol": symbol, "startTime": start_ms, "limit": 1000}
    while True:
        batch = _http_get_json(url, params)
        if not batch:
            break
        for d in batch:
            if d["T"] > end_ms:
                return out
            out.append((int(d["T"]), float(d["p"]), float(d["q"]), bool(d["m"])))
            if len(out) >= max_trades:
                log(f"  reached --max-trades cap ({max_trades})")
                return out
        last = batch[-1]
        if len(batch) < 1000 and last["T"] >= end_ms:
            break
        params = {"symbol": symbol, "fromId": last["a"] + 1, "limit": 1000}
        if len(out) % 50000 < 1000:
            log(f"  …{len(out):,} trades ({datetime.fromtimestamp(last['T']/1000, timezone.utc):%H:%M:%S})")
        time.sleep(0.20)  # gentle on the public endpoint
    return out


def write_crypto_csv(inst: Instrument, trades, out_dir: str, spread_bps: float):
    """Writes quotes.csv (synthesised L1 around each trade) + trades.csv (the real tape). CSV is the
    portable interop format the engine's CsvTick/CsvTrade readers consume."""
    os.makedirs(out_dir, exist_ok=True)
    quotes_path = os.path.join(out_dir, "quotes.csv")
    trades_path = os.path.join(out_dir, "trades.csv")

    with open(quotes_path, "w", encoding="utf-8", newline="") as q, \
         open(trades_path, "w", encoding="utf-8", newline="") as t:
        q.write("timestamp_micros,bid,ask,bid_size,ask_size\n")
        t.write("timestamp_micros,price,size,aggressor\n")
        for (ts_ms, price, qty, is_buyer_maker) in trades:
            micros = to_micros(ts_ms)
            half = max(inst.tick_size / 2.0, price * spread_bps / 1e4)
            # One quote per trade at the same timestamp (the merge yields the quote first on a tie,
            # so the strategy's spread/mid is current when it sees the print).
            q.write(f"{micros},{price - half:.8f},{price + half:.8f},1,1\n")
            # Real trade: aggressor = Sell (2) when the buyer was the maker, else Buy (1).
            size = max(1, int(round(qty * inst.size_scale)))
            t.write(f"{micros},{price:.8f},{size},{2 if is_buyer_maker else 1}\n")
    return quotes_path, trades_path


def write_quotes_csv(path, rows):
    """rows: iterable of (micros, bid, ask). Sizes default to 1 (no real L1 depth)."""
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("timestamp_micros,bid,ask,bid_size,ask_size\n")
        for micros, bid, ask in rows:
            f.write(f"{micros},{bid:.6f},{ask:.6f},1,1\n")


# ── Yahoo futures bars → synthetic quote path ────────────────────────────────────────────────────

def fetch_futures_quotes(inst: Instrument, days: int, out_dir: str, spread_ticks: float):
    """Pulls 1m bars from Yahoo and expands each into an O→H→L→C quote path. Quotes only (no tape)."""
    try:
        import yfinance as yf
    except ImportError:
        sys.exit("Backtesting futures needs yfinance:  pip install yfinance")

    log(f"  yfinance {inst.source_symbol}: {days}d of 1m bars")
    df = yf.download(inst.source_symbol, period=f"{min(days,7)}d", interval="1m",
                     auto_adjust=False, prepost=True, progress=False)
    if df is None or len(df) == 0:
        log(f"  no Yahoo data for {inst.source_symbol} — skipping")
        return None
    # yfinance may return MultiIndex columns for a single ticker; flatten.
    cols = {c[0] if isinstance(c, tuple) else c: c for c in df.columns}

    def col(name):
        return df[cols[name]]

    os.makedirs(out_dir, exist_ok=True)
    quotes_path = os.path.join(out_dir, "quotes.csv")
    half = spread_ticks * inst.tick_size / 2.0
    rows = []

    o, h, l, c = col("Open"), col("High"), col("Low"), col("Close")
    for idx in range(len(df)):
        t = df.index[idx]
        # Normalise the bar's start time to UTC epoch ms.
        ts = t.to_pydatetime()
        if ts.tzinfo is None:
            ts = ts.replace(tzinfo=timezone.utc)
        base_ms = int((ts.astimezone(timezone.utc) - EPOCH).total_seconds() * 1000)
        ov, hv, lv, cv = float(o.iloc[idx]), float(h.iloc[idx]), float(l.iloc[idx]), float(c.iloc[idx])
        if any(v != v for v in (ov, hv, lv, cv)):  # NaN guard
            continue
        # O at +0s, H at +20s, L at +40s, C at +59s — a crude but monotone-in-time path.
        for off_s, px in ((0, ov), (20, hv), (40, lv), (59, cv)):
            rows.append((to_micros(base_ms + off_s * 1000), px - half, px + half))

    if not rows:
        return None
    write_quotes_csv(quotes_path, rows)
    return quotes_path


# ── Drive the C# engine ──────────────────────────────────────────────────────────────────────────

def run_backtest(cli: str, inst: Instrument, quotes: str, trades: str | None,
                 starting_cash: float, out_dir: str) -> dict:
    args = [cli, "run", "--strategy", "sigmaIcFlow", "--symbol", inst.symbol,
            "--data", quotes, "--tick-size", str(inst.tick_size),
            "--multiplier", str(inst.multiplier), "--starting-cash", str(starting_cash),
            "--output", out_dir]
    if trades:
        args += ["--trades", trades]
    if inst.fee_bps:
        args += ["--fee-bps", str(inst.fee_bps)]
    proc = subprocess.run(args, capture_output=True, text=True)
    if proc.returncode != 0:
        log(f"  CLI failed (exit {proc.returncode}):\n{proc.stdout}\n{proc.stderr}")
        return {}
    summary_path = os.path.join(out_dir, "summary.json")
    if not os.path.isfile(summary_path):
        log("  no summary.json produced")
        return {}
    with open(summary_path, encoding="utf-8") as f:
        return json.load(f)


def summarise(inst: Instrument, mode: str, summary: dict) -> dict:
    s = (summary or {}).get("Stats") or {}
    return {
        "instrument": inst.symbol,
        "kind": inst.kind,
        "tape": mode,  # "real" | "synthetic-L1"
        "trades": s.get("TradeCount", 0),
        "total_return": s.get("TotalReturn", 0.0),
        "sharpe": s.get("Sharpe", 0.0),
        "sortino": s.get("Sortino", 0.0),
        "max_drawdown": s.get("MaxDrawdown", 0.0),
        "win_rate": s.get("WinRate", 0.0),
        "profit_factor": s.get("ProfitFactor", 0.0),
        "pnl": (summary or {}).get("TotalPnl", 0.0),
    }


def print_table(rows: list[dict]) -> None:
    if not rows:
        print("\nNo results.")
        return
    hdr = ("Instrument", "Tape", "Trades", "Return", "Sharpe", "MaxDD", "Win%", "PF", "PnL")
    print("\n" + "=" * 92)
    print(f"{hdr[0]:<11}{hdr[1]:<14}{hdr[2]:>7}{hdr[3]:>10}{hdr[4]:>8}{hdr[5]:>9}{hdr[6]:>8}{hdr[7]:>7}{hdr[8]:>14}")
    print("-" * 92)
    for r in rows:
        print(f"{r['instrument']:<11}{r['tape']:<14}{r['trades']:>7}"
              f"{r['total_return']*100:>9.2f}%{r['sharpe']:>8.2f}{r['max_drawdown']*100:>8.2f}%"
              f"{r['win_rate']*100:>7.1f}%{r['profit_factor']:>7.2f}{r['pnl']:>14.2f}")
    print("=" * 92)


def main() -> int:
    p = argparse.ArgumentParser(description="Backtest SigmaIcFlow on crypto + index futures.")
    p.add_argument("--instruments", default="BTC,ETH,MES,MNQ",
                   help="Comma list from: " + ",".join(INSTRUMENTS))
    p.add_argument("--hours", type=float, default=24.0, help="Crypto lookback window in hours (default 24).")
    p.add_argument("--days", type=int, default=5, help="Futures lookback in days (Yahoo caps 1m at ~7, default 5).")
    p.add_argument("--out", default="./bt-sigmaicflow", help="Output directory.")
    p.add_argument("--cli", default=None, help="Path to daxalgo-backtest.exe (auto-detected if omitted).")
    p.add_argument("--starting-cash", type=float, default=100_000.0)
    p.add_argument("--max-trades", type=int, default=2_000_000, help="Crypto: cap on fetched aggTrades per symbol.")
    p.add_argument("--spread-bps", type=float, default=1.0, help="Crypto synthesised half-spread, bps of price.")
    p.add_argument("--spread-ticks", type=float, default=1.0, help="Futures synthesised spread, in ticks.")
    p.add_argument("--binance-base", default="https://api.binance.com",
                   help="Binance REST base (try https://data-api.binance.vision if geo-blocked).")
    p.add_argument("--list", action="store_true", help="List known instruments and exit.")
    args = p.parse_args()

    if args.list:
        for k, i in INSTRUMENTS.items():
            print(f"  {k:<4} {i.symbol:<8} {i.kind:<7} src={i.source_symbol:<8} tick={i.tick_size} mult={i.multiplier}")
        return 0

    keys = [k.strip().upper() for k in args.instruments.split(",") if k.strip()]
    unknown = [k for k in keys if k not in INSTRUMENTS]
    if unknown:
        sys.exit(f"Unknown instrument(s): {unknown}. Known: {list(INSTRUMENTS)}")

    cli = find_cli(args.cli)
    log(f"CLI: {cli}")
    os.makedirs(args.out, exist_ok=True)

    now = datetime.now(timezone.utc)
    rows: list[dict] = []

    for key in keys:
        inst = INSTRUMENTS[key]
        work = os.path.join(args.out, inst.symbol)
        os.makedirs(work, exist_ok=True)
        log(f"=== {inst.symbol} ({inst.kind}) ===")
        try:
            if inst.kind == "crypto":
                end_ms = int((now - EPOCH).total_seconds() * 1000)
                start_ms = int(((now - timedelta(hours=args.hours)) - EPOCH).total_seconds() * 1000)
                log(f"  fetching Binance {inst.source_symbol} tape, last {args.hours:g}h…")
                trades = fetch_binance_aggtrades(args.binance_base, inst.source_symbol,
                                                 start_ms, end_ms, args.max_trades)
                if not trades:
                    log("  no trades fetched — skipping")
                    continue
                log(f"  fetched {len(trades):,} trades; writing CSV…")
                quotes_path, trades_path = write_crypto_csv(inst, trades, work, args.spread_bps)
                summary = run_backtest(cli, inst, quotes_path, trades_path, args.starting_cash, work)
                rows.append(summarise(inst, "real", summary))
            else:
                quotes_path = fetch_futures_quotes(inst, args.days, work, args.spread_ticks)
                if not quotes_path:
                    continue
                summary = run_backtest(cli, inst, quotes_path, None, args.starting_cash, work)
                rows.append(summarise(inst, "synthetic-L1", summary))
            r = rows[-1]
            log(f"  done: trades={r['trades']} return={r['total_return']*100:.2f}% sharpe={r['sharpe']:.2f}")
        except Exception as e:  # noqa: BLE001 — one bad symbol shouldn't kill the run
            log(f"  ERROR on {inst.symbol}: {e}")

    print_table(rows)
    if rows:
        csv_path = os.path.join(args.out, "summary.csv")
        with open(csv_path, "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
            w.writeheader()
            w.writerows(rows)
        log(f"Wrote {csv_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
