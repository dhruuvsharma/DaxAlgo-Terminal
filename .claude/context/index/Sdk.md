# index/Sdk — per-file index (Windows tree)

Generated from the current source tree. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Sdk/DaxAlgo.Sdk/AuthoredPlugin.cs` | 162 | win | DaxAlgo.Sdk | B I P | Y | The author wrote a complete hand-written window: metadata, a view-model, a view. |
| `src/windows/Sdk/DaxAlgo.Sdk/IPluginRegistrar.cs` | 33 | win | DaxAlgo.Sdk | B I P | Y | The host service collection the plugin registers its strategy / view / |
| `src/windows/Sdk/DaxAlgo.Sdk/IStrategyEngineFactory.cs` | 26 | win | DaxAlgo.Sdk | B I P | Y | Declarative tunables used by live editors, backtests, and optimizers. |
| `src/windows/Sdk/DaxAlgo.Sdk/IStrategyPlugin.cs` | 28 | win | DaxAlgo.Sdk | B I P | Y | Human-readable plugin name (logging + the future marketplace UI). |
| `src/windows/Sdk/DaxAlgo.Sdk/SdkInfo.cs` | 18 | win | DaxAlgo.Sdk | B I P | Y | Semantic version of this SDK build. Bump on any breaking change to |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/CanonicalJson.cs` | 59 | win | DaxAlgo.Strategy.Bundle | B I P | Y | Minimal JSON string/number encoding with a frozen escape algorithm. It deliberately avoids |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/DaxStrategyBundle.cs` | 328 | win | DaxAlgo.Strategy.Bundle | B I P | Y | Creates and verifies passive .daxstrategy archives without loading any payload assembly. |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleArchive.cs` | 402 | win | DaxAlgo.Strategy.Bundle | B I P | Y | DSSE PAE domain-separates the payload type and both byte lengths from the |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleEnginePolicy.cs` | 256 | win | DaxAlgo.Strategy.Bundle | B I P | Y | Validates the manifest-named factory from metadata without loading strategy code. |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleExternalAssemblyPolicy.cs` | 249 | win | DaxAlgo.Strategy.Bundle | B I P | Y | Frozen v1 list of assemblies supplied by the .NET 9 Windows shared |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleLimitOptions.cs` | 60 | win | DaxAlgo.Strategy.Bundle | B I P | Y |  |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleManifestCodec.cs` | 625 | win | DaxAlgo.Strategy.Bundle | B I P | Y |  |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleModels.cs` | 215 | win | DaxAlgo.Strategy.Bundle | B I P | Y | A repeatable source for one payload. The bundle packer owns and disposes |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePath.cs` | 93 | win | DaxAlgo.Strategy.Bundle | B I P | Y |  |
| `src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePayloadPolicy.cs` | 300 | win | DaxAlgo.Strategy.Bundle | B I P | Y | Validates bundle payload shape as metadata only. This is a format and |
