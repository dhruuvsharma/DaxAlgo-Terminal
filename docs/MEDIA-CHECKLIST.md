# Media checklist

This file tracks every screenshot and video the documentation references. Each entry maps a placeholder
in the docs to the exact file to drop in, and describes what to capture. When you add a file, delete the
matching placeholder blockquote in the doc and replace it with the real embed:

```markdown
![Strategy catalog](docs/images/main-window.png)
```

Screenshots live in `docs/images/`. Videos are large binaries — prefer uploading them to a GitHub
release (or an issue/PR, then copy the generated URL) or a hosted demo, and link out rather than
committing an `.mp4` into the repository. The `docs/media/` paths below are the intended names if you do
keep a local copy (for example via Git LFS).

## Capture guidance (read once)

- **Data source:** run the offline `DevSim` profile so every shot shows clean synthetic data and no
  personal account details — `DOTNET_ENVIRONMENT=DevSim dotnet run --project src/windows/Shell/TradingTerminal.App.Basic`.
- **Theme:** capture the whole set in **one** theme (the default dark theme) for a consistent gallery.
  Optionally re-capture the main window in a light theme for the Theme Studio entry.
- **Resolution:** 1600×1000 or larger, then downscale; PNG for screenshots. Keep the window chrome.
- **Redaction:** no real broker credentials, account numbers, order data, or file paths with a username.
- **Videos:** 1080p, ≤60s, no audio narration required; show the cursor. Trim dead time.

## Screenshots

| # | File | Used in (README section) | What to capture |
|---|------|--------------------------|-----------------|
| 1 | `docs/images/main-window.png` | Intro (hero) | Main window: strategy catalog, header chips, Activity Log drawer open at the bottom. |
| 2 | `docs/images/broker-selection.png` | Build and run | Broker-selection window at startup with the keyless crypto sources and **Simulated** checked. |
| 3 | `docs/images/shell-menu-bar.png` | The current shell | The menu bar + header, showing the **REC** chip and connection/status indicators. |
| 4 | `docs/images/catalog-cards.png` | Catalog, strategies, and visualizers | A strategy card (purple spine, **Open**) next to a visualizer card (blue spine, **Add to chart**), context menu open on the strategy card. |
| 5 | `docs/images/strategy-studio-hyperion.png` | Author a strategy in the application | Strategy Studio › Vibe Code › Hyperion mid-flow: prompt on the left, generated/compiled strategy on the right. |
| 6 | `docs/images/backtest-studio.png` | Backtesting | Backtest Studio after a run: setup rail on the left, results panel (equity curve, stats strip, trade list) on the right. |
| 7 | `docs/images/recorder-panel.png` | Market data, storage, and archives | The background recorder panel (opened from the **REC** chip) with L1/L2/bar/trade-tape toggles. |

### Optional / additional shots

These are not yet referenced in `README.md`. Add them if you want deeper coverage; wire each into the
relevant section and move its row up to the table above.

| File | Suggested placement | What to capture |
|------|---------------------|-----------------|
| `docs/images/theme-studio.png` | View menu / themes | Theme Studio editing a palette token live, with the preview updating. |
| `docs/images/extensions-manager.png` | Author an external runtime plugin | Strategy Studio › Extensions: an installed `.daxplugin` with enable/disable/quarantine controls. |
| `docs/images/activity-log.png` | The current shell | The Activity Log drawer expanded, showing System + strategy sources. |
| `docs/images/ai-providers.png` | Author a strategy in the application | Settings › AI providers with a provider configured (keys redacted). |
| `docs/images/backtest-optimization.png` | Backtesting | Backtest Studio optimization/walk-forward view with the score surface. |

## Videos

| # | File / link | Used in (README section) | Storyboard |
|---|-------------|--------------------------|------------|
| V1 | `docs/media/getting-started.mp4` | Build and run | Clone → `dotnet build` → `dotnet run` → broker-selection picks **Simulated** → open Backtest Studio → run → results appear. ≈60s. |
| V2 | `docs/media/authoring-walkthrough.mp4` | Author a strategy in the application | Open Hyperion → describe a strategy → generate → review the compiled code → install → the new card appears → **Quick backtest**. ≈60–90s. |

## Status

Nothing captured yet — every entry above is a placeholder. Update this table (or tick the boxes) as
files land so it stays the single source of truth for what still needs a shot.
