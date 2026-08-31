using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityWebBridgeLeaseTests
    {
        [Test]
        public void LeaseRequiresExactIssuerConnectionAndBothGenerations()
        {
            var issuer = new object();
            var connection = new object();
            var lease = new ThreeUnityWebBridgeLease(issuer, connection, 4, 7);

            Assert.That(lease.Matches(issuer, connection, 4, 7), Is.True);
            Assert.That(lease.Matches(new object(), connection, 4, 7), Is.False);
            Assert.That(lease.Matches(issuer, new object(), 4, 7), Is.False);
            Assert.That(lease.Matches(issuer, connection, 5, 7), Is.False);
            Assert.That(lease.Matches(issuer, connection, 4, 8), Is.False);
        }

        [Test]
        public void RetiredLeaseCannotWriteIntoReplacementConnection()
        {
            var gameObject = new GameObject("bridge-lease-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            object firstConnection = null;
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var firstPage, out var firstPipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(firstPage, firstPipe), Is.True);
                firstConnection = InstallConnection(launcher, firstPage, firstPipe, out var firstLease);
                Assert.That(launcher.SendToWeb(firstLease, "first"), Is.True);

                Assert.That(lifecycle.TryReportFault(
                    firstPage,
                    firstPipe,
                    ThreeUnityHostFaultReason.HostExited), Is.True);
                Assert.That(lifecycle.CompleteRetirement(firstPage, firstPipe, 0), Is.True);
                Assert.That(lifecycle.TryBeginLaunch(250, true, out var nextPage, out var nextPipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(nextPage, nextPipe), Is.True);
                InstallConnection(launcher, nextPage, nextPipe, out var nextLease);

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "THREE_UNITY_WEB_BRIDGE_GENERATION_REJECTED.*direction=outbound-lease"));
                Assert.That(launcher.SendToWeb(firstLease, "must-not-cross"), Is.False);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "THREE_UNITY_WEB_BRIDGE_GENERATION_REJECTED.*direction=outbound-latest-lease"));
                Assert.That(launcher.SendLatestToWeb(firstLease, "state", "must-not-cross"), Is.False);

                Assert.That(launcher.SendToWeb(nextLease, "current"), Is.True);
                Assert.That(launcher.SendLatestToWeb(nextLease, "state", "current"), Is.True);
                var metrics = launcher.GetTransportMetrics();
                Assert.That(metrics.Outbound.PendingReliable, Is.EqualTo(1));
                Assert.That(metrics.Outbound.PendingLatest, Is.EqualTo(1));
            }
            finally
            {
                DisposeConnection(firstConnection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static ThreeUnityWebBridgeLifecycle GetLifecycle(ThreeUnityWebBridgeLauncher launcher)
        {
            return (ThreeUnityWebBridgeLifecycle)typeof(ThreeUnityWebBridgeLauncher)
                .GetField("lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(launcher);
        }

        private static object InstallConnection(
            ThreeUnityWebBridgeLauncher launcher,
            long pageGeneration,
            long connectionGeneration,
            out ThreeUnityWebBridgeLease lease)
        {
            var launcherType = typeof(ThreeUnityWebBridgeLauncher);
            var connectionType = launcherType.GetNestedType(
                "ConnectionResources",
                BindingFlags.NonPublic);
            var connection = Activator.CreateInstance(
                connectionType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    pageGeneration,
                    connectionGeneration,
                    "three-unity-lease-test-" + Guid.NewGuid().ToString("N"),
                },
                null);
            lease = new ThreeUnityWebBridgeLease(
                launcherType.GetField("leaseIssuerIdentity", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(launcher),
                connectionType.GetProperty("LeaseIdentity").GetValue(connection),
                pageGeneration,
                connectionGeneration);
            connectionType.GetProperty("Lease").SetValue(connection, lease);
            launcherType.GetField("activeConnection", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(launcher, connection);
            return connection;
        }

        private static void DisposeConnection(object connection)
        {
            if (connection == null)
                return;
            connection.GetType().GetMethod("Dispose").Invoke(connection, new object[] { 0 });
        }
    }
}
