namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// The terminal's own settings panel, as a page.
///
/// <para><b>The terminal draws this one, not the unit.</b> A unit's page is the author's and can be
/// anything; the settings, the instrument picker, Apply and Reset look the same in every unit and cannot
/// be got wrong by a model — which is exactly what happened when they were left to one: a generated
/// instrument selector that could not select an instrument, in build after build.</para>
///
/// <para>It speaks the same bridge a unit's page does (<see cref="DaxPageScript"/>): it receives
/// <c>settings</c> and sends <c>set</c>, <c>apply</c>, <c>reset</c> and <c>action</c>. Nothing here
/// reaches the unit directly — the host applies them through the same presenter the WPF expander used,
/// so validation, clamping and the restart-on-apply are one implementation.</para>
/// </summary>
internal static class SettingsPage
{
    public const string Html = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: #0d1117; color: #e6edf3;
            font: 12px "Segoe UI", system-ui, sans-serif;
          }
          header {
            display: flex; align-items: baseline; justify-content: space-between; gap: 8px;
            padding: 10px 12px 8px; border-bottom: 1px solid #1f2733; position: sticky; top: 0; background: #0d1117;
          }
          header h1 { margin: 0; font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: #8b949e; font-weight: 600; }
          header .dirty { font-size: 10px; color: #d29922; }
          main { padding: 8px 12px 12px; }
          .row { padding: 7px 0; border-bottom: 1px solid #161b22; }
          .row:last-child { border-bottom: 0; }
          label { display: block; color: #c9d1d9; margin-bottom: 4px; }
          label .unit { color: #6e7681; margin-left: 4px; }
          input[type=text], input[type=number], select {
            width: 100%; padding: 5px 7px; background: #010409; color: #e6edf3;
            border: 1px solid #30363d; border-radius: 4px; font: inherit;
          }
          input:focus, select:focus { outline: none; border-color: #388bfd; }
          .row.invalid input, .row.invalid select { border-color: #f85149; }
          .hint { color: #6e7681; margin-top: 3px; font-size: 10.5px; }
          .error { color: #f85149; margin-top: 3px; font-size: 10.5px; }
          .toggle { display: flex; align-items: center; gap: 8px; }
          .toggle input { width: 14px; height: 14px; accent-color: #388bfd; }
          .picker input { margin-bottom: 4px; }
          .picker select { height: 132px; }
          footer {
            position: sticky; bottom: 0; background: #0d1117; border-top: 1px solid #1f2733;
            padding: 9px 12px; display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
          }
          button {
            padding: 5px 13px; border-radius: 4px; border: 1px solid #30363d;
            background: #21262d; color: #e6edf3; font: inherit; cursor: pointer;
          }
          button.primary { background: #1f6feb; border-color: #1f6feb; }
          button:disabled { opacity: .45; cursor: default; }
          .status { color: #8b949e; flex: 1 1 100%; }
          .actions { display: flex; gap: 6px; flex-wrap: wrap; padding: 8px 0 0; border-top: 1px solid #161b22; margin-top: 8px; }
          .empty { color: #6e7681; padding: 14px 0; }
        </style>
        </head>
        <body>
        <header><h1>Settings</h1><span class="dirty" id="dirty"></span></header>
        <main id="rows"><div class="empty">Waiting for the unit…</div></main>
        <div class="actions" id="actions"></div>
        <footer>
          <button class="primary" id="apply" disabled>Apply</button>
          <button id="reset" disabled>Reset</button>
          <span class="status" id="status">Applying restarts the unit with these values. Its history starts again; the log does not.</span>
        </footer>
        <script>
        (() => {
          const rows = document.getElementById('rows');
          const actions = document.getElementById('actions');
          const apply = document.getElementById('apply');
          const reset = document.getElementById('reset');
          const status = document.getElementById('status');
          const dirty = document.getElementById('dirty');
          const focused = () => document.activeElement && document.activeElement.dataset
            ? document.activeElement.dataset.key : null;

          const send = (topic, payload) => dax.send(topic, payload);
          const set = (key, value) => send('set', { key, value: String(value) });

          const text = (value) => {
            const node = document.createElement('span');
            node.textContent = value == null ? '' : String(value);
            return node.innerHTML;
          };

          function editor(p) {
            if (p.kind === 'Boolean') {
              return '<div class="toggle"><input type="checkbox" data-key="' + text(p.key) + '"'
                + (String(p.value).toLowerCase() === 'true' ? ' checked' : '') + '><span>'
                + (String(p.value).toLowerCase() === 'true' ? 'On' : 'Off') + '</span></div>';
            }
            if (p.choices && p.choices.length) {
              return '<select data-key="' + text(p.key) + '">'
                + p.choices.map(c => '<option' + (c === p.value ? ' selected' : '') + '>' + text(c) + '</option>').join('')
                + '</select>';
            }
            if (p.instruments && p.instruments.length) {
              const search = (p.search || '').toLowerCase();
              const shown = p.instruments.filter(i =>
                !search || i.name.toLowerCase().includes(search) || (i.category || '').toLowerCase().includes(search));
              return '<div class="picker">'
                + '<input type="text" data-search="' + text(p.key) + '" placeholder="Search instruments" value="' + text(p.search || '') + '">'
                + '<select size="8" data-key="' + text(p.key) + '">'
                + shown.map(i => '<option value="' + text(i.id) + '"' + (i.id === p.value ? ' selected' : '') + '>'
                    + text(i.name) + (i.broker ? ' · ' + text(i.broker) : '') + '</option>').join('')
                + '</select></div>';
            }
            const type = (p.kind === 'Integer' || p.kind === 'Number') ? 'number' : 'text';
            const step = p.kind === 'Integer' ? ' step="1"' : '';
            return '<input type="' + type + '"' + step + ' data-key="' + text(p.key) + '" value="' + text(p.value) + '">';
          }

          function render(state) {
            apply.disabled = !state.canEdit || state.isApplying || !state.isDirty;
            reset.disabled = !state.canEdit || state.isApplying || !state.isDirty;
            dirty.textContent = state.isDirty ? 'unapplied changes' : '';
            if (state.status) status.textContent = state.status;

            if (!state.parameters.length) {
              rows.innerHTML = '<div class="empty">This unit declares no settings.</div>';
            } else {
              rows.innerHTML = state.parameters.map(p =>
                '<div class="row' + (p.error ? ' invalid' : '') + '">'
                + '<label for="' + text(p.key) + '">' + text(p.label)
                + (p.unit ? '<span class="unit">' + text(p.unit) + '</span>' : '') + '</label>'
                + editor(p)
                + (p.error ? '<div class="error">' + text(p.error) + '</div>'
                    : p.rangeHint || p.description
                      ? '<div class="hint">' + text(p.rangeHint ? p.rangeHint + (p.description ? ' · ' : '') : '')
                        + text(p.description || '') + '</div>'
                      : '')
                + '</div>').join('');
            }

            actions.innerHTML = (state.actions || []).map(a =>
              '<button data-action="' + text(a.id) + '" title="' + text(a.detail || '') + '">' + text(a.label) + '</button>').join('');

            const key = state.focus;
            if (key) {
              const node = rows.querySelector('[data-key="' + key + '"]');
              if (node && node.tagName !== 'SELECT') { node.focus(); node.selectionStart = node.value.length; }
            }
          }

          rows.addEventListener('change', e => {
            const node = e.target;
            if (node.dataset.key === undefined) return;
            set(node.dataset.key, node.type === 'checkbox' ? node.checked : node.value);
          });
          rows.addEventListener('input', e => {
            const node = e.target;
            if (node.dataset.search !== undefined) send('search', { key: node.dataset.search, value: node.value });
            else if (node.dataset.key !== undefined && node.tagName === 'INPUT' && node.type !== 'checkbox')
              set(node.dataset.key, node.value);
          });
          actions.addEventListener('click', e => {
            if (e.target.dataset && e.target.dataset.action) send('action', { id: e.target.dataset.action });
          });
          apply.addEventListener('click', () => send('apply', null));
          reset.addEventListener('click', () => send('reset', null));

          dax.on('settings', state => { state.focus = focused(); render(state); });
          dax.ready();
        })();
        </script>
        </body>
        </html>
        """;
}
