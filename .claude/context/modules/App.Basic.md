# TradingTerminal.App.Basic — Basic edition shell

**Path** `src/windows/Shell/TradingTerminal.App.Basic/` · **Edition** Basic · **Blast** leaf shell

Independent WPF composition root for the keyless edition: main window, strategy/plugin catalog,
activity log, archive UI, notifications, and tool wiring. It registers keyless brokers and login
forms only; do not add credentialed broker or `DevLive` composition here.

Shared shell behavior is paired with `App.Intermediate`; follow
`RECIPES/shell-fix-editions.md`. A consuming overlay owns any additional shell copy and must be
handled under that workspace's guide.

**Build** `dotnet build TradingTerminal.Windows.Basic.slnf`

**Run** `dotnet run --project src/windows/Shell/TradingTerminal.App.Basic`

**Surface** `symbols/App.Basic.md`
