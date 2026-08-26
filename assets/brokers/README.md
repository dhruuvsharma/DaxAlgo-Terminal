# Broker logo assets

These marks identify third-party broker integrations in the login and API-usage UI. They were
retrieved through Google's favicon service using each broker's official domain — the original twelve on
2026-07-25, the rest on 2026-08-26 when the catalogue was widened. The listed sites identify the mark
owners; they are not a permission grant for reuse.

**The list is generated from `BrokerCatalog`**, which is also what the picker reads, so a mark on disk
and a broker in the product cannot drift apart. `BrokerCatalogTests` fails on an orphan file, and on two
files with identical bytes — the favicon service answers an unknown domain with a generic globe rather
than failing, so a wrong logo arrives looking exactly like a right one. Two were caught that way.

| Asset | Broker | Official domain |
|---|---|---|
| `5paisa.png` | 5paisa | [5paisa.com](https://5paisa.com) |
| `alice-blue.png` | Alice Blue | [aliceblueonline.com](https://aliceblueonline.com) |
| `alpaca.png` | Alpaca | [alpaca.markets](https://alpaca.markets) |
| `angel-one.png` | Angel One | [angelone.in](https://angelone.in) |
| `binance.png` | Binance | [binance.com](https://binance.com) |
| `bitget.png` | Bitget | [bitget.com](https://bitget.com) |
| `bithumb.png` | Bithumb | [bithumb.com](https://bithumb.com) |
| `bybit.png` | Bybit | [bybit.com](https://bybit.com) |
| `charles-schwab.png` | Charles Schwab | [schwab.com](https://schwab.com) |
| `coinbase.png` | Coinbase | [coinbase.com](https://coinbase.com) |
| `cqg.png` | CQG | [cqg.com](https://cqg.com) |
| `crypto-com.png` | Crypto.com | [crypto.com](https://crypto.com) |
| `ctrader.png` | cTrader | [ctrader.com](https://ctrader.com) |
| `das-trader.png` | DAS Trader | [dastrader.com](https://dastrader.com) |
| `deribit.png` | Deribit | [deribit.com](https://deribit.com) |
| `dhan.png` | Dhan | [dhan.co](https://dhan.co) |
| `dukascopy.png` | Dukascopy | [dukascopy.com](https://dukascopy.com) |
| `etrade.png` | E*TRADE | [etrade.com](https://etrade.com) |
| `forex-com.png` | FOREX.com | [forex.com](https://forex.com) |
| `futu.png` | Futu / moomoo | [futunn.com](https://futunn.com) |
| `fyers.png` | Fyers | [fyers.in](https://fyers.in) |
| `gate-io.png` | Gate.io | [gate.io](https://gate.io) |
| `gemini.png` | Gemini | [gemini.com](https://gemini.com) |
| `groww.png` | Groww | [groww.in](https://groww.in) |
| `hyperliquid.png` | Hyperliquid | [hyperliquid.xyz](https://hyperliquid.xyz) |
| `ig-group.png` | IG | [ig.com](https://ig.com) |
| `interactive-brokers.png` | Interactive Brokers | [interactivebrokers.com](https://interactivebrokers.com) |
| `ironbeam.png` | Ironbeam | [ironbeam.com](https://ironbeam.com) |
| `kraken.png` | Kraken | [kraken.com](https://kraken.com) |
| `kucoin.png` | KuCoin | [kucoin.com](https://kucoin.com) |
| `london-strategic-edge.png` | London Strategic Edge | [londonstrategicedge.com](https://londonstrategicedge.com) |
| `metatrader.png` | MetaTrader 4 / 5 | [metatrader5.com](https://metatrader5.com) |
| `ninjatrader.png` | NinjaTrader | [ninjatrader.com](https://ninjatrader.com) |
| `oanda.png` | OANDA | [oanda.com](https://oanda.com) |
| `okx.png` | OKX | [okx.com](https://okx.com) |
| `rithmic.png` | Rithmic | [rithmic.com](https://rithmic.com) |
| `saxo-bank.png` | Saxo Bank | [home.saxo](https://home.saxo) |
| `swissquote.png` | Swissquote | [swissquote.com](https://swissquote.com) |
| `tastytrade.png` | tastytrade | [tastytrade.com](https://tastytrade.com) |
| `tiger-brokers.png` | Tiger Brokers | [itiger.com](https://itiger.com) |
| `tradestation.png` | TradeStation | [tradestation.com](https://tradestation.com) |
| `tradier.png` | Tradier | [tradier.com](https://tradier.com) |
| `tradovate.png` | Tradovate | [tradovate.com](https://tradovate.com) |
| `upbit.png` | Upbit | [upbit.com](https://upbit.com) |
| `upstox.png` | Upstox | [upstox.com](https://upstox.com) |
| `zerodha.png` | Zerodha | [zerodha.com](https://zerodha.com) |

## Catalogued without a mark

| Broker | Official domain | Why |
|---|---|---|
| ICICI Direct | [icicidirect.com](https://icicidirect.com) | no mark available — the picker shows the text fallback |

All names and marks are trademarks of their respective owners. Their inclusion identifies compatible
integrations and does not imply endorsement. Keep the text fallback when adding a broker whose mark is
not available or approved.
