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
   86: public static StrategyBundleSignResult Sign(
  102: public static StrategyBundleSignResult Sign(
  139: public static StrategyBundleInspection Inspect(
  148: public static StrategyBundleInspection Inspect(
  158: public static StrategyBundleVerification Verify(
  168: public static StrategyBundleVerification Verify(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleArchive.cs
```cs
   21: public StrategyBundleInspection ToInspection(StrategyBundleSignatureEvidence? evidence = null) => new(
   42: public const string ManifestEntryPath = DaxStrategyBundle.ManifestEntryPath;
   43: public const string PublisherSignatureEntryPath = DaxStrategyBundle.PublisherSignatureEntryPath;
   44: public const string PublisherPayloadType = DaxStrategyBundle.PublisherSignaturePayloadType;
   49: public static byte[] Write(
   96: public static StrategyBundleReadResult Read(Stream input, StrategyBundleLimitOptions limits)
  196: public static byte[] CreateEnvelope(string keyId, byte[] manifestBytes, byte[] signature)
  216: public static byte[] CreatePreAuthenticationEncoding(ReadOnlySpan<byte> payload)
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
   56: public sealed record StrategyBundleManifest(
   66: public const string CurrentFormat = "daxstrategy";
   67: public const int CurrentFormatVersion = 1;
   71: public sealed class StrategyBundlePayloadSource
   75: public StrategyBundlePayloadSource(
   85: public string Path { get; }
   86: public StrategyBundlePayloadRole Role { get; }
   88: public static StrategyBundlePayloadSource FromFile(
  100: public static StrategyBundlePayloadSource FromBytes(
  126: public sealed record StrategyBundlePackRequest(
  133: public sealed record StrategyBundlePublisherKey(
  138: public static StrategyBundlePublisherKey FromEcdsa(string publisherId, string keyId, ECDsa key)
  145: public enum StrategyBundleSignatureStatus
  154: public sealed record StrategyBundleSignatureEvidence(
  161: public const string PublisherAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";
  164: public sealed record StrategyBundleInspection(
  171: public sealed record StrategyBundleVerification(
  175: public bool IsPublisherVerified => PublisherSignature.Status == StrategyBundleSignatureStatus.Verified;
  178: public sealed record StrategyBundlePackResult(
  183: public sealed record StrategyBundleSignResult(
  189: public enum StrategyBundleValidationError
  203: public sealed class StrategyBundleValidationException : Exception
  205: public StrategyBundleValidationException(StrategyBundleValidationError error, string message)
  208: public StrategyBundleValidationException(
  214: public StrategyBundleValidationError Error { get; }
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePath.cs
```cs
   16: public static string NormalizePayloadPath(string path, StrategyBundleLimitOptions limits, bool requireCanonical)
   54: public static string AliasKey(string canonicalPath) =>
   57: public static void AddDistinctFilePath(
```

## src/windows/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePayloadPolicy.cs
```cs
   30: public static void Validate(
   98: public static void ValidateManagedAssemblyIdentities(IReadOnlyDictionary<string, byte[]> payloads)
  103: public static IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> DescribeManagedAssemblies(
  145: public static void ValidateManagedAssemblyGraph(
```
