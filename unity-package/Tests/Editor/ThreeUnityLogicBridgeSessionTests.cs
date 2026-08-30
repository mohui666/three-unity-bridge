using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ThreeUnity.Bridge.Logic;
using UnityEngine;
using UnityEngine.TestTools;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityLogicBridgeSessionTests
    {
        [Test]
        public void UnattributableMalformedJsonDoesNotFallbackTheCurrentModule()
        {
            var gameObject = new GameObject("logic-bridge-session-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            bridge.Configure(launcher, "shop-flight-v1");
            Invoke(bridge, "Awake");
            var router = GetField<ThreeUnityLogicSessionRouter>(bridge, "router");

            LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_LOGIC_PROTOCOL_ERROR"));
            Invoke(bridge, "ProcessInbound", router.HostGeneration, "not-json");

            Assert.That(router.CurrentModule.IsFallback, Is.False);
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void LatestStateStreamKeysArePartitionedBySession()
        {
            var first = new ThreeUnityLogicOutgoingMessage(
                "{}",
                "player.state",
                "session-a").StreamKey;
            var second = new ThreeUnityLogicOutgoingMessage(
                "{}",
                "player.state",
                "session-b").StreamKey;
            var legacy = new ThreeUnityLogicOutgoingMessage(
                "{}",
                "player.state",
                null).StreamKey;

            Assert.That(first, Is.EqualTo("session-a:player.state"));
            Assert.That(second, Is.EqualTo("session-b:player.state"));
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(legacy, Is.EqualTo("player.state"));
        }

        [Test]
        public void PlayerFocusAndPauseCallbacksUpdateTheGenericLifecycleState()
        {
            var gameObject = new GameObject("logic-bridge-lifecycle-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            bridge.Configure(launcher, "shop-flight-v1");
            Invoke(bridge, "Awake");
            var router = GetField<ThreeUnityLogicSessionRouter>(bridge, "router");
            var initialRevision = router.ApplicationLifecycleRevision;
            var nextFocus = !router.ApplicationFocused;

            LogAssert.Expect(LogType.Log, new Regex("THREE_UNITY_RUNTIME_LIFECYCLE source=focus"));
            Invoke(bridge, "OnApplicationFocus", nextFocus);
            Assert.That(router.ApplicationFocused, Is.EqualTo(nextFocus));
            Assert.That(router.ApplicationLifecycleRevision, Is.EqualTo(initialRevision + 1));

            Invoke(bridge, "OnApplicationFocus", nextFocus);
            Assert.That(router.ApplicationLifecycleRevision, Is.EqualTo(initialRevision + 1));

            LogAssert.Expect(LogType.Log, new Regex("THREE_UNITY_RUNTIME_LIFECYCLE source=pause"));
            Invoke(bridge, "OnApplicationPause", true);
            Assert.That(router.ApplicationPaused, Is.True);
            Assert.That(router.ApplicationActive, Is.False);
            Assert.That(router.ApplicationLifecycleRevision, Is.EqualTo(initialRevision + 2));
            LogAssert.NoUnexpectedReceived();
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void MalformedMessageFromAnOldHostGenerationIsRejectedBeforeJsonParsing()
        {
            var gameObject = new GameObject("logic-bridge-generation-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            bridge.Configure(launcher, "shop-flight-v1");
            Invoke(bridge, "Awake");
            var router = GetField<ThreeUnityLogicSessionRouter>(bridge, "router");
            var oldGeneration = router.HostGeneration;
            var nextGeneration = oldGeneration + 1;

            LogAssert.Expect(LogType.Log, "THREE_UNITY_LOGIC_TRANSPORT_RESET page=" + nextGeneration);
            Assert.That(bridge.ResetForHostGeneration(nextGeneration), Is.True);
            Invoke(bridge, "ProcessInbound", oldGeneration, "not-json");

            Assert.That(router.GenerationRejected, Is.EqualTo(1));
            Assert.That(router.SessionRejected, Is.EqualTo(0));
            Assert.That(router.CurrentModule.IsFallback, Is.False);
            LogAssert.NoUnexpectedReceived();
            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ReliableOutputSurvivesBackpressureAndAStaleLeaseWithoutReordering()
        {
            var gameObject = new GameObject("logic-bridge-reliable-retry-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            var connections = new List<object>();
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var pipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(page, pipe), Is.True);
                var firstConnection = InstallConnection(launcher, page, pipe, out var firstLease);
                connections.Add(firstConnection);

                var modules = new List<QueuedLogicModule>();
                var router = InstallQueuedRouter(bridge, launcher, modules);
                var module = modules[0];
                var first = Message("test.command", 1);
                var second = Message("test.command", 2);
                module.Enqueue(first);
                module.Enqueue(second);

                for (var index = 0; index < 1024; index++)
                    Assert.That(launcher.SendToWeb(firstLease, "filler-" + index), Is.True);

                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"));
                Invoke(bridge, "FlushOutgoing");

                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.True);
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.EqualTo(first));
                Assert.That(module.Pending, Is.EqualTo(1), "The second reliable message must remain behind the failed head.");

                // Replace the exact connection object without changing its numeric
                // generation. This deterministically expires the retained lease,
                // matching the race where a send fails while a connection retires.
                var replacement = InstallConnection(launcher, page, pipe, out _);
                connections.Add(replacement);
                Invoke(bridge, "FlushOutgoing");

                var outbound = GetOutbound(replacement);
                Assert.That(outbound.TryDequeue(out var retriedFirst), Is.True);
                Assert.That(outbound.TryDequeue(out var followingSecond), Is.True);
                Assert.That(outbound.TryDequeue(out _), Is.False);
                Assert.That(retriedFirst, Is.EqualTo(first));
                Assert.That(followingSecond, Is.EqualTo(second));
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.False);
                Assert.That(module.Pending, Is.Zero);
                Assert.That(router.HostGeneration, Is.EqualTo(page));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                foreach (var connection in connections)
                    DisposeConnection(connection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HostGenerationResetClearsRetainedOutputBeforeFreshTraffic()
        {
            var gameObject = new GameObject("logic-bridge-pending-generation-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            var connections = new List<object>();
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var oldPage, out var oldPipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(oldPage, oldPipe), Is.True);
                var oldConnection = InstallConnection(launcher, oldPage, oldPipe, out var oldLease);
                connections.Add(oldConnection);

                var modules = new List<QueuedLogicModule>();
                var router = InstallQueuedRouter(bridge, launcher, modules);
                var oldPending = Message("old.command", 1);
                modules[0].Enqueue(oldPending);
                modules[0].Enqueue(Message("old.command", 2));
                for (var index = 0; index < 1024; index++)
                    Assert.That(launcher.SendToWeb(oldLease, "filler-" + index), Is.True);

                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"));
                Invoke(bridge, "FlushOutgoing");
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.EqualTo(oldPending));

                Assert.That(
                    lifecycle.TryReportFault(oldPage, oldPipe, ThreeUnityHostFaultReason.HostExited),
                    Is.True);
                Assert.That(lifecycle.CompleteRetirement(oldPage, oldPipe, 0), Is.True);
                Assert.That(lifecycle.TryBeginLaunch(250, true, out var newPage, out var newPipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(newPage, newPipe), Is.True);
                var newConnection = InstallConnection(launcher, newPage, newPipe, out _);
                connections.Add(newConnection);

                LogAssert.Expect(LogType.Log, new Regex(
                    "THREE_UNITY_LOGIC_OUTBOUND_EPOCH_RETIRED.*reason=physical-generation.*retainedPendingDiscarded=1"));
                LogAssert.Expect(LogType.Log, "THREE_UNITY_LOGIC_TRANSPORT_RESET page=" + newPage);
                Assert.That(bridge.ResetForHostGeneration(newPage), Is.True);
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.False);
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.Null);
                Assert.That(router.RetiredOutgoingDiscarded, Is.EqualTo(2));
                Assert.That(modules, Has.Count.EqualTo(2));

                var fresh = Message("fresh.command", 1);
                modules[1].Enqueue(fresh);
                Invoke(bridge, "FlushOutgoing");

                var outbound = GetOutbound(newConnection);
                Assert.That(outbound.TryDequeue(out var actual), Is.True);
                Assert.That(actual, Is.EqualTo(fresh));
                Assert.That(outbound.TryDequeue(out _), Is.False);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                foreach (var connection in connections)
                    DisposeConnection(connection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ReliablePendingRecoversCapacityOnSameConnectionWithoutLossOrDuplication()
        {
            var gameObject = new GameObject("logic-bridge-same-connection-retry-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            object connection = null;
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var pipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(page, pipe), Is.True);
                connection = InstallConnection(launcher, page, pipe, out var lease);
                var outbound = GetOutbound(connection);

                var modules = new List<QueuedLogicModule>();
                InstallQueuedRouter(bridge, launcher, modules);
                var first = Message("test.command", 1);
                var second = Message("test.command", 2);
                modules[0].Enqueue(first);
                modules[0].Enqueue(second);
                for (var index = 0; index < 1024; index++)
                    Assert.That(launcher.SendToWeb(lease, "filler-" + index), Is.True);

                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"));
                Invoke(bridge, "FlushOutgoing");
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.EqualTo(first));

                Assert.That(outbound.TryDequeue(out var firstFiller), Is.True);
                Assert.That(firstFiller, Is.EqualTo("filler-0"));
                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"));
                Invoke(bridge, "FlushOutgoing");
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.EqualTo(second));

                var occurrencesOfFirst = 0;
                for (var index = 1; index < 1024; index++)
                {
                    Assert.That(outbound.TryDequeue(out var filler), Is.True);
                    Assert.That(filler, Is.EqualTo("filler-" + index));
                }
                Assert.That(outbound.TryDequeue(out var recoveredFirst), Is.True);
                if (recoveredFirst == first)
                    occurrencesOfFirst++;
                Assert.That(occurrencesOfFirst, Is.EqualTo(1));
                Assert.That(outbound.TryDequeue(out _), Is.False);

                Invoke(bridge, "FlushOutgoing");
                Assert.That(outbound.TryDequeue(out var recoveredSecond), Is.True);
                Assert.That(recoveredSecond, Is.EqualTo(second));
                Assert.That(outbound.TryDequeue(out _), Is.False);
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.False);
                Assert.That(launcher.GetTransportMetrics().Outbound.ReliableBackpressureRejected, Is.EqualTo(2));
                Assert.That(launcher.GetTransportMetrics().Outbound.ReliableDropped, Is.Zero);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                DisposeConnection(connection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void LogicalRestartPurgesOldOwnerBacklogBeforeFreshReady()
        {
            var gameObject = new GameObject("logic-bridge-owner-restart-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            object connection = null;
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var pipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(page, pipe), Is.True);
                connection = InstallConnection(launcher, page, pipe, out var lease);
                var outbound = GetOutbound(connection);

                var modules = new List<QueuedLogicModule>();
                var router = InstallQueuedRouter(bridge, launcher, modules);
                Invoke(bridge, "ProcessInboundWithLease", lease, Hello("session-a", 0));
                Assert.That(router.ActiveSessionId, Is.EqualTo("session-a"));

                var oldOwner = GetField<object>(bridge, "activeOutboundOwner");
                Assert.That(
                    launcher.SendLatestToWeb(lease, oldOwner, "session-a:test.state", "old-state"),
                    Is.True);
                for (var index = 0; index < 1025; index++)
                    modules[0].Enqueue(Message("old.command", index));
                for (var flush = 0; flush < 4; flush++)
                    Invoke(bridge, "FlushOutgoing");
                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"));
                Invoke(bridge, "FlushOutgoing");
                Assert.That(outbound.Snapshot().PendingReliable, Is.EqualTo(1024));
                Assert.That(outbound.Snapshot().PendingLatest, Is.EqualTo(1));
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.True);

                LogAssert.Expect(LogType.Log, new Regex(
                    "THREE_UNITY_LOGIC_OUTBOUND_EPOCH_RETIRED.*purged=1025"));
                LogAssert.Expect(LogType.Log, new Regex(
                    "THREE_UNITY_LOGIC_SESSION_RESTART.*outboundPurged=1025"));
                Invoke(
                    bridge,
                    "ProcessInboundWithLease",
                    lease,
                    Hello("session-b", 0, "session-a"));

                Assert.That(router.ActiveSessionId, Is.EqualTo("session-b"));
                Assert.That(router.RetiredOutgoingDiscarded, Is.EqualTo(1));
                Assert.That(modules, Has.Count.EqualTo(2));
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.False);
                Assert.That(GetField<object>(bridge, "activeOutboundOwner"), Is.Not.SameAs(oldOwner));
                Assert.That(outbound.Snapshot().PendingReliable, Is.Zero);
                Assert.That(outbound.Snapshot().PendingLatest, Is.Zero);

                var ready = Message("bridge.ready", 0);
                modules[1].Enqueue(ready);
                Invoke(bridge, "FlushOutgoing");
                Assert.That(outbound.TryDequeue(out var firstAfterRestart), Is.True);
                Assert.That(firstAfterRestart, Is.EqualTo(ready));
                Assert.That(outbound.TryDequeue(out _), Is.False);

                var metrics = launcher.GetTransportMetrics().Outbound;
                Assert.That(metrics.OwnerPurgedReliable, Is.EqualTo(1024));
                Assert.That(metrics.OwnerPurgedLatest, Is.EqualTo(1));
                Assert.That(metrics.ReliableBackpressureRejected, Is.EqualTo(1));
                Assert.That(metrics.ReliableDropped, Is.Zero);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                DisposeConnection(connection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void BurstFlushStopsAtBudgetAndPreservesRemainingOrder()
        {
            const int queuedMessages = 4096;
            const int expectedPerFlush = 256;
            var gameObject = new GameObject("logic-bridge-flush-budget-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var bridge = gameObject.AddComponent<ThreeUnityLogicBridge>();
            object connection = null;
            try
            {
                var lifecycle = GetLifecycle(launcher);
                lifecycle.Start(0);
                Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var pipe), Is.True);
                Assert.That(lifecycle.TryMarkConnected(page, pipe), Is.True);
                connection = InstallConnection(launcher, page, pipe, out _);
                var outbound = GetOutbound(connection);

                var modules = new List<QueuedLogicModule>();
                InstallQueuedRouter(bridge, launcher, modules);
                for (var index = 0; index < queuedMessages; index++)
                    modules[0].Enqueue(Message("burst.command", index));

                Invoke(bridge, "FlushOutgoing");

                Assert.That(outbound.Snapshot().PendingReliable, Is.EqualTo(expectedPerFlush));
                Assert.That(modules[0].Pending, Is.EqualTo(queuedMessages - expectedPerFlush));
                Assert.That(GetField<long>(bridge, "outboundFlushBudgetStops"), Is.EqualTo(1));
                Assert.That(GetField<int>(bridge, "maxOutgoingMessagesPerFlush"), Is.EqualTo(expectedPerFlush));
                for (var index = 0; index < expectedPerFlush; index++)
                {
                    Assert.That(outbound.TryDequeue(out var message), Is.True);
                    Assert.That(message, Is.EqualTo(Message("burst.command", index)));
                }

                Invoke(bridge, "FlushOutgoing");

                Assert.That(outbound.Snapshot().PendingReliable, Is.EqualTo(expectedPerFlush));
                Assert.That(modules[0].Pending, Is.EqualTo(queuedMessages - (expectedPerFlush * 2)));
                Assert.That(GetField<long>(bridge, "outboundFlushBudgetStops"), Is.EqualTo(2));
                for (var index = expectedPerFlush; index < expectedPerFlush * 2; index++)
                {
                    Assert.That(outbound.TryDequeue(out var message), Is.True);
                    Assert.That(message, Is.EqualTo(Message("burst.command", index)));
                }
                Assert.That(outbound.TryDequeue(out _), Is.False);

                var benchmark = "THREE_UNITY_OUTBOUND_FLUSH_BENCHMARK"
                    + " queued=" + queuedMessages
                    + " firstFlush=" + expectedPerFlush
                    + " remainingAfterFirst=" + (queuedMessages - expectedPerFlush)
                    + " budgetStops=" + GetField<long>(bridge, "outboundFlushBudgetStops");
                LogAssert.Expect(LogType.Log, benchmark);
                Debug.Log(benchmark);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                DisposeConnection(connection);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static ThreeUnityLogicSessionRouter InstallQueuedRouter(
            ThreeUnityLogicBridge bridge,
            ThreeUnityWebBridgeLauncher launcher,
            ICollection<QueuedLogicModule> modules)
        {
            bridge.Configure(launcher, "shop-flight-v1");
            Invoke(bridge, "Awake");
            GetField<ThreeUnityLogicSessionRouter>(bridge, "router")?.Dispose();
            var router = new ThreeUnityLogicSessionRouter(
                "queued-test-v1",
                _ =>
                {
                    var module = new QueuedLogicModule();
                    modules.Add(module);
                    return module;
                },
                launcher.PageGeneration);
            SetField(bridge, "router", router);
            bridge.enabled = true;
            return router;
        }

        private static string Message(string type, int value)
        {
            return LogicEnvelopeWriter.Encode(type, value, new TestPayload { value = value });
        }

        private static string Hello(
            string sessionId,
            long sequence,
            string previousSessionId = null)
        {
            var previous = previousSessionId == null
                ? string.Empty
                : ",\"previousSessionId\":\"" + previousSessionId + "\"";
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"bridge.hello\",\"seq\":" + sequence
                + ",\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"queued-test-v1\"]"
                + previous + "}}";
        }

        private static ThreeUnityWebBridgeLifecycle GetLifecycle(ThreeUnityWebBridgeLauncher launcher)
        {
            return GetField<ThreeUnityWebBridgeLifecycle>(launcher, "lifecycle");
        }

        private static object InstallConnection(
            ThreeUnityWebBridgeLauncher launcher,
            long pageGeneration,
            long connectionGeneration,
            out ThreeUnityWebBridgeLease lease)
        {
            var launcherType = typeof(ThreeUnityWebBridgeLauncher);
            var connectionType = launcherType.GetNestedType("ConnectionResources", BindingFlags.NonPublic);
            Assert.That(connectionType, Is.Not.Null);
            var connection = Activator.CreateInstance(
                connectionType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[]
                {
                    pageGeneration,
                    connectionGeneration,
                    "three-unity-logic-retry-test-" + Guid.NewGuid().ToString("N"),
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

        private static ThreeUnityOutboundBuffer GetOutbound(object connection)
        {
            return (ThreeUnityOutboundBuffer)connection.GetType().GetProperty("Outbound").GetValue(connection);
        }

        private static void DisposeConnection(object connection)
        {
            if (connection == null)
                return;
            connection.GetType().GetMethod("Dispose").Invoke(connection, new object[] { 0 });
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(target, arguments);
        }

        private static T GetField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(target);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        [Serializable]
        private sealed class TestPayload
        {
            public int value;
        }

        private sealed class QueuedLogicModule : IThreeUnityLogicModule
        {
            private readonly Queue<string> outgoing = new Queue<string>();

            public string Profile => "queued-test-v1";
            public string SessionId { get; private set; }
            public bool IsAuthoritative => true;
            public bool IsFallback { get; private set; }
            public int Pending => outgoing.Count;

            public void Enqueue(string json) => outgoing.Enqueue(json);
            public void BindSession(string sessionId) => SessionId = sessionId;
            public void Handle(string json, LogicEnvelopeHeader header) { }
            public void FixedTick(float deltaTime) { }

            public bool TryDequeueOutgoing(out string json)
            {
                if (outgoing.Count == 0)
                {
                    json = null;
                    return false;
                }
                json = outgoing.Dequeue();
                return true;
            }

            public void ForceFallback(string reason) => IsFallback = true;

            public void Dispose()
            {
                IsFallback = true;
                outgoing.Clear();
            }
        }
    }
}
