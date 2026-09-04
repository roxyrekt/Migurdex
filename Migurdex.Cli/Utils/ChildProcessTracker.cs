using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Migurdex.Cli.Utils;

public static class ChildProcessTracker
{
    private static readonly IntPtr _jobHandle;

    static ChildProcessTracker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _jobHandle = CreateJobObject(IntPtr.Zero, null);

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        var length          = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var extendedInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

            if (!SetInformationJobObject(_jobHandle,
                                         JobObjectInfoClass.ExtendedLimitInformation,
                                         extendedInfoPtr,
                                         (uint) length))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    public static void Track(Process process)
    {
        if (!OperatingSystem.IsWindows() || _jobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
            }
        }
        catch
        {
            // ignored
        }
    }

#region Win32 API

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob,
        JobObjectInfoClass                                    jobObjectInfoClass,
        IntPtr                                                lpJobObjectInfo,
        uint                                                  cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long           PerProcessUserTimeLimit;
        public long           PerJobUserTimeLimit;
        public JOBOBJECTLIMIT LimitFlags;
        public nuint          MinimumWorkingSetSize;
        public nuint          MaximumWorkingSetSize;
        public uint           ActiveProcessLimit;
        public nuint          Affinity;
        public uint           PriorityClass;
        public uint           SchedulingClass;
    }

    [Flags]
    private enum JOBOBJECTLIMIT : uint
    {
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
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
        public IO_COUNTERS                       IoCounters;
        public nuint                             ProcessMemoryLimit;
        public nuint                             JobMemoryLimit;
        public nuint                             PeakProcessMemoryLimit;
        public nuint                             PeakJobMemoryLimit;
    }

#endregion
}
