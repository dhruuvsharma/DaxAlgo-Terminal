# Tests — TradingTerminal.Tests + Tests.Headless (Windows tree)

**Paths** `tests/TradingTerminal.Tests/` (1,044 LOC — WPF-touching, `[WpfFact]`) and
`tests/TradingTerminal.Tests.Headless/` (11,248 LOC / 94 files — the bulk) · xUnit + FluentAssertions + NSubstitute.

**Find the right test file:** grep `index/Tests.md` by feature keyword — folders mirror source
areas (Quant/, MarketData/, Backtest/, brokers, strategies…). Headless references the 4 tape-heavy
strategy plugins + SamplePlugin as fixtures; Tests references App.Intermediate (shell smoke).

**Run narrow:** `dotnet test tests/TradingTerminal.Tests.Headless --filter "FullyQualifiedName~<Area>"`.
Postgres tests self-skip without Docker; 1 known flaky GPU test lives in the LINUX tree, not here.

**Conventions.** New engine/quant math ⇒ numeric tests required. Strategy plugins get kernel-level
tests (not UI). WPF-touching tests go in Tests with `[WpfFact]`, never in Headless.
`build-on-stop` builds but does NOT run tests — run the filtered suite yourself before claiming green.
