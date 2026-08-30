using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using Microsoft.Win32.SafeHandles;
#endif

namespace ThreeUnity.Bridge
{
    /// <summary>
    /// Owns a Windows Job Object configured to terminate every assigned Host when
    /// the owning Unity process closes its last Job handle. Keep one instance for
    /// the complete launcher lifetime and assign each physical Host generation to it.
    /// </summary>
    public sealed class ThreeUnityWindowsHostJob : IDisposable
    {
        private readonly object syncRoot = new object();
        private int disposed;
        private int assignedProcessCount;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private SafeJobHandle jobHandle;
#endif

        public ThreeUnityWindowsHostJob()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var rawHandle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
                throw CreateWin32Exception("CreateJobObjectW failed");

            var createdHandle = new SafeJobHandle(rawHandle);
            try
            {
                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
                    },
                };

                var informationLength = (uint)Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
                if (!NativeMethods.SetInformationJobObject(
                    createdHandle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ref limits,
                    informationLength))
                {
                    throw CreateWin32Exception(
                        "SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) failed");
                }

                jobHandle = createdHandle;
                createdHandle = null;
            }
            finally
            {
                createdHandle?.Dispose();
            }
#endif
        }

        /// <summary>Whether this build can create and assign a Windows Job Object.</summary>
        public static bool IsSupported
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return true;
#else
                return false;
#endif
            }
        }

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        /// <summary>
        /// Diagnostic state for tests and launcher telemetry. A false value means
        /// the native handle is absent or has already been closed.
        /// </summary>
        public bool HasOpenNativeHandle
        {
            get
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                lock (syncRoot)
                    return jobHandle != null && !jobHandle.IsInvalid && !jobHandle.IsClosed;
#else
                return false;
#endif
            }
        }

        /// <summary>The number of successful process assignments over this Job's lifetime.</summary>
        public int AssignedProcessCount => Volatile.Read(ref assignedProcessCount);

        /// <summary>
        /// Current number of live processes owned by this Job. The launcher uses
        /// this as the authoritative zero-process fence before starting a new
        /// physical Host generation.
        /// </summary>
        public int ActiveProcessCount
        {
            get
            {
                lock (syncRoot)
                {
                    ThrowIfDisposed();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                    if (!NativeMethods.QueryInformationJobObject(
                        jobHandle,
                        JobObjectInfoType.BasicAccountingInformation,
                        out var accounting,
                        (uint)Marshal.SizeOf(typeof(JobObjectBasicAccountingInformation)),
                        IntPtr.Zero))
                    {
                        throw CreateWin32Exception(
                            "QueryInformationJobObject(BasicAccountingInformation) failed");
                    }
                    return checked((int)accounting.ActiveProcesses);
#else
                    throw new PlatformNotSupportedException(
                        "ThreeUnityWindowsHostJob is only available in Windows Editor or Windows Player builds.");
#endif
                }
            }
        }

        /// <summary>
        /// Assigns a process owned by the caller. On Windows, any native assignment
        /// failure is surfaced as a Win32Exception containing the process id and
        /// original GetLastWin32Error value.
        /// </summary>
        public void Assign(Process process)
        {
            if (process == null)
                throw new ArgumentNullException(nameof(process));

            lock (syncRoot)
            {
                ThrowIfDisposed();

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                IntPtr processHandle;
                int processId;
                try
                {
                    processId = process.Id;
                    processHandle = process.Handle;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Cannot obtain the ThreeUnity Web Host process handle before Job assignment.",
                        exception);
                }

                if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle))
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        errorCode,
                        "AssignProcessToJobObject failed for ThreeUnity Web Host pid="
                        + processId
                        + " (win32="
                        + errorCode
                        + ").");
                }

                Interlocked.Increment(ref assignedProcessCount);
#else
                throw new PlatformNotSupportedException(
                    "ThreeUnityWindowsHostJob is only available in Windows Editor or Windows Player builds.");
#endif
            }
        }

        /// <summary>
        /// Requests asynchronous termination of every process in this Job. Callers
        /// must continue polling <see cref="ActiveProcessCount"/> until it reaches
        /// zero before disposing the handle or launching a replacement generation.
        /// </summary>
        public void Terminate()
        {
            lock (syncRoot)
            {
                ThrowIfDisposed();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                if (!NativeMethods.TerminateJobObject(jobHandle, 1))
                    throw CreateWin32Exception("TerminateJobObject failed");
#else
                throw new PlatformNotSupportedException(
                    "ThreeUnityWindowsHostJob is only available in Windows Editor or Windows Player builds.");
#endif
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                var handleToClose = jobHandle;
                jobHandle = null;
                handleToClose?.Dispose();
#endif
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(ThreeUnityWindowsHostJob));
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static Win32Exception CreateWin32Exception(string operation)
        {
            var errorCode = Marshal.GetLastWin32Error();
            return new Win32Exception(
                errorCode,
                operation + " (win32=" + errorCode + ").");
        }

        private enum JobObjectInfoType
        {
            BasicAccountingInformation = 1,
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            public const uint JobObjectLimitKillOnJobClose = 0x00002000;

            [DllImport(
                "kernel32.dll",
                EntryPoint = "CreateJobObjectW",
                CharSet = CharSet.Unicode,
                ExactSpelling = true,
                SetLastError = true)]
            public static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                SafeJobHandle job,
                JobObjectInfoType informationClass,
                ref JobObjectExtendedLimitInformation information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(
                SafeJobHandle job,
                IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool TerminateJobObject(
                SafeJobHandle job,
                uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool QueryInformationJobObject(
                SafeJobHandle job,
                JobObjectInfoType informationClass,
                out JobObjectBasicAccountingInformation information,
                uint informationLength,
                IntPtr returnLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
#endif
    }
}
