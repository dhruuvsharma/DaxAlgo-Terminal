# Hosting the plugin marketplace feed

> How the signed plugin feed is produced and served. This is the **maintainer / operator** side; plugin
> *authors* read [plugin-authoring.md](plugin-authoring.md) and [marketplace-policy.md](marketplace-policy.md),
> and *users* just see the Plugin Manager's **Catalog** tab. The app-side consumer is
> [`PluginFeedClient`](../src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/Feed/PluginFeedClient.cs);
> the trust model is in [plugin-security.md](plugin-security.md).

The feed needs **no server**: a static, signed `plugins-index.json` plus the `.daxplugin` packages it points
at, both served over HTTPS. The reference hosting is a public GitHub repo — index on GitHub Pages, packages
as Release assets — but any static host works. A website built later reads the exact same index.

## The registry repo (`daxalgo-plugins`)

```
daxalgo-plugins/
├─ docs/                         # GitHub Pages root (Settings → Pages → /docs)
│  ├─ plugins-index.json         # the feed  → https://<pages-host>/plugins-index.json
│  └─ plugins-index.json.sig     # detached signature, served beside it
└─ .github/workflows/publish.yml # regenerate + sign + deploy on push to the index source
```

- **Index** → `https://dhruuvsharma.github.io/daxalgo-plugins/plugins-index.json` (whatever Pages URL you
  get). That URL goes into `Plugins:FeedUrl`.
- **Signature** → the client always fetches `<FeedUrl>.sig`, so it must sit at the same path plus `.sig`.
- **Packages** → upload each `<id>-<version>.daxplugin` as a **Release asset** and put its download URL in
  the index entry's `url`. Release assets are CDN-backed, immutable per release, and free.

## Feed schema (`plugins-index.json`)

Matches [`PluginIndex`](../src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/Feed/PluginIndex.cs)
(`feedVersion` is the schema version — the app ignores an index newer than it understands):

```json
{
  "feedVersion": 1,
  "publishedUtc": "2026-07-12T00:00:00Z",
  "plugins": [
    {
      "id": "acme.orderflow-imbalance",
      "name": "Order-Flow Imbalance",
      "publisher": "Acme Research",
      "description": "Trade-based OBI regime signal.",
      "tags": ["orderflow", "microstructure"],
      "paperUrl": "https://arxiv.org/abs/2507.22712",
      "latest": {
        "version": "1.2.0",
        "sdkVersion": "0.2.0-alpha",
        "minAppVersion": "1.1.0",
        "url": "https://github.com/dhruuvsharma/daxalgo-plugins/releases/download/acme.orderflow-imbalance-1.2.0/acme.orderflow-imbalance-1.2.0.daxplugin",
        "sha256": "8F3C…",
        "sizeBytes": 41231,
        "signatureThumbprint": null
      },
      "versions": [ /* older builds, same shape */ ]
    }
  ],
  "revoked": [
    { "id": "bad.plugin", "sha256": null, "reason": "withdrawn by author", "dateUtc": "2026-07-11T00:00:00Z" }
  ]
}
```

- **`sha256`** is the hash of the `.daxplugin` bytes. The catalog installer re-checks the download against
  it before anything is unpacked
  ([`PluginCatalogInstaller`](../src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/Feed/PluginCatalogInstaller.cs)),
  so the signed index is what binds the trusted feed to the bytes a user actually receives. Get it wrong and
  the install is refused.
- **`revoked[]`** is synced into each user's local `revoked.json` kill-list on the next feed refresh
  ([`PluginRevocationSync`](../src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/Feed/PluginRevocationSync.cs)),
  and the loader refuses those builds on the next start. Revoke by `sha256` (one bad build) or `id` (all
  builds of a plugin).

## The signing key

The feed is protected by an **ECDSA P-256** keypair. The app pins only the **public** key; the **private**
key signs the index and must never leave the maintainer's control (offline, or a CI secret).

Mint a keypair once with
[`FeedSigner`](../src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/Feed/FeedSigner.cs):

```csharp
var keys = FeedSigner.GenerateKeyPair();
// keys.PublicKeyBase64  -> paste into PluginsOptions.FeedPublicKey (shipped in the app)
// keys.PrivateKeyBase64 -> store as a SECRET (offline vault or the publish repo's Actions secret)
```

- **Public key** → `Plugins:FeedPublicKey` in the app's `appsettings.json`. It is base64
  `SubjectPublicKeyInfo`; an index whose signature doesn't verify against it is ignored (Activity Log
  warning), so a tampered or unsigned feed cannot inject a plugin.
- **Private key** → base64 PKCS#8. Never commit it. **Rotation:** ship a new public key in an app update,
  re-sign the index with the new private key; until users update, keep serving a copy signed by the old key
  if you must support both (or accept that un-updated apps stop seeing feed changes — they never load an
  *unsigned* feed, which is the safe failure).

## Signing + publishing the index

The signature is a detached ECDSA-P256/SHA-256 signature over the **raw index bytes** (no reformatting —
the verifier is byte-exact, so sign the file exactly as served):

```csharp
// In the publish workflow, with the private key from a secret:
FeedSigner.SignIndexFile("docs/plugins-index.json", Environment.GetEnvironmentVariable("FEED_PRIVATE_KEY")!);
// writes docs/plugins-index.json.sig
```

Then deploy `docs/` to Pages. Publish order matters: upload the **package** release asset first, then the
**index+sig** — never advertise a `url`/`sha256` before the bytes exist.

A minimal publish flow (regenerate index from submissions → sign → deploy) belongs in the registry repo's
`publish.yml`; it can call the two `FeedSigner` lines above via a tiny `dotnet run`, or reproduce the same
ECDSA-P256/SHA-256 operation in any language.

## Submissions → index

Listings are curated and source-required (see [marketplace-policy.md](marketplace-policy.md)). The intended
pipeline: a private `daxalgo-plugin-submissions` repo takes a PR with source + `plugin.json` + declared
permissions; CI **builds from source** (reproducible package hash), runs the plugin's tests and the IL
policy scanner, a human reviews the diff, and on approval the `.daxplugin` is signed (Authenticode, the
`signatureThumbprint`) and an index entry is generated into `daxalgo-plugins`. Until that repo exists, an
operator can hand-build the index and sign it with the flow above.

## Turning it on in the app

Both keys must be set or the Catalog tab stays hidden and no feed is fetched:

```json
"Plugins": {
  "FeedUrl": "https://dhruuvsharma.github.io/daxalgo-plugins/plugins-index.json",
  "FeedPublicKey": "MFkwEwYHKoZIzj0CAQYI…"   // base64 SubjectPublicKeyInfo from GenerateKeyPair
}
```

On launch the app refreshes in the background (never blocking startup), caches the last-good index under
`%LocalAppData%/DaxAlgoTerminal/plugin-feed/`, and syncs revocations. The Plugin Manager's Catalog tab then
lets users browse, install, and update — every install still going through the full
manifest / SDK / trust / IL-scan gate chain.

See also: [plugins.md](plugins.md) · [plugin-security.md](plugin-security.md) ·
[marketplace-policy.md](marketplace-policy.md) · [LICENSE-EXCEPTIONS.md](../LICENSE-EXCEPTIONS.md).
