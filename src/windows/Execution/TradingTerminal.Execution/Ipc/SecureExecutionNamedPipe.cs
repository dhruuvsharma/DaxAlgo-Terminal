using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TradingTerminal.Execution.Ipc;

/// <summary>Creates only local-machine named-pipe endpoints for the execution IPC protocol.</summary>
public static class SecureExecutionNamedPipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SingleServerInstance = 1;
    private const uint BufferSize = 4096;

    /// <summary>Stable default pipe name; the DACL supplies current-user isolation.</summary>
    public const string DefaultPipeName = "DaxAlgoTerminal.Execution.v1";

    /// <summary>
    /// Creates a duplex server with a protected DACL containing exactly one allow rule for the
    /// current user's SID. A protected allow-only DACL implicitly denies every other identity.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName = DefaultPipeName)
    {
        ValidatePipeName(pipeName);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User ??
            throw new InvalidOperationException("The current Windows identity has no user SID.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(
            new PipeAccessRule(
                currentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorHandle = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorHandle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            var handle = CreateNamedPipeW(
                $@"\\.\pipe\{pipeName}",
                PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
                PipeRejectRemoteClients,
                SingleServerInstance,
                BufferSize,
                BufferSize,
                defaultTimeoutMilliseconds: 0,
                ref securityAttributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "The local execution named pipe could not be created safely.");
            }

            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            descriptorHandle.Free();
        }
    }

    /// <summary>
    /// Creates a desktop client pinned to the local machine. There is deliberately no server-name
    /// parameter and therefore no API in this slice for a remote named-pipe endpoint.
    /// </summary>
    public static NamedPipeClientStream CreateLocalClient(string pipeName = DefaultPipeName)
    {
        ValidatePipeName(pipeName);
        return new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification,
            HandleInheritability.None);
    }

    private static void ValidatePipeName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 200 || pipeName.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException("The execution pipe name must be a bounded local name.", nameof(pipeName));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeoutMilliseconds,
        ref SecurityAttributes securityAttributes);
}
