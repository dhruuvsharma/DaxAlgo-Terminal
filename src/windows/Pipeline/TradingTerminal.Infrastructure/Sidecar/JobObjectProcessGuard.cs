using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TradingTerminal.Infrastructure.Sidecar;

/// <summary>
/// Wraps a Windows Job Object configured with <c>KILL_ON_JOB_CLOSE</c> so any child process assigned to
/// it is terminated when the job handle closes — i.e. when the parent (this app) exits, even on a crash
/// or a kill from Task Manager. Callers that require fail-closed ownership can inspect
/// <see cref="IsConfigured"/> and the result of <see cref="TryAssign"/> before proceeding.
/// </summary>
internal sealed class JobObjectProcessGuard : IDisposable
{
    private IntPtr _job;

    /// <summary>True off Windows (where process-tree kill is used), or when KILL_ON_JOB_CLOSE is active.</summary>
    public bool IsConfigured =>
        !OperatingSystem.IsWindows() ||
        Interlocked.CompareExchange(ref _job, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;

    public JobObjectProcessGuard()
    {
        // Windows-only mechanism. On Linux/macOS the guard stays inert (_job == Zero); teardown
        // falls back to SidecarHostService's cross-platform Kill(entireProcessTree: true).
        if (!OperatingSystem.IsWindows()) return;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var length = Marshal.SizeOf(info);
        var ptr = IntPtr.Zero;
        var configured = false;
        try
        {
            ptr = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(info, ptr, false);
            configured = SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length);
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            if (!configured) CloseHandle(job);
        }
        if (configured) _job = job;
    }

    /// <summary>Assigns a process to the job so it dies when this guard (the app) goes away.</summary>
    public bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var job = Interlocked.CompareExchange(ref _job, IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(job, process.Handle); }
        catch { return false; }
    }

    public void Dispose() => Close();

    private void Close()
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    // ── Win32 ─────────────────────────────────────────────────────────────────────────────────────

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
