---
name: add-notifier
description: Recipe for adding a new notification transport (e.g. Slack, Email, SMS) to DaxAlgo Terminal's INotificationTransport seam, alongside the existing Telegram (Bot API) and Discord (channel webhook) transports. Use when the user asks for a new notifier, fanout target, alert channel, or webhook integration. Covers transport interface, options binding, settings persistence, hot-reload, and the dispatcher auto-discovery contract.
---

# Add a Notifier Transport

Notifications flow: strategies → `INotificationPublisher` (Core) → bounded `Channel<>` → hosted `NotificationDispatcher` (Infrastructure) → all enabled `INotificationTransport`s.

Adding a transport = one new file + a few config touch-points. The dispatcher auto-discovers via `IEnumerable<INotificationTransport>`.

## Recipe

1. **New folder** `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/<Channel>/` (e.g. `Slack/`).
2. **Implement `INotificationTransport`** — see `TelegramTransport.cs` and `DiscordTransport.cs` for shape. The contract is async, takes a `StrategyNotification`, returns `Task`. Failures must be caught and logged — never throw out of `SendAsync`; one bad transport must not block the others.
3. **Options class** — `<Channel>Options` (e.g. `SlackOptions { string WebhookUrl, bool Enabled }`).
4. **Add to `NotificationsOptions`** — the parent record/class that groups all transport configs. Add a `SlackOptions Slack` property.
5. **Persist in `NotificationsUserFile.Save`** — the JSON written to `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` must include the new section. Add load + save lines.
6. **Surface in `NotificationsSettingsViewModel` + view** — bind an Enabled toggle and any per-channel fields (webhook URL, etc.). Persist to `notifications.json` on save.
7. **DI registration** in `App.xaml.cs`:
   ```csharp
   services.Configure<SlackOptions>(config.GetSection("Notifications:Slack"));
   services.AddSingleton<INotificationTransport, SlackTransport>();
   ```
   Place alongside Telegram/Discord registrations. The dispatcher picks it up automatically via `IEnumerable<INotificationTransport>`.

## Hot-reload contract

- Options are consumed via `IOptionsMonitor<T>`, NOT `IOptions<T>`. Read `_monitor.CurrentValue` inside `SendAsync` so changes from the Settings tab take effect without restart.
- The Settings tab writes to `notifications.json` and the file-watcher reloads — your transport must re-read `CurrentValue` per send.

## Hard rules

- **Never throw out of `SendAsync`.** Catch + log. One bad transport must not poison the channel.
- **Respect the `Enabled` flag.** If disabled, return early — don't even open a connection.
- **No UI marshalling needed** — the dispatcher runs on its own hosted background worker. Don't touch `Dispatcher.Invoke`.
- **No new dependencies into Core or UI.** Pure Infrastructure addition.

## Reference reads

- `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/Telegram/TelegramTransport.cs` — Bot API (HttpClient + JSON).
- `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/Discord/DiscordTransport.cs` — webhook (HttpClient + JSON).
- `src/windows/Pipeline/TradingTerminal.Infrastructure/Notifications/NotificationDispatcher.cs` — the background worker draining the Channel.
- `src/windows/Core/TradingTerminal.Core/Notifications/INotificationTransport.cs` — the contract.
