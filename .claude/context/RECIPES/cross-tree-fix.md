# RECIPE — cross-tree fix (Windows + Linux, applied twice)

The trees share no code (ADR-0001) but mirror namespaces. A backend fix (Core / MarketData /
Infrastructure / Backtest.Engine / strategy math) usually applies to both.

Procedure:
1. Confirm scope with Dhruv if only one tree was named (PROTOCOL hard stop #5).
2. Fix the Windows copy first (this layer indexes Windows).
3. Find the mirror: `rg --files src/linux -g "<FileName>"` — do NOT assume the same group path;
   the Linux tree is organized independently. If the file doesn't exist there, the feature may be
   un-mirrored — check `docs/LINUX-MIRROR-BACKLOG.md` and add an entry instead of force-porting.
4. Port the edit; adapt WPF-isms (Dispatcher, XAML) to Avalonia equivalents — backend code should
   port verbatim.
5. Build both: `dotnet build TradingTerminal.Windows.Intermediate.slnf` (or full slnx) AND
   `dotnet build TradingTerminal.Linux.slnx`.
6. Test both: `tests/TradingTerminal.Tests.Headless` and `tests/linux/TradingTerminal.Tests.Headless`
   (Linux headless runs on Windows; 1 known flaky GPU test).
7. If deferring the Linux side: append it to `docs/LINUX-MIRROR-BACKLOG.md` in the same commit —
   that file is the debt ledger.
