# Media checklist

This file is the planned media backlog. The public README does not embed placeholder assets; add an
embed or reference to public documentation only after the corresponding file or hosted video exists.
Use image paths relative to the document that contains the embed.

Screenshots live in `docs/images/`. Videos are large binaries — prefer uploading them to a GitHub
release (or an issue/PR, then copy the generated URL) or a hosted demo, and link out rather than
committing an `.mp4` into the repository. The `docs/media/` paths below are the intended names if you do
keep a local copy (for example via Git LFS).

## Capture guidance (read once)

- **Data source:** this edition ships a single profile that launches on plain `appsettings.json`. The
  connectable Simulated broker was removed on 2026-08-16, so capture against one of the **keyless
  crypto feeds** (Binance, Coinbase, Bybit, Kraken, OKX) — real data, no account, nothing personal on
  screen.
- **Trading mode:** capture in **Paper** unless a shot is specifically about the Real-mode warning.
  Never capture a real account identifier, position, or order.
- **Theme:** capture the whole set in **one** theme (the default dark theme) for a consistent gallery.
  Optionally re-capture the main window in a light theme for the Theme Studio entry.
- **Resolution:** 1600×1000 or larger, then downscale; PNG for screenshots. Keep the window chrome.
- **Redaction:** no real broker credentials, account numbers, order data, or file paths with a username.
- **Videos:** 1080p, ≤60s, no audio narration required; show the cursor. Trim dead time.

## Screenshots

| # | File | Suggested README section | What to capture |
|---|------|--------------------------|-----------------|
| 1 | `docs/images/main-window.png` | Intro (hero) | Main window: strategy catalog, header chips, Activity Log drawer open at the bottom. |
| 2 | `docs/images/broker-selection.png` | Build and run | Broker-selection window at startup with the keyless crypto sources and **Simulated** checked. |
| 3 | `docs/images/shell-menu-bar.png` | The current shell | The menu bar + header, showing the **REC** chip and connection/status indicators. |
| 4 | `docs/images/catalog-cards.png` | Catalog, strategies, and visualizers | A strategy card (purple spine, **Open**) next to a visualizer card (blue spine, **Add to chart**), context menu open on the strategy card. |
| 5 | `docs/images/vibe-code-hyperion.png` | Author a strategy in the application | Vibe Code › Hyperion mid-flow: prompt on the left, generated/compiled strategy on the right. |
| 6 | `docs/images/execution-console.png` | Order execution | Execution Console with a book open: positions, open orders, and history grids, with the Paper/Real mode banner visible. |
| 7 | `docs/images/recorder-panel.png` | Market data, storage, and archives | The background recorder panel (opened from the **REC** chip) with L1/L2/bar/trade-tape toggles. |

### Optional / additional shots

These are optional deeper-coverage assets. Wire one into the relevant section only after the image is
tracked.

| File | Suggested placement | What to capture |
|------|---------------------|-----------------|
| `docs/images/theme-studio.png` | View menu / themes | Theme Studio editing a palette token live, with the preview updating. |
| `docs/images/extensions-manager.png` | Know the artifact boundary | Vibe Code › Extensions after reading a `.daxalgostrategy`: the verified manifest and payload list. |
| `docs/images/activity-log.png` | The current shell | The Activity Log drawer expanded, showing System + strategy sources. |
| `docs/images/login-paper-real.png` | Order execution | The broker login window footer showing the Paper/Real toggle, with the typed-`LIVE` confirmation prompt open. |

## Videos

| # | File / link | Suggested README section | Storyboard |
|---|-------------|--------------------------|------------|
| V1 | `docs/media/getting-started.mp4` | Build and run | Clone → `dotnet build` → `dotnet run` → broker-selection picks a keyless crypto feed → the terminal opens on live data. ≈60s. |
| V2 | `docs/media/authoring-walkthrough.mp4` | Author a strategy in the application | Open Hyperion → describe a strategy → generate → review the compiled code → **Compile & Register** → the new card appears. ≈60–90s. |

## Status

Nothing is captured yet. Update this file as assets land; do not add broken media links to public docs.
