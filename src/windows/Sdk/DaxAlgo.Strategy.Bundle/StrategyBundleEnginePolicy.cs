using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>Validates the manifest-named factory from metadata without loading strategy code.</summary>
internal static class StrategyBundleEnginePolicy
{
    private const string FactoryAssembly = "DaxAlgo.Sdk";
    private const string FactoryNamespace = "DaxAlgo.Sdk";
    private const string FactoryName = "IStrategyEngineFactory";
    private const string BacktestStrategyType =
        "TradingTerminal.Core:TradingTerminal.Core.Backtest.IBacktestStrategy";
    private const string ContractType =
        "TradingTerminal.Core:TradingTerminal.Core.Domain.Contract";
    private const string DataRequirementType =
        "TradingTerminal.Core:TradingTerminal.Core.Strategies.StrategyDataRequirement";
    private const string ParameterSchemaType =
        "TradingTerminal.Core:TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema";
    private const string ParametersType =
        "TradingTerminal.Core:TradingTerminal.Core.Strategies.Parameters.StrategyParameters";

    private static readonly FactorySignatureTypeProvider SignatureTypes = new();

    public static void Validate(StrategyBundleEngineEntryPoint entryPoint, ReadOnlyMemory<byte> engine)
    {
        try
        {
            using var stream = new MemoryStream(engine.ToArray(), writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var metadata = pe.GetMetadataReader();
            var separator = entryPoint.TypeName.LastIndexOf('.');
            var expectedNamespace = entryPoint.TypeName[..separator];
            var expectedName = entryPoint.TypeName[(separator + 1)..];

            TypeDefinition? matched = null;
            foreach (var handle in metadata.TypeDefinitions)
            {
                var candidate = metadata.GetTypeDefinition(handle);
                if (metadata.GetString(candidate.Namespace) != expectedNamespace ||
                    metadata.GetString(candidate.Name) != expectedName)
                {
                    continue;
                }

                if (matched is not null)
                    Reject(entryPoint, "is ambiguous in the engine metadata.");
                matched = candidate;
            }

            if (matched is null)
                Reject(entryPoint, "does not exist in the declared engine assembly.");

            var definition = matched.Value;
            if ((definition.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
                (definition.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0)
            {
                Reject(entryPoint, "must be a public, concrete top-level class.");
            }

            var implementsFactory = definition.GetInterfaceImplementations()
                .Select(metadata.GetInterfaceImplementation)
                .Any(implementation => IsFactoryInterface(metadata, implementation.Interface));
            if (!implementsFactory)
                Reject(entryPoint, $"must directly implement {FactoryNamespace}.{FactoryName} from {FactoryAssembly}.");

            if (!HasPublicParameterlessConstructor(metadata, definition))
                Reject(entryPoint, "must expose a public parameterless constructor.");

            RequirePublicInstanceMethod(
                metadata,
                definition,
                "get_Schema",
                ParameterSchemaType,
                []);
            RequirePublicInstanceMethod(
                metadata,
                definition,
                "get_DataRequirement",
                DataRequirementType,
                []);
            RequirePublicInstanceMethod(
                metadata,
                definition,
                "Create",
                BacktestStrategyType,
                [ContractType, ParametersType]);
        }
        catch (StrategyBundleValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Engine entry point '{entryPoint.TypeName}' could not be validated from metadata: {ex.Message}",
                ex);
        }
    }

    private static bool HasPublicParameterlessConstructor(
        MetadataReader metadata,
        TypeDefinition definition)
    {
        foreach (var handle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != ".ctor" ||
                (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                (method.Attributes & MethodAttributes.Static) != 0 ||
                (method.Attributes & MethodAttributes.SpecialName) == 0 ||
                (method.Attributes & MethodAttributes.RTSpecialName) == 0 ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                !HasManagedIlBody(method))
            {
                continue;
            }

            var signature = method.DecodeSignature(SignatureTypes, genericContext: null);
            if (signature.Header.IsInstance &&
                signature.GenericParameterCount == 0 &&
                signature.ReturnType == "System.Void" &&
                signature.ParameterTypes.Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RequirePublicInstanceMethod(
        MetadataReader metadata,
        TypeDefinition definition,
        string name,
        string returnType,
        IReadOnlyList<string> parameterTypes)
    {
        foreach (var handle in definition.GetMethods())
        {
            var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != name ||
                (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                (method.Attributes & MethodAttributes.Virtual) == 0 ||
                (method.Attributes & (MethodAttributes.Static | MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                !HasManagedIlBody(method))
            {
                continue;
            }

            var signature = method.DecodeSignature(SignatureTypes, genericContext: null);
            if (signature.Header.IsInstance &&
                signature.GenericParameterCount == 0 &&
                signature.ReturnType == returnType &&
                signature.ParameterTypes.SequenceEqual(parameterTypes, StringComparer.Ordinal))
            {
                return;
            }
        }

        Reject(
            name,
            $"must expose public instance member '{name}' with the strategy-factory v1 signature.");
    }

    private static bool HasManagedIlBody(MethodDefinition method) =>
        method.RelativeVirtualAddress != 0 &&
        (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL &&
        (method.ImplAttributes & MethodImplAttributes.ManagedMask) == MethodImplAttributes.Managed &&
        (method.ImplAttributes & MethodImplAttributes.InternalCall) == 0;

    private static bool IsFactoryInterface(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;
        var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
        if (metadata.GetString(reference.Namespace) != FactoryNamespace ||
            metadata.GetString(reference.Name) != FactoryName ||
            reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
        return metadata.GetString(assembly.Name) == FactoryAssembly;
    }

    private sealed class FactorySignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "System.Void",
            _ => $"primitive:{typeCode}",
        };

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            return $"engine:{QualifiedName(reader.GetString(type.Namespace), reader.GetString(type.Name))}";
        }

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
                return "unsupported-type-reference";
            var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            return $"{reader.GetString(assembly.Name)}:" +
                   QualifiedName(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => $"{elementType}[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"array:{elementType}";
        public string GetByReferenceType(string elementType) => $"byref:{elementType}";
        public string GetPointerType(string elementType) => $"pointer:{elementType}";
        public string GetPinnedType(string elementType) => $"pinned:{elementType}";
        public string GetModifiedType(string modifierType, string unmodifiedType, bool isRequired) =>
            $"modified:{unmodifiedType}";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"generic:{genericType}";
        public string GetGenericMethodParameter(object? genericContext, int index) => $"method-parameter:{index}";
        public string GetGenericTypeParameter(object? genericContext, int index) => $"type-parameter:{index}";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";

        private static string QualifiedName(string @namespace, string name) =>
            string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    [DoesNotReturn]
    private static void Reject(StrategyBundleEngineEntryPoint entryPoint, string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Engine entry point '{entryPoint.TypeName}' {reason}");

    [DoesNotReturn]
    private static void Reject(string member, string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Engine factory member '{member}' {reason}");
}
