# DaxAlgo strategy bundle tool

`DaxAlgo.Strategy.BundleTool` is the portable .NET tool for creating and passively checking
`.daxstrategy` files. It uses the public `DaxAlgo.Strategy.Bundle` API and never loads a payload
assembly.

```powershell
dotnet pack DaxAlgo.Strategy.BundleTool.csproj -c Release
dotnet tool install --global --add-source <package-folder> --prerelease DaxAlgo.Strategy.BundleTool
```

## Pack

```powershell
daxalgo-bundle pack `
  --id acme.mean-reversion `
  --name "Acme Mean Reversion" `
  --version 1.2.0 `
  --publisher acme-research `
  --sdk 0.2.0-alpha `
  --engine .\bin\Acme.MeanReversion.Engine.dll `
  --entry-type Acme.MeanReversion.Engine.StrategyFactory `
  --ui .\bin\Acme.MeanReversion.Wpf.dll `
  --dependency .\bin\Acme.Numerics.dll `
  --capability market-data.depth `
  --output .\Acme.MeanReversion.daxstrategy
```

`--entry-type` names one public, parameterless `DaxAlgo.Sdk.IStrategyEngineFactory`; its instance
contract declares the parameter schema, data requirements, and parameterized kernel activation. The
verifier resolves and validates that shape from metadata without loading code. `--ui` is optional and
the core permits at most one.
`--dependency`, `--resource`, `--sbom`,
`--provenance`, and `--capability` may be repeated. Bundle paths are derived from the payload role and
NFC-normalized file name; source directory names are not disclosed. The core rejects path aliases,
duplicate derived names, invalid role counts, incompatible metadata, and limit violations.
Pack outputs must use the `.daxstrategy` extension and cannot overwrite any existing file.

## Sign

```powershell
# Safe in-place rewrite after the complete signed output has been flushed.
daxalgo-bundle sign --bundle .\Acme.MeanReversion.daxstrategy `
  --key .\publisher-private.pem --key-id acme-2026

# Or keep unsigned and signed files separately. Pipe a PEM with --key - in CI.
daxalgo-bundle sign --bundle .\unsigned.daxstrategy --output .\signed.daxstrategy `
  --key - --key-id acme-2026
```

Private key bytes are never accepted as a command-line value and are never printed. Prefer an
appropriately protected key file or pipe the PEM from a secret provider; avoid shell history and logs.
The v1 signer accepts an unencrypted ECDSA P-256 private-key PEM.
All bundle inputs and outputs must use the `.daxstrategy` extension. A signing output cannot overwrite
any existing file; omitting `--output` still permits the intended safe in-place bundle rewrite. The
final move is non-overwriting in every other case, including junction, symlink, and hard-link aliases.

## Verify and inspect

```powershell
daxalgo-bundle verify --bundle .\signed.daxstrategy `
  --public-key .\publisher-public.pem --publisher acme-research --key-id acme-2026

daxalgo-bundle inspect --bundle .\unsigned-or-signed.daxstrategy
```

Both commands show the content root, identity, payload inventory, and signature status. `inspect` reports
signature presence without claiming authenticity. `verify` requires a public key explicitly bound to
the manifest's stable publisher id; a key trusted for another publisher cannot verify it.
`--public-key` accepts a PEM file, standard input (`-`), or public PEM text; private key material is
always rejected.

Exit codes are `0` for success, `2` for command usage, `3` for bundle validation, `4` for key/signature
failure, and `5` for I/O failure. Other unexpected failures return `1`.
