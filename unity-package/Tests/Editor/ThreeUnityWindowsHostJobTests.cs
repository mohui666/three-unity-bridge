using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityWindowsHostJobTests
    {
        [Test]
        public void DisposeBeforeAnyAssignmentIsIdempotent()
        {
            var job = new ThreeUnityWindowsHostJob();

            Assert.That(job.AssignedProcessCount, Is.Zero);
            Assert.That(job.HasOpenNativeHandle, Is.EqualTo(ThreeUnityWindowsHostJob.IsSupported));
#if UNITY_EDITOR_WIN
            Assert.That(job.ActiveProcessCount, Is.Zero);
            Assert.DoesNotThrow(job.Terminate);
            Assert.That(job.ActiveProcessCount, Is.Zero);
#endif
            Assert.DoesNotThrow(job.Dispose);
            Assert.DoesNotThrow(job.Dispose);
            Assert.That(job.IsDisposed, Is.True);
            Assert.That(job.HasOpenNativeHandle, Is.False);
        }

#if UNITY_EDITOR_WIN
        [Test]
        [Timeout(15000)]
        public void AssignedShortLivedOwnedProcessCanExitNormallyAndJobHandleCloses()
        {
            var pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                Arguments = "127.0.0.1 -n 2 -w 100",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process process = null;
            ThreeUnityWindowsHostJob job = null;
            try
            {
                process = Process.Start(startInfo);
                Assert.That(process, Is.Not.Null);

                job = new ThreeUnityWindowsHostJob();
                Assert.That(job.HasOpenNativeHandle, Is.True);
                Assert.DoesNotThrow(() => job.Assign(process));
                Assert.That(job.AssignedProcessCount, Is.EqualTo(1));

                Assert.That(process.WaitForExit(10000), Is.True);
                Assert.That(process.ExitCode, Is.Zero);

                Assert.DoesNotThrow(job.Dispose);
                Assert.That(job.IsDisposed, Is.True);
                Assert.That(job.HasOpenNativeHandle, Is.False);
                Assert.Throws<ObjectDisposedException>(() => job.Assign(process));
            }
            finally
            {
                job?.Dispose();
                if (process != null)
                {
                    try
                    {
                        if (!process.WaitForExit(1000))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The test-owned process exited between the status check and cleanup.
                    }

                    process.Dispose();
                }
            }
        }

        [Test]
        [Timeout(15000)]
        public void TerminateReachesAuthoritativeZeroBeforeTheJobIsDisposed()
        {
            var pingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = pingPath,
                Arguments = "127.0.0.1 -n 30 -w 1000",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process process = null;
            ThreeUnityWindowsHostJob job = null;
            try
            {
                process = Process.Start(startInfo);
                Assert.That(process, Is.Not.Null);
                job = new ThreeUnityWindowsHostJob();
                job.Assign(process);
                Assert.That(job.ActiveProcessCount, Is.EqualTo(1));

                job.Terminate();
                Assert.That(process.WaitForExit(10000), Is.True);
                Assert.That(
                    SpinWait.SpinUntil(() => job.ActiveProcessCount == 0, 2000),
                    Is.True,
                    "The Job must report zero active processes before its handle is released.");
                Assert.That(job.ActiveProcessCount, Is.Zero);
                Assert.That(job.HasOpenNativeHandle, Is.True);
            }
            finally
            {
                job?.Dispose();
                if (process != null)
                {
                    try
                    {
                        if (!process.WaitForExit(1000))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch (InvalidOperationException) { }
                    process.Dispose();
                }
            }
        }
#endif
    }
}
