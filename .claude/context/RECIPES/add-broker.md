# RECIPE — add a broker (pairs with the `add-broker` + `broker-gotchas` skills)

Mirror `Infrastructure/Binance/` (REST+WS, no SDK) or `Infrastructure/IronBeam/` (auth'd REST+WS).

Checklist:
1. `src/windows/Core/TradingTerminal.Core/Brokers/BrokerKind.cs:3` — new enum member.
2. Options record in Core (mirror `AlpacaOptions` — see `symbols/Core-Brokers.md` / `Core-Configuration.md`).
3. Client: `src/windows/Pipeline/TradingTerminal.Infrastructure/<Broker>/Real<Broker>Client.cs : IBrokerClient`
   (seam: `Core/MarketData/IBrokerClient.cs:16` — digest in `symbols.md`). `ConnectAsync()` takes no
   params; read own `IOptions<>`. Trade tape optional — throw `NotSupportedException` if absent.
4. DI: `Infrastructure/DependencyInjection.cs` — `AddCredentialedBrokers` (line ~100) or
   `AddKeylessBrokers` (line ~233) depending on auth model.
5. Edition policy: `Core/Configuration/BrokerEditionPolicy.cs:16` — add to `Keyless` or `Credentialed` list.
6. Login: form in `TradingTerminal.Login` (grep `AddLogin` / `AddCredentialedLoginForms` in `symbols/Login.md`).
   RULE: credentialed form registered ⇒ `AddCredentialedBrokers()` must be in the same composition
   (unpaired form crashes the login window). Basic shell must NOT get a credentialed form.
7. Instrument catalog entries if the broker serves the 37-instrument `SignalInstrumentCatalog`.

Invariants: no SDK/HTTP types above Infrastructure; provenance filled on every event; reconnect
backoff 1s→30s; `IObservable<ConnectionState>`; marshal to UI only inside MarketDataRepository.
Build: `dotnet build TradingTerminal.Windows.slnx` (Infrastructure is cross-cutting).
Test: `--filter "FullyQualifiedName~<Broker>"`. Update: `docs/brokers.md`, `symbols/Infrastructure-<Broker>.md` (regen), `deps.json` n/a, issue tick.
