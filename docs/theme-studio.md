# Theme Studio — recolour the whole app

> Last updated: 2026-07-22

DaxAlgo Terminal ships with **DaxAlgo Dark** and **DaxAlgo Light**. **Theme Studio** is a built-in,
live colour editor that lets you change *any* palette colour and see the result instantly — no code,
no restart — then save and share your own theme.

Open it from **View → Customize theme… (Theme Studio)**.

> 🖼️ **Screenshot:** `images/theme-studio.png` — the live palette editor with a token being edited
> and the app preview updating in real time.

---

## In plain terms

Everything you see — backgrounds, text, the green "bullish" and red "bearish" colours, borders,
button highlights — is driven by a small set of named **colour tokens** (for example
`Background.Primary`, `Accent.Brush`, `Bullish.Brush`). Theme Studio is a panel that lists every token
with a colour swatch. Change a swatch and the whole app re-tints **live**, so you can dial in exactly
the look you want and watch it happen. When you're happy, you save it as a named theme and can export
it to a file to back up or share with someone else.

It's aimed at anyone who wants the terminal to match their taste or workspace — a designer
collaborator tweaking the palette, or a user who wants a softer variation of either DaxAlgo base.

---

## The built-in themes

Before you touch the editor, there are two ready-made themes under **View → Theme**:

| Theme | Look |
|---|---|
| **DaxAlgo Dark** | The default — a deep blue-grey terminal canvas with electric-blue accents. |
| **DaxAlgo Light** | A bright, cool-toned counterpart with the same accent and P&L semantics. |

Pick any with one click; the choice is remembered for next launch.

---

## Using the editor

1. **Open** View → Customize theme… The editor lists every palette **token** with its current colour.
2. **Edit** a token's colour. The app re-renders immediately, so you're always previewing the real
   result, not a mock-up.
3. **Save** your edits as a **custom theme** (give it a name). It then appears alongside the built-in
   themes in View → Theme.
4. **Export / Import**. A custom theme is just a small **JSON** file. Export it to back it up or send
   it to a collaborator; import a JSON someone sent you to use their theme.

Because the whole UI binds to these tokens through `DynamicResource`, a change to one token cascades to
every window that uses it — strategy windows, charts, dialogs, the shell — all at once.

---

## Tips

- **Keep contrast readable.** The bullish/bearish and warning/error colours carry meaning across every
  chart and the activity log; if you recolour them, keep green-ish/red-ish semantics so signals stay
  legible at a glance.
- **Start from a base theme.** Switch to DaxAlgo Dark or DaxAlgo Light first, then tweak — you'll
  only need to change a handful of tokens.
- **Custom themes are portable.** The exported JSON has no machine-specific data, so it's safe to
  commit to a repo or share.

---

## Under the hood (for contributors)

Theme Studio extends the shared **`IThemeManager`** (in `TradingTerminal.UI`), which owns the active
palette and the `ApplySaved()` call made at startup (before any window is shown, so the login window
already wears the saved theme). The base themes are resource dictionaries of named brushes/colours; a
custom theme is a serialized token→colour map persisted as JSON under the app's local data folder. The
editor view-model writes token changes straight into the live `DynamicResource` dictionary, which is
what makes the preview instantaneous. No new dependencies are required.

**DaxAlgo Dark** and **DaxAlgo Light** are the only bundled palettes in the Windows/WPF application.
Each is a `ResourceDictionary` under `TradingTerminal.UI/Themes/`, registered in `ThemeManager`. The
live Theme Studio editor is reached from the desktop shell's **View** menu.

See also [user-guide.md](user-guide.md) (the View menu) and [configuration.md](configuration.md)
(where preferences persist).
