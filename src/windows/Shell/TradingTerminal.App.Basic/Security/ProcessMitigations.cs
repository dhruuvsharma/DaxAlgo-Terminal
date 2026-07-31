using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingTerminal.App.Security;

internal readonly record struct ProcessMitigationReport(
    bool ImageLoadPolicyApplied,
    bool ControlFlowGuardEnabled,
    bool DynamicCodeAuditEnabled,
    int ImageLoadPolicyError,
    int ControlFlowGuardQueryError,
    int DynamicCodeAuditError)
{
    public bool SmokePassed => ImageLoadPolicyApplied && ControlFlowGuardEnabled;

    public string Failure => !ImageLoadPolicyApplied
        ? $"Windows rejected the required image-load mitigation policy ({FormatError(ImageLoadPolicyError)})."
        : !ControlFlowGuardEnabled
            ? $"The apphost is missing Control Flow Guard ({FormatError(ControlFlowGuardQueryError)})."
            : string.Empty;

    public override string ToString() =>
        $"DaxAlgo process mitigations: image-policy-applied={ImageLoadPolicyApplied}; " +
        $"cfg-enabled={ControlFlowGuardEnabled}; " +
        $"dynamic-code-audit={DynamicCodeAuditEnabled}; dynamic-code-prohibition=not-enabled-by-app; " +
        "binary-signature-policy=unchanged.";

    private static string FormatError(int error) => error == 0
        ? "unknown error"
        : $"Win32 {error}: {new Win32Exception(error).Message}";
}

/// <summary>
/// Applies only mitigations that are compatible with the CLR, WPF, and the terminal's native SDKs.
/// Dynamic-code prohibition is intentionally not enabled because the CLR JIT requires it. The
/// Microsoft-only binary-signature policy is also intentionally not enabled because it rejects
/// third-party Authenticode publishers, including DaxAlgo.
/// </summary>
internal static class ProcessMitigations
{
    private const uint NoRemoteImages = 1u << 0;
    private const uint NoLowMandatoryLabelImages = 1u << 1;
    private const uint RequiredImageLoadFlags = NoRemoteImages | NoLowMandatoryLabelImages;
    private const uint ControlFlowGuardEnabled = 1u << 0;
    private const uint ProhibitDynamicCode = 1u << 0;
    private const uint AuditProhibitDynamicCode = 1u << 3;

    public static ProcessMitigationReport ApplyEarly()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            var unsupported = new ProcessMitigationReport(false, false, false, 50, 50, 50);
            Trace.TraceError(unsupported.ToString());
            return unsupported;
        }

        var currentImagePolicy = default(MitigationPolicyFlags);
        var initialImageQuerySucceeded = GetProcessMitigationPolicy(
            GetCurrentProcess(),
            ProcessMitigationPolicy.ProcessImageLoadPolicy,
            ref currentImagePolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        var imagePolicyAlreadyApplied = initialImageQuerySucceeded &&
                                        (currentImagePolicy.Flags & RequiredImageLoadFlags) == RequiredImageLoadFlags;
        var requestedImagePolicy = new MitigationPolicyFlags
        {
            // Preserve any stricter machine policy while adding the two compatible requirements.
            Flags = (initialImageQuerySucceeded ? currentImagePolicy.Flags : 0) | RequiredImageLoadFlags,
        };
        var setSucceeded = imagePolicyAlreadyApplied || SetProcessMitigationPolicy(
            ProcessMitigationPolicy.ProcessImageLoadPolicy,
            ref requestedImagePolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        var setError = setSucceeded ? 0 : Marshal.GetLastWin32Error();

        currentImagePolicy = default;
        var imageQuerySucceeded = GetProcessMitigationPolicy(
            GetCurrentProcess(),
            ProcessMitigationPolicy.ProcessImageLoadPolicy,
            ref currentImagePolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        var imageQueryError = imageQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
        var imagePolicyApplied = imageQuerySucceeded &&
                                 (currentImagePolicy.Flags & RequiredImageLoadFlags) == RequiredImageLoadFlags;

        var controlFlowGuardPolicy = default(MitigationPolicyFlags);
        var cfgQuerySucceeded = GetProcessMitigationPolicy(
            GetCurrentProcess(),
            ProcessMitigationPolicy.ProcessControlFlowGuardPolicy,
            ref controlFlowGuardPolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        var cfgQueryError = cfgQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
        var cfgEnabled = cfgQuerySucceeded &&
                         (controlFlowGuardPolicy.Flags & ControlFlowGuardEnabled) != 0;

        var dynamicCodePolicy = default(MitigationPolicyFlags);
        var dynamicQuerySucceeded = GetProcessMitigationPolicy(
            GetCurrentProcess(),
            ProcessMitigationPolicy.ProcessDynamicCodePolicy,
            ref dynamicCodePolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        var dynamicAuditError = dynamicQuerySucceeded ? 0 : Marshal.GetLastWin32Error();
        if (!dynamicQuerySucceeded || (dynamicCodePolicy.Flags & ProhibitDynamicCode) == 0)
        {
            // Audit bit 3 records attempted dynamic-code creation without blocking the CLR JIT.
            // Bit 0 (ProhibitDynamicCode) is never added here.
            var requestedDynamicPolicy = new MitigationPolicyFlags
            {
                Flags = (dynamicQuerySucceeded ? dynamicCodePolicy.Flags : 0) | AuditProhibitDynamicCode,
            };
            var dynamicSetSucceeded = SetProcessMitigationPolicy(
                ProcessMitigationPolicy.ProcessDynamicCodePolicy,
                ref requestedDynamicPolicy,
                (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
            if (!dynamicSetSucceeded)
                dynamicAuditError = Marshal.GetLastWin32Error();
        }

        dynamicCodePolicy = default;
        dynamicQuerySucceeded = GetProcessMitigationPolicy(
            GetCurrentProcess(),
            ProcessMitigationPolicy.ProcessDynamicCodePolicy,
            ref dynamicCodePolicy,
            (nuint)Marshal.SizeOf<MitigationPolicyFlags>());
        if (!dynamicQuerySucceeded && dynamicAuditError == 0)
            dynamicAuditError = Marshal.GetLastWin32Error();
        var dynamicAuditEnabled = dynamicQuerySucceeded &&
                                  (dynamicCodePolicy.Flags & AuditProhibitDynamicCode) != 0;

        var report = new ProcessMitigationReport(
            imagePolicyApplied,
            cfgEnabled,
            dynamicAuditEnabled,
            setError != 0 ? setError : imageQueryError,
            cfgQueryError,
            dynamicAuditError);
        if (report.SmokePassed)
            Trace.TraceInformation(report.ToString());
        else
            Trace.TraceWarning(report.ToString());
        return report;
    }

    private enum ProcessMitigationPolicy
    {
        ProcessDynamicCodePolicy = 2,
        ProcessControlFlowGuardPolicy = 7,
        ProcessImageLoadPolicy = 10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MitigationPolicyFlags
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(
        ProcessMitigationPolicy mitigationPolicy,
        ref MitigationPolicyFlags buffer,
        nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(
        nint process,
        ProcessMitigationPolicy mitigationPolicy,
        ref MitigationPolicyFlags buffer,
        nuint length);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
