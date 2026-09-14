namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// The whole of what a unit's page gets from the terminal: <c>dax.on</c>, <c>dax.send</c> and
/// <c>dax.ready</c>, injected before the page's own scripts run.
///
/// <para><b>Transport, not a toolkit.</b> Three functions and an error forwarder. Anything a page
/// looks like — charts, tables, controls — is the page's own, which is the point of giving a unit a
/// page rather than a widget library.</para>
///
/// <para>Messages host → page are <c>{ topic, payload }</c>. Messages page → host carry <c>dax: 1</c>
/// and a <c>type</c> of <c>ready</c>, <c>message</c> or <c>error</c>, so nothing else a page might post
/// is mistaken for the bridge.</para>
/// </summary>
internal static class DaxPageScript
{
    /// <summary>The origin a unit's page is served from. Private to the WebView2 instance.</summary>
    public const string HostName = "unit.daxalgo.local";

    public const string EntryUrl = "https://" + HostName + "/index.html";

    public const string Source = """
        (() => {
          if (window.dax) return;
          const webview = window.chrome && window.chrome.webview;
          const handlers = new Map();
          const post = message => {
            try { if (webview) webview.postMessage(Object.assign({ dax: 1 }, message)); } catch (_) { }
          };
          const text = value => {
            if (value && value.stack) return String(value.stack);
            if (value && value.message) return String(value.message);
            return String(value);
          };

          window.addEventListener('error', e => post({
            type: 'error',
            message: String(e.message || text(e.error) || 'Script error'),
            source: String(e.filename || ''),
            line: Number(e.lineno || 0)
          }));
          window.addEventListener('unhandledrejection', e => post({
            type: 'error',
            message: 'Unhandled promise rejection: ' + text(e.reason),
            source: '',
            line: 0
          }));

          if (webview) {
            webview.addEventListener('message', e => {
              const data = e.data;
              if (!data || typeof data.topic !== 'string') return;
              const list = handlers.get(data.topic);
              if (!list) return;
              for (const handler of Array.from(list)) {
                try { handler(data.payload); }
                catch (err) { post({ type: 'error', message: 'dax.on("' + data.topic + '") threw: ' + text(err), source: '', line: 0 }); }
              }
            });
          }

          window.dax = Object.freeze({
            on(topic, handler) {
              if (typeof handler !== 'function') throw new TypeError('dax.on needs a function');
              if (!handlers.has(topic)) handlers.set(topic, new Set());
              handlers.get(topic).add(handler);
              return () => handlers.get(topic).delete(handler);
            },
            send(topic, payload) {
              post({ type: 'message', topic: String(topic), payload: payload === undefined ? null : payload });
            },
            ready() {
              post({ type: 'ready' });
            }
          });
        })();
        """;
}
