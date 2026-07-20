# TradingTerminal.App.Intermediate — Intermediate edition shell

**Path** `src/windows/Shell/TradingTerminal.App.Intermediate/` · **Edition** Intermediate · **Blast** leaf shell

Independent WPF composition root with the shared public tool surface plus credentialed brokers and
login forms. Keep `AddCredentialedLoginForms()` paired with `AddCredentialedBrokers()`; preserve
its additional development profiles.

Shared shell behavior is paired with `App.Basic`; follow
`RECIPES/shell-fix-editions.md`. A consuming overlay owns any additional shell copy and must be
handled under that workspace's guide.

**Build** `dotnet build TradingTerminal.Windows.Intermediate.slnf`

**Run** `dotnet run --project src/windows/Shell/TradingTerminal.App.Intermediate`

**Surface** `symbols/App.Intermediate.md`
