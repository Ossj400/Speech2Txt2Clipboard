using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SpeechToTextAI;

internal sealed class OwnedServerProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;

    private OwnedServerProcess(SafeFileHandle job, SafeFileHandle process)
    {
        _job = job;
        _process = process;
    }

    public bool HasExited
    {
        get
        {
            uint result = WaitForSingleObject(_process, 0);
            if (result == 0) return true;
            if (result == 0x102) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static OwnedServerProcess Start(string batchPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Uruchamianie serwera MOSS wymaga systemu Windows.");

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, 9, buffer, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var commandLine = new StringBuilder($"\"{cmd}\" /c \"{batchPath}\"");
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = 1,
                ShowWindow = 2
            };

            if (!CreateProcess(cmd, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0x00000004 | 0x00000010, IntPtr.Zero, Path.GetDirectoryName(batchPath),
                ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var process = new SafeFileHandle(info.Process, true);
            using var thread = new SafeFileHandle(info.Thread, true);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (ResumeThread(thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new OwnedServerProcess(job, process);
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _job.Dispose();
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}