using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>
/// Validates bundle payload shape as metadata only. This is a format and packaging policy check,
/// not a sandbox or a statement that accepted managed code is safe to execute.
/// </summary>
internal static class StrategyBundlePayloadPolicy
{
    private static readonly HashSet<string> ForbiddenNonExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".com", ".msi", ".ps1", ".psm1", ".bat", ".cmd",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw",
        ".sh", ".bash", ".zsh", ".ksh", ".fish", ".lnk", ".jar", ".scr", ".cpl",
        ".sys",
    };

    private static readonly HashSet<string> ForbiddenEngineAssemblyReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "DaxAlgo.Sdk.Wpf",
    };

    public static void Validate(
        string path,
        StrategyBundlePayloadRole role,
        ReadOnlyMemory<byte> content)
    {
        if (role is StrategyBundlePayloadRole.Engine or
            StrategyBundlePayloadRole.WindowsUi or
            StrategyBundlePayloadRole.ManagedDependency)
        {
            ValidateManagedAssembly(path, role, content);
            return;
        }

        var extension = Path.GetExtension(path);
        if (ForbiddenNonExecutableExtensions.Contains(extension))
            Reject(path, $"role '{StrategyBundleManifestCodec.RoleToWire(role)}' may not contain executable or script-style extension '{extension}'.");
        if (LooksLikePortableExecutable(content))
            Reject(path, $"role '{StrategyBundleManifestCodec.RoleToWire(role)}' may not contain Portable Executable content, even under a non-code extension.");
    }

    private static void ValidateManagedAssembly(
        string path,
        StrategyBundlePayloadRole role,
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                Reject(path, "is not a managed assembly with metadata.");

            var corHeader = pe.PEHeaders.CorHeader;
            if (corHeader is null)
                Reject(path, "has no CLR header and may be native or AOT code.");
            if ((corHeader.Flags & CorFlags.ILOnly) == 0)
                Reject(path, "is not IL-only and may be mixed-mode or native code.");
            if ((corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
                Reject(path, "declares a native entry point.");
            if (corHeader.ManagedNativeHeaderDirectory.RelativeVirtualAddress != 0 ||
                corHeader.ManagedNativeHeaderDirectory.Size != 0)
                Reject(path, "contains a ReadyToRun managed-native header.");

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                Reject(path, "contains managed metadata but is not an assembly.");

            var definition = metadata.GetAssemblyDefinition();
            var identity = metadata.GetString(definition.Name);
            if (IsHostAssemblyIdentity(identity))
                Reject(path, $"bundles host-owned assembly identity '{identity}'.");

            if (role is StrategyBundlePayloadRole.Engine or StrategyBundlePayloadRole.ManagedDependency)
                ValidateEngineSafeReferences(path, role, metadata);
        }
        catch (StrategyBundleValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' is not a valid managed IL-only assembly: {ex.Message}",
                ex);
        }
    }

    public static void ValidateManagedAssemblyIdentities(IReadOnlyDictionary<string, byte[]> payloads)
    {
        _ = DescribeManagedAssemblies(payloads);
    }

    public static IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> DescribeManagedAssemblies(
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<StrategyBundleManagedAssemblyDescriptor>();
        foreach (var payload in payloads.OrderBy(static payload => payload.Key, StringComparer.Ordinal))
        {
            if (!IsManagedPayloadPath(payload.Key))
                continue;

            var descriptor = ReadManagedAssemblyDescriptor(payload.Key, payload.Value);
            if (!identities.TryAdd(descriptor.Name, payload.Key))
                Reject(
                    payload.Key,
                    $"duplicates managed assembly identity '{descriptor.Name}' from payload '{identities[descriptor.Name]}'.");
            descriptors.Add(descriptor);
        }

        ValidateManagedDependencyClosure(descriptors);
        return descriptors;
    }

    private static void ValidateManagedDependencyClosure(
        IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> assemblies)
    {
        var bundledNames = assemblies
            .Select(static assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            foreach (var reference in assembly.References)
            {
                if (bundledNames.Contains(reference) || StrategyBundleExternalAssemblyPolicy.IsAllowed(reference))
                    continue;

                RejectGraph(
                    $"entry '{assembly.Path}' references private assembly '{reference}', but that assembly is not bundled.");
            }
        }
    }

    public static void ValidateManagedAssemblyGraph(
        IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> declared,
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var actual = DescribeManagedAssemblies(payloads);
        if (declared.Count != actual.Count)
            RejectGraph("does not contain exactly one descriptor for every managed payload.");
        for (var index = 0; index < actual.Count; index++)
        {
            var expected = declared[index];
            var observed = actual[index];
            if (!string.Equals(expected.Path, observed.Path, StringComparison.Ordinal) ||
                !string.Equals(expected.Name, observed.Name, StringComparison.Ordinal) ||
                !expected.References.SequenceEqual(observed.References, StringComparer.Ordinal))
            {
                RejectGraph($"entry '{expected.Path}' does not match its managed PE metadata.");
            }
        }
    }

    private static void ValidateEngineSafeReferences(
        string path,
        StrategyBundlePayloadRole role,
        MetadataReader metadata)
    {
        var roleName = StrategyBundleManifestCodec.RoleToWire(role);
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (IsForbiddenEngineAssemblyReference(name))
                Reject(path, $"role '{roleName}' references UI/WPF assembly '{name}'.");
        }

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var namespaceName = reference.Namespace.IsNil
                ? string.Empty
                : metadata.GetString(reference.Namespace);
            if (IsForbiddenEngineNamespace(namespaceName))
                Reject(path, $"role '{roleName}' references UI/WPF namespace '{namespaceName}'.");
        }
    }

    private static StrategyBundleManagedAssemblyDescriptor ReadManagedAssemblyDescriptor(
        string path,
        byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var metadata = pe.GetMetadataReader();
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            var references = metadata.AssemblyReferences
                .Select(metadata.GetAssemblyReference)
                .Select(reference => metadata.GetString(reference.Name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static reference => reference, StringComparer.Ordinal)
                .ToArray();
            return new StrategyBundleManagedAssemblyDescriptor(path, name, references);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' has unreadable managed assembly identity metadata: {ex.Message}",
                ex);
        }
    }

    private static bool IsManagedPayloadPath(string path) =>
        path.StartsWith("payload/engine/", StringComparison.Ordinal) ||
        path.StartsWith("payload/windows/", StringComparison.Ordinal) ||
        path.StartsWith("payload/deps/", StringComparison.Ordinal);

    private static bool LooksLikePortableExecutable(ReadOnlyMemory<byte> content)
    {
        var bytes = content.Span;
        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
            return true;

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return pe.PEHeaders.PEHeader is not null || pe.HasMetadata;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsHostAssemblyIdentity(string name) =>
        StrategyBundleExternalAssemblyPolicy.IsAllowed(name);

    internal static bool IsForbiddenEngineAssemblyReference(string name) =>
        ForbiddenEngineAssemblyReferences.Contains(name) ||
        string.Equals(name, "Accessibility", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Microsoft.VisualBasic.Forms", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "PresentationUI", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "System.Printing", StringComparison.OrdinalIgnoreCase) ||
        IsAssemblyNameOrChild(name, "System.Windows") ||
        IsAssemblyNameOrChild(name, "PresentationFramework") ||
        IsAssemblyNameOrChild(name, "System.Windows.Forms") ||
        IsAssemblyNameOrChild(name, "WindowsFormsIntegration") ||
        IsAssemblyNameOrChild(name, "ReachFramework") ||
        IsAssemblyNameOrChild(name, "UIAutomation") ||
        IsAssemblyNameOrChild(name, "TradingTerminal.UI") ||
        IsAssemblyNameOrChild(name, "MahApps") ||
        IsAssemblyNameOrChild(name, "ControlzEx") ||
        IsAssemblyNameOrChild(name, "Microsoft.Xaml.Behaviors") ||
        IsAssemblyNameOrChild(name, "Microsoft.UI") ||
        IsAssemblyNameOrChild(name, "Avalonia") ||
        IsAssemblyNameOrChild(name, "ICSharpCode.AvalonEdit") ||
        IsAssemblyNameOrChild(name, "ScottPlot") ||
        IsAssemblyNameOrChild(name, "SkiaSharp.Views") ||
        IsAssemblyNameOrChild(name, "CommunityToolkit.Mvvm");

    private static bool IsForbiddenEngineNamespace(string name) =>
        IsNamespaceOrChild(name, "System.Windows") ||
        IsNamespaceOrChild(name, "System.Windows.Forms") ||
        IsNamespaceOrChild(name, "DaxAlgo.Sdk.Wpf") ||
        IsNamespaceOrChild(name, "TradingTerminal.UI") ||
        IsNamespaceOrChild(name, "MahApps") ||
        IsNamespaceOrChild(name, "ControlzEx") ||
        IsNamespaceOrChild(name, "Microsoft.Xaml.Behaviors") ||
        IsNamespaceOrChild(name, "Microsoft.UI") ||
        IsNamespaceOrChild(name, "Avalonia") ||
        IsNamespaceOrChild(name, "ICSharpCode.AvalonEdit") ||
        IsNamespaceOrChild(name, "ScottPlot") ||
        IsNamespaceOrChild(name, "SkiaSharp.Views") ||
        IsNamespaceOrChild(name, "CommunityToolkit.Mvvm");

    private static bool IsAssemblyNameOrChild(string value, string root) =>
        string.Equals(value, root, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsNamespaceOrChild(string value, string root) =>
        string.Equals(value, root, StringComparison.Ordinal) ||
        value.StartsWith(root + ".", StringComparison.Ordinal);

    [DoesNotReturn]
    private static void Reject(string path, string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Payload '{path}' {reason}");

    [DoesNotReturn]
    private static void RejectGraph(string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Managed assembly graph {reason}");
}
