# Tests — TradingTerminal.Tests + Tests.Headless (Windows tree)

**Paths** `tests/TradingTerminal.Tests/` (WPF-touching, `[WpfFact]`) and
`tests/TradingTerminal.Tests.Headless/` (the bulk) · xUnit + FluentAssertions + NSubstitute.

**Find the right test file:** grep `index/Tests.md` by feature keyword — folders mirror source
areas (Quant/, MarketData/, Backtest/, brokers, authoring). Headless uses SamplePlugin as the
in-tree plugin-contract fixture; Tests references App.Intermediate for WPF shell coverage.

**Run narrow:** `dotnet test tests/TradingTerminal.Tests.Headless --filter "FullyQualifiedName~<Area>"`.
Postgres tests self-skip without Docker; 1 known flaky GPU test lives in the LINUX tree, not here.

**Conventions.** New engine/quant math ⇒ numeric tests required. External strategy plugins own their
kernel tests. WPF-touching host tests go in Tests with `[WpfFact]`, never in Headless.
`build-on-stop` builds but does NOT run tests — run the filtered suite yourself before claiming green.
