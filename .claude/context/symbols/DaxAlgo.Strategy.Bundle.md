# DaxAlgo.Strategy.Bundle — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/CanonicalJson.cs
```cs
   16: public static byte[] ToUtf8(StringBuilder json) => StrictUtf8.GetBytes(json.ToString());
   18: public static void AppendString(StringBuilder json, string value)
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/DaxStrategyBundle.cs
```cs
    7: public static class DaxStrategyBundle
    9: public const string FileExtension = ".daxstrategy";
   10: public const string ManifestEntryPath = "bundle.manifest.json";
   11: public const string PublisherSignatureEntryPath = "signatures/publisher.dsse.json";
   12: public const string PublisherSignaturePayloadType = "application/vnd.daxalgo.strategy-manifest.v1+json";
   14: public static StrategyBundlePackResult Pack(
   26: public static StrategyBundlePackResult Pack(
   87: public static StrategyBundleSignResult Sign(
  103: public static StrategyBundleSignResult Sign(
  140: public static StrategyBundleInspection Inspect(
  149: public static StrategyBundleInspection Inspect(
  159: public static StrategyBundleVerification Verify(
  169: public static StrategyBundleVerification Verify(
  268: public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ResolveEngineClosure(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleArchive.cs
```cs
   21: public StrategyBundleInspection ToInspection(StrategyBundleSignatureEvidence? evidence = null) => new(
   42: public const string ManifestEntryPath = DaxStrategyBundle.ManifestEntryPath;
   43: public const string PublisherSignatureEntryPath = DaxStrategyBundle.PublisherSignatureEntryPath;
   44: public const string PublisherPayloadType = DaxStrategyBundle.PublisherSignaturePayloadType;
   49: public static byte[] Write(
   96: public static StrategyBundleReadResult Read(Stream input, StrategyBundleLimitOptions limits)
  197: public static byte[] CreateEnvelope(string keyId, byte[] manifestBytes, byte[] signature)
  217: public static byte[] CreatePreAuthenticationEncoding(ReadOnlySpan<byte> payload)
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleEnginePolicy.cs
```cs
   28: public static void Validate(StrategyBundleEngineEntryPoint entryPoint, ReadOnlyMemory<byte> engine)
  193: public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
  199: public string GetTypeFromDefinition(
  208: public string GetTypeFromReference(
  221: public string GetTypeFromSpecification(
  228: public string GetSZArrayType(string elementType) => $"{elementType}[]";
  229: public string GetArrayType(string elementType, ArrayShape shape) => $"array:{elementType}";
  230: public string GetByReferenceType(string elementType) => $"byref:{elementType}";
  231: public string GetPointerType(string elementType) => $"pointer:{elementType}";
  232: public string GetPinnedType(string elementType) => $"pinned:{elementType}";
  233: public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) =>
  235: public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
  237: public string GetGenericMethodParameter(object? genericContext, int index) => $"method-parameter:{index}";
  238: public string GetGenericTypeParameter(object? genericContext, int index) => $"type-parameter:{index}";
  239: public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleExternalAssemblyPolicy.cs
```cs
  248: public static bool IsAllowed(string name) => Allowed.Contains(name);
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleLimitOptions.cs
```cs
    3: public sealed record StrategyBundleLimitOptions
    5: public static StrategyBundleLimitOptions Default { get; } = new();
    7: public long MaxCompressedBundleBytes { get; init; } = 128L * 1024 * 1024;
    8: public long MaxCompressedEntryBytes { get; init; } = 64L * 1024 * 1024;
    9: public int MaxEntryCount { get; init; } = 128;
   10: public long MaxEntryExpandedBytes { get; init; } = 64L * 1024 * 1024;
   11: public long MaxTotalExpandedBytes { get; init; } = 256L * 1024 * 1024;
   12: public long MaxManifestBytes { get; init; } = 1024 * 1024;
   13: public long MaxSignatureEnvelopeBytes { get; init; } = 2L * 1024 * 1024;
   14: public double MaxCompressionRatio { get; init; } = 100d;
   15: public int MaxPathLength { get; init; } = 240;
   16: public int MaxPathDepth { get; init; } = 8;
   17: public int MaxCapabilities { get; init; } = 64;
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleManifestCodec.cs
```cs
   10: public static StrategyBundleManifest Create(
   25: public static byte[] WriteCanonical(StrategyBundleManifest manifest)
  107: public static StrategyBundleManifest ParseCanonical(
  237: public static StrategyBundleManifest NormalizeAndValidate(
  363: public static string RoleToWire(StrategyBundlePayloadRole role) => role switch
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleModels.cs
```cs
    5: public enum StrategyBundlePayloadRole
   15: public sealed record StrategyBundleIdentity(
   21: public sealed record StrategyBundleCompatibility(
   31: public sealed record StrategyBundleEngineEntryPoint(
   37: public const string CurrentContract = "daxalgo.strategy-engine-factory/1";
   38: public const string CurrentActivation = "public-parameterless-constructor";
   41: public sealed record StrategyBundlePayloadDescriptor(
   51: public sealed record StrategyBundleManagedAssemblyDescriptor(
   60: public sealed record StrategyBundleEngineAssemblyDescriptor(
   68: public sealed record StrategyBundleManifest(
   78: public const string CurrentFormat = "daxstrategy";
   79: public const int CurrentFormatVersion = 1;
   83: public sealed class StrategyBundlePayloadSource
   87: public StrategyBundlePayloadSource(
   97: public string Path { get; }
   98: public StrategyBundlePayloadRole Role { get; }
  100: public static StrategyBundlePayloadSource FromFile(
  112: public static StrategyBundlePayloadSource FromBytes(
  138: public sealed record StrategyBundlePackRequest(
  145: public sealed record StrategyBundlePublisherKey(
  150: public static StrategyBundlePublisherKey FromEcdsa(string publisherId, string keyId, ECDsa key)
  157: public enum StrategyBundleSignatureStatus
  166: public sealed record StrategyBundleSignatureEvidence(
  173: public const string PublisherAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";
  176: public string? KeyFingerprintSha256 { get; init; }
  179: public sealed record StrategyBundleInspection(
  186: public sealed record StrategyBundleVerification(
  190: public bool IsPublisherVerified => PublisherSignature.Status == StrategyBundleSignatureStatus.Verified;
  193: public sealed record StrategyBundlePackResult(
  198: public sealed record StrategyBundleSignResult(
  204: public enum StrategyBundleValidationError
  218: public sealed class StrategyBundleValidationException : Exception
  220: public StrategyBundleValidationException(StrategyBundleValidationError error, string message)
  223: public StrategyBundleValidationException(
  229: public StrategyBundleValidationError Error { get; }
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePath.cs
```cs
   16: public static string NormalizePayloadPath(string path, StrategyBundleLimitOptions limits, bool requireCanonical)
   54: public static string AliasKey(string canonicalPath) =>
   57: public static void AddDistinctFilePath(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePayloadPolicy.cs
```cs
   32: public static void Validate(
  100: public static void ValidateManagedAssemblyIdentities(IReadOnlyDictionary<string, byte[]> payloads)
  105: public static IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> DescribeManagedAssemblies(
  157: public static void ValidateManagedAssemblyGraph(
  177: public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ResolveEngineClosure(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleRuntimePolicy.cs
```cs
   10: public static class StrategyBundleRuntimePolicy
   12: public static StrategyBundleManifest ParseCanonicalManifest(
   19: public static string ComputeContentRoot(ReadOnlySpan<byte> canonicalManifestBytes) =>
   26: public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ValidateEngineImage(
   72: public static bool IsExternalAssemblyAllowed(string simpleName) =>
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleSemanticVersion.cs
```cs
    9: public static string Normalize(string? value, string parameterName)
   18: public static StrategyBundleSemanticVersion Parse(string value, string parameterName)
   40: public int CompareTo(StrategyBundleSemanticVersion other)
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStore.cs
```cs
   11: public sealed class StrategyBundleStore
   25: public StrategyBundleStore(string rootDirectory, StrategyBundleLimitOptions? limits = null)
   54: public string RootDirectory { get; }
   56: public StrategyBundleInstallation Install(
   71: public StrategyBundleInstallation Install(
  103: public StrategyBundleInstallation VerifyInstallation(
  178: public StrategyBundleInstallation Activate(
  196: public StrategyBundleInstallation ResolveActive(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStoreJson.cs
```cs
   15: public const string CurrentSchema = "daxalgo.strategy-activation";
   16: public const int CurrentSchemaVersion = 1;
   21: public static byte[] WriteReceipt(StrategyBundleInstallReceipt receipt)
   62: public static StrategyBundleInstallReceipt ParseReceipt(ReadOnlySpan<byte> bytes)
  134: public static byte[] WriteActivation(StrategyBundleActivationPointer activation)
  151: public static StrategyBundleActivationPointer ParseActivation(ReadOnlySpan<byte> bytes)
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStoreModels.cs
```cs
    4: public enum StrategyBundleTrustMode
   14: public sealed class StrategyBundleInstallPolicy
   18: public StrategyBundleInstallPolicy(
   39: public string HostVersion { get; }
   40: public string SdkVersion { get; }
   41: public StrategyBundleTrustMode TrustMode { get; }
   42: public IReadOnlyList<StrategyBundlePublisherKey> TrustedPublisherKeys => _trustedPublisherKeys
   46: public static StrategyBundleInstallPolicy LocalDevelopment(
   52: public static StrategyBundleInstallPolicy VerifiedPublisher(
   63: public sealed record StrategyBundleInstallReceipt(
   73: public const string CurrentSchema = "daxalgo.strategy-install-receipt";
   74: public const int CurrentSchemaVersion = 1;
   78: public sealed record StrategyBundleInstallation(
   87: public enum StrategyBundleStoreError
   96: public sealed class StrategyBundleStoreException : Exception
   98: public StrategyBundleStoreException(StrategyBundleStoreError error, string message)
  101: public StrategyBundleStoreException(StrategyBundleStoreError error, string message, Exception innerException)
  104: public StrategyBundleStoreError Error { get; }
```
