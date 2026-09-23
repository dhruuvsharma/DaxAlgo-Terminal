using System.Text.Json;
using TradingTerminal.Blocks.Runtime.Verification;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// What a unit's page looks like, measured rather than judged: whether it fits its window, whether one
/// panel hides the main view, whether it prints "NaN", whether text runs off the edge or over other text.
///
/// <para><b>The eyes a text-only model does not have.</b> Every NVIDIA NIM model Hyperion has been run
/// on is text-only, so the picture critic reads the page's source and guesses what it will look like.
/// Measured on the 2026-09-20 Nemotron Battlefield run: the delivered page was a 2D depth chart filling
/// the whole window on top of the 3D scene, with a scrollbar — three rounds of critics reading the
/// source never said so, because nothing in the source says "this covers the scene". The browser
/// knows, for free, and this asks it.</para>
///
/// <para><b>Warnings, never failures.</b> A layout measure can be wrong about intent — a full-window
/// panel may be the design — so it does not stop a unit from passing the gate. It is handed to the
/// critics as fact and to the page's owner as a finding, and a run that cannot fix it still delivers.</para>
/// </summary>
public static class PageLayoutAudit
{
    /// <summary>The code prefix every layout finding carries.</summary>
    public const string CodePrefix = "page.layout.";

    /// <summary>
    /// Runs in the page, returns what it measured. Kept to plain DOM so it works on any page: the main
    /// view is the largest visible canvas, a panel is the outermost element that does not also contain it.
    /// </summary>
    public const string Script = """
        (() => {
          const W = window.innerWidth, H = window.innerHeight;
          const out = { width: W, height: H, scroll: null, covered: [], broken: [], offscreen: [], overlap: [] };
          const describe = el => {
            if (!el || !el.tagName) return '?';
            if (el.id) return '#' + el.id;
            const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2) : [];
            return el.tagName.toLowerCase() + (cls.length ? '.' + cls.join('.') : '');
          };
          const shown = el => {
            if (el.checkVisibility && !el.checkVisibility({ opacityProperty: true, visibilityProperty: true })) return false;
            const r = el.getBoundingClientRect();
            return r.width > 1 && r.height > 1;
          };
          const inView = r => Math.max(0, Math.min(r.right, W) - Math.max(r.left, 0)) * Math.max(0, Math.min(r.bottom, H) - Math.max(r.top, 0));
          const alpha = c => {
            const m = /rgba?\(([^)]+)\)/.exec(c || '');
            if (!m) return 0;
            const p = m[1].split(/[\s,\/]+/).filter(Boolean);
            return p.length >= 4 ? parseFloat(p[3]) : 1;
          };

          // A page that can scroll, and does. One that hides its overflow cannot; what it cuts off is
          // measured as text off the edge instead.
          const doc = document.scrollingElement || document.documentElement;
          const clipped = axis => [document.documentElement, document.body].some(el => /^(hidden|clip)$/.test(getComputedStyle(el)['overflow' + axis]));
          if ((doc.scrollHeight > H + 4 && !clipped('Y')) || (doc.scrollWidth > W + 4 && !clipped('X')))
            out.scroll = { width: doc.scrollWidth, height: doc.scrollHeight };

          // The main view, and anything solid on top of it.
          let main = null, mainArea = 0;
          for (const c of document.querySelectorAll('canvas')) {
            if (!shown(c)) continue;
            const a = inView(c.getBoundingClientRect());
            if (a > mainArea) { main = c; mainArea = a; }
          }
          if (main && mainArea >= 0.25 * W * H) {
            const r = main.getBoundingClientRect();
            const x0 = Math.max(r.left, 0), x1 = Math.min(r.right, W), y0 = Math.max(r.top, 0), y1 = Math.min(r.bottom, H);
            const hits = new Map();
            let samples = 0;
            for (let i = 0; i < 16; i++) {
              for (let j = 0; j < 12; j++) {
                samples++;
                const top = document.elementFromPoint(x0 + (i + 0.5) / 16 * (x1 - x0), y0 + (j + 0.5) / 12 * (y1 - y0));
                if (!top || top === main || main.contains(top) || top.contains(main)) continue;
                let panel = top;
                while (panel.parentElement && !panel.parentElement.contains(main)) panel = panel.parentElement;
                let solid = false;
                for (let el = top; el; el = el.parentElement) {
                  const s = getComputedStyle(el);
                  if (/^(CANVAS|IMG|VIDEO|IFRAME)$/.test(el.tagName) || alpha(s.backgroundColor) * parseFloat(s.opacity || '1') >= 0.85) { solid = true; break; }
                  if (el === panel) break;
                }
                if (solid) hits.set(panel, (hits.get(panel) || 0) + 1);
              }
            }
            for (const [panel, n] of hits) {
              if (n / samples >= 0.45) out.covered.push({ element: describe(panel), main: describe(main), percent: Math.round(100 * n / samples) });
            }
          }

          // A 3D scene (three.js marks its canvas) squeezed out by something larger.
          const outermost = el => { let p = el; while (p.parentElement && p.parentElement !== document.body) p = p.parentElement; return p; };
          out.squeezed = [];
          for (const c of document.querySelectorAll('canvas[data-engine^="three"]')) {
            const r = c.getBoundingClientRect();
            const a = shown(c) ? inView(r) : 0;
            if (a >= 0.35 * W * H || !main || main === c || mainArea <= a) continue;
            out.squeezed.push({
              element: describe(c), width: Math.round(Math.max(0, Math.min(r.right, W) - Math.max(r.left, 0))),
              height: Math.round(Math.max(0, Math.min(r.bottom, H) - Math.max(r.top, 0))), percent: Math.round(100 * a / (W * H)),
              largest: describe(outermost(main)), largestPercent: Math.round(100 * mainArea / (W * H)),
            });
          }

          // three.js loaded, and no three.js renderer on the page: the scene never started. A page that
          // catches its own WebGL failure and falls back throws nothing the gate would see.
          const importMap = document.querySelector('script[type="importmap"]');
          const threeLoaded = (importMap && /three/.test(importMap.textContent || ''))
            || performance.getEntriesByType('resource').some(e => /\/three(\.module|\.core)?(\.min)?\.js/.test(e.name));
          out.noScene = threeLoaded && !document.querySelector('canvas[data-engine^="three"]');
          out.console = (window.__daxProbeConsole || []).slice(0, 6);

          // Values that were never set.
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          const bad = /(^|[^\w])(undefined|NaN|\[object Object\]|Infinity)([^\w]|$)/;
          for (let n = walker.nextNode(); n && out.broken.length < 6; n = walker.nextNode()) {
            const t = (n.nodeValue || '').trim();
            if (!t || !(bad.test(t) || t === 'null')) continue;
            const el = n.parentElement;
            if (!el || /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE)$/.test(el.tagName) || !shown(el) || inView(el.getBoundingClientRect()) === 0) continue;
            out.broken.push({ element: describe(el), text: t.slice(0, 60) });
          }

          // Text, clipped by whatever panel scrolls or hides it (never by the page itself).
          const pageSized = q => (q.right - q.left) * (q.bottom - q.top) >= 0.95 * W * H;
          const clip = (el, r) => {
            let l = r.left, t = r.top, rt = r.right, b = r.bottom;
            for (let p = el.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
              const s = getComputedStyle(p);
              if (s.overflowX === 'visible' && s.overflowY === 'visible') continue;
              const q = p.getBoundingClientRect();
              if (pageSized(q)) continue;
              if (s.overflowX !== 'visible') { l = Math.max(l, q.left); rt = Math.min(rt, q.right); }
              if (s.overflowY !== 'visible') { t = Math.max(t, q.top); b = Math.min(b, q.bottom); }
            }
            return { l, t, r: rt, b, w: rt - l, h: b - t };
          };
          const leaves = [];
          for (const el of document.body.querySelectorAll('*')) {
            if (leaves.length >= 400) break;
            if (/^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE|CANVAS|svg|path|g)$/i.test(el.tagName)) continue;
            let own = '';
            for (const c of el.childNodes) if (c.nodeType === 3) own += c.nodeValue;
            own = own.trim();
            if (!own || !shown(el)) continue;
            const c = clip(el, el.getBoundingClientRect());
            if (c.w <= 1 || c.h <= 1) continue;
            leaves.push({ el, c, text: own });
          }
          if (!out.scroll) {
            for (const { el, c } of leaves) {
              if (out.offscreen.length >= 6) break;
              const over = Math.max(c.r - W, c.b - H, -c.l, -c.t);
              if (over > 2 && inView({ left: c.l, top: c.t, right: c.r, bottom: c.b }) > 0) out.offscreen.push({ element: describe(el), pixels: Math.round(over) });
            }
          }
          for (let a = 0; a < leaves.length && out.overlap.length < 5; a++) {
            for (let b = a + 1; b < leaves.length && out.overlap.length < 5; b++) {
              const A = leaves[a], B = leaves[b];
              if (A.el.contains(B.el) || B.el.contains(A.el)) continue;
              const ix = Math.min(A.c.r, B.c.r) - Math.max(A.c.l, B.c.l), iy = Math.min(A.c.b, B.c.b) - Math.max(A.c.t, B.c.t);
              if (ix <= 0 || iy <= 0) continue;
              if (ix * iy < 0.35 * Math.min(A.c.w * A.c.h, B.c.w * B.c.h)) continue;
              out.overlap.push({ a: describe(A.el), b: describe(B.el), textA: A.text.slice(0, 30), textB: B.text.slice(0, 30) });
            }
          }
          return out;
        })()
        """;

    /// <summary>
    /// Installed in the probe's page before anything else runs: keeps what the page itself reported
    /// through <c>console.error</c> and <c>console.warn</c> — the errors it caught and logged, which no
    /// error event carries. The Nemotron scene's WebGL failure went to <c>console.warn</c>.
    /// The probe's only; the terminal's own windows never run it.
    /// </summary>
    public const string ConsoleRecorder = """
        (() => {
          const kept = window.__daxProbeConsole = [];
          const text = a => a && a.stack ? String(a.stack).split('\n').slice(0, 2).join(' ')
            : a && a.message ? String(a.message)
            : typeof a === 'object' ? (() => { try { return JSON.stringify(a); } catch (_) { return String(a); } })()
            : String(a);
          for (const level of ['error', 'warn']) {
            const original = console[level].bind(console);
            console[level] = (...args) => {
              try { if (kept.length < 20) kept.push((level === 'warn' ? 'warning: ' : '') + args.map(text).join(' ').slice(0, 300)); } catch (_) { }
              return original(...args);
            };
          }
        })();
        """;

    /// <summary>The findings in what <see cref="Script"/> returned — the JSON WebView2 hands back.</summary>
    public static IReadOnlyList<DriveFinding> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // WebView2 returns a script's string result JSON-encoded; an object comes back as the object.
            if (root.ValueKind == JsonValueKind.String) return Read(root.GetString());
            if (root.ValueKind != JsonValueKind.Object) return [];

            var width = Int(root, "width");
            var height = Int(root, "height");
            var findings = new List<DriveFinding>();

            if (root.TryGetProperty("scroll", out var scroll) && scroll.ValueKind == JsonValueKind.Object)
                findings.Add(Warning("scrolls",
                    $"The page is {Int(scroll, "width")}×{Int(scroll, "height")} in a {width}×{height} window, so it scrolls and part of it is out of sight.",
                    "A unit's window is a fixed app: html and body at 100% height with overflow hidden, and every panel sized from the window (grid, flex or vh units) instead of pushing past its edge."));

            foreach (var item in Items(root, "covered"))
                findings.Add(Warning("covered",
                    $"{Text(item, "element")} covers {Int(item, "percent")}% of the page's main view ({Text(item, "main")}), so what is drawn under it cannot be seen.",
                    "Dock it to one side at the size the brief gives (a side or corner panel, typically a quarter to a third of the window), or make it translucent; the main view stays visible."));

            var logged = Items(root, "console").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();

            if (root.TryGetProperty("noScene", out var noScene) && noScene.ValueKind == JsonValueKind.True)
                findings.Add(Warning("no-scene",
                    "The page loads three.js, but no three.js renderer is on the page: the 3D scene never started — it failed and the error was caught, or the page fell back."
                    + (logged.Length > 0 ? $" The page logged: {string.Join(" | ", logged.Take(3))}" : string.Empty),
                    "Find why the renderer did not start (the logged error names it) and fix that; a fallback is for a machine without WebGL, and this one has it."));
            else if (logged.Where(l => !l.StartsWith("warning: ", StringComparison.Ordinal)).ToArray() is { Length: > 0 } errors)
                findings.Add(Warning("console-errors",
                    $"The page caught and logged errors while the unit fed it: {string.Join(" | ", errors.Take(4))}",
                    "Fix what they report; an error a page catches still leaves the part that threw not working."));

            foreach (var item in Items(root, "squeezed"))
                findings.Add(Warning("squeezed",
                    $"The 3D scene ({Text(item, "element")}) shows at {Int(item, "width")}×{Int(item, "height")}, {Int(item, "percent")}% of the {width}×{height} window, "
                    + $"while {Text(item, "largest")} takes {Int(item, "largestPercent")}% — the scene has been squeezed out of the main view.",
                    "Give the scene the window (a full-window layer, or the main area of the grid) and size its renderer from its own element's size; dock the other panels around or over it."));

            var broken = Items(root, "broken").ToArray();
            if (broken.Length > 0)
                findings.Add(Warning("broken-value",
                    $"The page shows values that were never set: {string.Join("; ", broken.Select(b => $"\"{Text(b, "text")}\" in {Text(b, "element")}"))}.",
                    "Read the payload fields the unit actually sends, and format every number with a fallback ('—') for when it is not there yet."));

            var offscreen = Items(root, "offscreen").ToArray();
            if (offscreen.Length > 0)
                findings.Add(Warning("offscreen",
                    $"Text runs past the edge of the {width}×{height} window and is cut off: {string.Join(", ", offscreen.Select(o => $"{Text(o, "element")} by {Int(o, "pixels")}px"))}.",
                    "Fit those panels inside the window: size them from the window, let lists scroll inside their own panel, and shorten or wrap long labels."));

            var overlap = Items(root, "overlap").ToArray();
            if (overlap.Length > 0)
                findings.Add(Warning("overlap",
                    $"Text is drawn on top of other text: {string.Join("; ", overlap.Select(o => $"{Text(o, "a")} (\"{Text(o, "textA")}\") over {Text(o, "b")} (\"{Text(o, "textB")}\")"))}.",
                    "Give each its own space: they are positioned into the same place, usually by absolute positioning or a fixed height that is too small."));

            return findings;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DriveFinding Warning(string code, string message, string remedy) =>
        new(DriveSeverity.Warning, CodePrefix + code, message, remedy);

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "?" : "?";
}
