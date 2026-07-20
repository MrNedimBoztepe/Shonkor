// Licensed to Shonkor under the MIT License.

using System.Runtime.InteropServices;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// OS-level "kill the sidecar if the host dies" safety net — belt-and-suspenders with the explicit
/// <see cref="System.IAsyncDisposable"/> teardown and the sidecar's own stdin-EOF self-exit.
/// <para>
/// On Windows the child is assigned to a Job Object created with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the job handle closes (host process exit, even a crash),
/// the OS terminates the child, so a killed host never leaks an orphaned node process.
/// </para>
/// <para>
/// On non-Windows platforms this is a no-op: the equivalent guarantee is provided by the sidecar exiting
/// when its stdin closes on parent death (see <c>sidecar/index.js</c>). A native
/// <c>prctl(PR_SET_PDEATHSIG)</c> would require injecting code into the child before <c>exec</c>, which the
/// plain <c>Process.Start</c> launch cannot do; the stdin-EOF path covers the same case portably.
/// </para>
/// </summary>
internal sealed class ChildProcessGuard : IDisposable
{
    private IntPtr _jobHandle = IntPtr.Zero;

    private ChildProcessGuard(IntPtr jobHandle) => _jobHandle = jobHandle;

    /// <summary>
    /// Creates a guard for <paramref name="process"/>. Returns a no-op guard on non-Windows or if any Win32
    /// call fails — the guard is a safety net, never a hard dependency, so failure to arm it is non-fatal.
    /// </summary>
    public static ChildProcessGuard? TryCreate(System.Diagnostics.Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return null;

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extended, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(job);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                return null;
            }

            return new ChildProcessGuard(job);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_jobHandle == IntPtr.Zero) return;
        // Closing the job handle terminates the assigned child (KILL_ON_JOB_CLOSE).
        try { CloseHandle(_jobHandle); } catch { /* best effort */ }
        _jobHandle = IntPtr.Zero;
    }

    // ---- Win32 interop ----

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
