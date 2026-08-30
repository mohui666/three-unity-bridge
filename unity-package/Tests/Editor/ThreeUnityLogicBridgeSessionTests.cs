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
            var method = typeof(ThreeUnityLogicBridge).GetMethod(
                "LatestStreamKey",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var first = (string)method.Invoke(null, new object[]
            {
                new LogicEnvelopeHeader { sessionId = "session-a", type = "player.state" },
            });
            var second = (string)method.Invoke(null, new object[]
            {
                new LogicEnvelopeHeader { sessionId = "session-b", type = "player.state" },
            });
            var legacy = (string)method.Invoke(null, new object[]
            {
                new LogicEnvelopeHeader { type = "player.state" },
            });

            Assert.That(first, Is.EqualTo("session-a:player.state"));
            Assert.That(second, Is.EqualTo("session-b:player.state"));
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(legacy, Is.EqualTo("player.state"));
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

                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_OVERFLOW"));
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

                LogAssert.Expect(LogType.Warning, new Regex("THREE_UNITY_WEB_BRIDGE_RELIABLE_OVERFLOW"));
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

                LogAssert.Expect(LogType.Log, "THREE_UNITY_LOGIC_TRANSPORT_RESET page=" + newPage);
                Assert.That(bridge.ResetForHostGeneration(newPage), Is.True);
                Assert.That(GetField<bool>(bridge, "hasPendingOutgoing"), Is.False);
                Assert.That(GetField<string>(bridge, "pendingOutgoingJson"), Is.Null);
                Assert.That(router.RetiredOutgoingDiscarded, Is.EqualTo(1));
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
                launcher,
                connection,
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
