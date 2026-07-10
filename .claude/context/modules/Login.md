# TradingTerminal.Login — sign-in window + credential store

**Path** `src/windows/Shell/TradingTerminal.Login/` · 3,847 LOC / 45 files · **Editions** B I P (Basic registers keyless forms only) · **Blast: med**

**Purpose.** Login window, per-broker login forms (`IBrokerLoginForm`), credential persistence,
services panel (probes sidecar/Docker/TWS/NT/Ollama with copy-command + re-check),
`ServiceDependencyViewModel`.

**DI.** `AddLogin()` (`symbols/Login.md` → extensions file line 27) registers keyless forms;
`AddCredentialedLoginForms()` (line 59) adds the rest. **RULE:** resolving
`IEnumerable<IBrokerLoginForm>` instantiates every registered form ⇒ `AddCredentialedLoginForms()`
without `AddCredentialedBrokers()` crashes the login window. Basic never calls it.

**Depends on** Core, UI. **Depended by** both shells.
**Surface** `symbols/Login.md` (445 lines — LoginViewModel 540 LOC, LoginWindow.xaml 646 LOC).

**Invariants.** No 2FA UI (TWS handles its own; the API socket has none — don't re-suggest).
Dev bypass via `DevOptions.BypassLogin`, not special-cased code paths.

**Tests** Tests.Headless `~Login`. **Common changes.** New broker form (pair with
`RECIPES/add-broker.md` step 6); services-panel probe additions.
