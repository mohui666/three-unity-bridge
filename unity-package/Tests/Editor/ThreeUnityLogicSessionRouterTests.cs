using System;
using System.Collections.Generic;
using NUnit.Framework;
using ThreeUnity.Bridge.Logic;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityLogicSessionRouterTests
    {
        [Test]
        public void OptionalSessionIsWrittenAndLegacyEncodingRemainsUnscoped()
        {
            var legacy = LogicEnvelopeWriter.Encode("bridge.ready", 0, new TestPayload { value = 1 });
            var scoped = LogicEnvelopeWriter.Encode("bridge.ready", 1, "session-a", new TestPayload { value = 2 });

            AssertHeader(legacy, "bridge.ready", 0, null);
            AssertHeader(scoped, "bridge.ready", 1, "session-a");
            StringAssert.DoesNotContain("sessionId", legacy);
        }

        [Test]
        public void ExplicitSessionIdsRejectWhitespaceAndOversizedValues()
        {
            const string whitespace = "{\"protocol\":1,\"sessionId\":\"  \u0020\",\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{}}";
            const string padded = "{\"protocol\":1,\"sessionId\":\" session-a\",\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{}}";
            const string nestedLegacy = "{\"protocol\":1,\"type\":\"legacy.message\",\"seq\":0,\"payload\":{\"sessionId\":\"payload-value\"}}";
            var oversizedId = new string('x', LogicEnvelopeParser.MaxSessionIdLength + 1);
            var oversized = "{\"protocol\":1,\"sessionId\":\"" + oversizedId
                + "\",\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{}}";

            Assert.That(LogicEnvelopeParser.TryParseHeader(whitespace, out _, out _), Is.False);
            Assert.That(LogicEnvelopeParser.TryParseHeader(padded, out _, out _), Is.False);
            Assert.That(LogicEnvelopeParser.TryParseHeader(oversized, out _, out _), Is.False);
            Assert.That(LogicEnvelopeParser.TryParseHeader(nestedLegacy, out var legacyHeader, out _), Is.True);
            Assert.That(legacyHeader.sessionId, Is.Null);
            Assert.Throws<ArgumentException>(() =>
                LogicEnvelopeWriter.Encode("bridge.hello", 0, " ", new TestPayload()));
            Assert.Throws<ArgumentException>(() =>
                LogicEnvelopeWriter.Encode("bridge.hello", 0, "session-a ", new TestPayload()));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                LogicEnvelopeWriter.Encode("bridge.hello", 0, oversizedId, new TestPayload()));
        }

        [Test]
        public void NewHelloWithMatchingPreviousSessionReplacesAFallbackModule()
        {
            using (var router = new ThreeUnityLogicSessionRouter("shop-flight-v1"))
            {
                Assert.That(Route(router, Hello("session-a", 10)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(router.ActiveSessionId, Is.EqualTo("session-a"));
                Assert.That(router.TryDequeueOutgoing(out var firstReady), Is.True);
                AssertHeader(firstReady, "bridge.ready", 0, "session-a");
                StringAssert.Contains(ThreeUnityLogicFeatures.SessionRestart, firstReady);

                Assert.That(Route(router, Bootstrap("session-a", 11, 7)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                router.FixedTick(0.02f);
                Assert.That(router.TryDequeueOutgoing(out var firstState), Is.True);
                AssertHeader(firstState, "flight.state", 1, "session-a");

                Assert.That(Route(router, Fallback("session-a", 12)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(router.TryDequeueOutgoing(out var fallback), Is.True);
                AssertHeader(fallback, "bridge.fallback", 2, "session-a");
                var retired = router.CurrentModule;

                Assert.That(
                    Route(router, Hello("session-b", 0, "session-a")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Restarted));
                Assert.That(router.CurrentModule, Is.Not.SameAs(retired));
                Assert.That(retired.IsFallback, Is.True);
                Assert.That(router.ActiveSessionId, Is.EqualTo("session-b"));
                Assert.That(router.SessionRestarts, Is.EqualTo(1));
                Assert.That(router.TryDequeueOutgoing(out var restartedReady), Is.True);
                AssertHeader(restartedReady, "bridge.ready", 0, "session-b");

                Assert.That(Route(router, Bootstrap("session-a", 100, 99)), Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));
                Assert.That(router.SessionRejected, Is.EqualTo(1));
                Assert.That(Route(router, Bootstrap("session-b", 1, 8)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                router.FixedTick(0.02f);
                Assert.That(router.TryDequeueOutgoing(out var restartedState), Is.True);
                AssertHeader(restartedState, "flight.state", 1, "session-b");
                StringAssert.Contains("\"generation\":8", restartedState);
            }
        }

        [Test]
        public void RestartEnvelopeCreatesAFreshModuleThatWaitsForItsHello()
        {
            using (var router = new ThreeUnityLogicSessionRouter("shop-flight-v1"))
            {
                Route(router, Hello("session-a", 0));
                Assert.That(router.TryDequeueOutgoing(out _), Is.True);
                var retired = router.CurrentModule;

                Assert.That(
                    Route(router, Restart("session-b", 0, "wrong-session")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));
                Assert.That(router.CurrentModule, Is.SameAs(retired));

                Assert.That(
                    Route(router, Restart("session-b", 0, "session-a")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Restarted));
                Assert.That(router.CurrentModule, Is.Not.SameAs(retired));
                Assert.That(router.IsAwaitingHello, Is.True);
                Assert.That(Route(router, Bootstrap("session-b", 1, 3)), Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));

                Assert.That(Route(router, Hello("session-b", 2)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(router.IsAwaitingHello, Is.False);
                Assert.That(router.TryDequeueOutgoing(out var ready), Is.True);
                AssertHeader(ready, "bridge.ready", 0, "session-b");
                Assert.That(router.SessionRestarts, Is.EqualTo(1));
                Assert.That(router.SessionRejected, Is.EqualTo(2));
            }
        }

        [Test]
        public void RestartDiscardsRetiredOutputButPreservesLifetimeTelemetry()
        {
            using (var router = new ThreeUnityLogicSessionRouter("shop-flight-v1"))
            {
                Route(router, Hello("session-a", 0));
                Route(router, Bootstrap("session-a", 1, 1));
                router.FixedTick(0.02f);
                Assert.That(router.GetStateEmissionMetrics().Emitted, Is.EqualTo(1));

                Assert.That(
                    Route(router, Hello("session-b", 0, "session-a")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Restarted));

                Assert.That(router.RetiredOutgoingDiscarded, Is.EqualTo(2), "Old ready/state must not escape after the swap.");
                Assert.That(router.GetStateEmissionMetrics().Emitted, Is.EqualTo(1), "Lifetime counters must stay monotonic.");
                Assert.That(router.TryDequeueOutgoing(out var ready), Is.True);
                AssertHeader(ready, "bridge.ready", 0, "session-b");
                Assert.That(router.TryDequeueOutgoing(out _), Is.False);

                Route(router, Bootstrap("session-b", 1, 2));
                router.FixedTick(0.02f);
                Assert.That(router.GetStateEmissionMetrics().Emitted, Is.EqualTo(2));
            }
        }

        [Test]
        public void TransportResetPurgesOldOutputPreservesTelemetryAndAcceptsAnOrdinaryFreshHello()
        {
            using (var router = new ThreeUnityLogicSessionRouter(
                "shop-flight-v1",
                initialHostGeneration: 3))
            {
                Assert.That(
                    Route(router, 3, Hello("old-session", 10)),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(
                    Route(router, 3, Bootstrap("old-session", 11, 4)),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                router.FixedTick(0.02f);
                Assert.That(router.GetStateEmissionMetrics().Emitted, Is.EqualTo(1));

                var retired = router.CurrentModule;
                Assert.That(router.ResetForHostGeneration(4), Is.True);

                Assert.That(router.HostGeneration, Is.EqualTo(4));
                Assert.That(router.TransportResets, Is.EqualTo(1));
                Assert.That(router.CurrentModule, Is.Not.SameAs(retired));
                Assert.That(router.ActiveSessionId, Is.Null);
                Assert.That(router.HasScopedSession, Is.False);
                Assert.That(router.RetiredOutgoingDiscarded, Is.EqualTo(2));
                Assert.That(router.GetStateEmissionMetrics().Emitted, Is.EqualTo(1));
                Assert.That(router.TryDequeueOutgoing(out _), Is.False);

                Assert.That(
                    Route(router, 4, Hello("fresh-session", 0)),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(router.ActiveSessionId, Is.EqualTo("fresh-session"));
                Assert.That(router.TryDequeueOutgoing(out var ready), Is.True);
                AssertHeader(ready, "bridge.ready", 0, "fresh-session");
            }
        }

        [Test]
        public void OldGenerationCannotBindOrTailAfterTransportReset()
        {
            var modules = new List<RecordingModule>();
            using (var router = new ThreeUnityLogicSessionRouter(
                "recording-v1",
                _ =>
                {
                    var created = new RecordingModule();
                    modules.Add(created);
                    return created;
                },
                initialHostGeneration: 8))
            {
                Assert.That(
                    Route(router, 8, Hello("old-session", 0, null, "recording-v1")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(router.ResetForHostGeneration(9), Is.True);

                Assert.That(
                    Route(router, 8, Input("old-session", 100)),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));
                Assert.That(
                    Route(router, 8, Hello("old-session", 101, null, "recording-v1")),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));

                Assert.That(modules.Count, Is.EqualTo(2));
                Assert.That(modules[1].Handled, Is.EqualTo(0));
                Assert.That(router.ActiveSessionId, Is.Null);
                Assert.That(router.GenerationRejected, Is.EqualTo(2));
                Assert.That(router.SessionRejected, Is.EqualTo(0));
            }
        }

        [Test]
        public void TransportResetIsIdempotentAndNeverMovesBackward()
        {
            var modules = new List<RecordingModule>();
            using (var router = new ThreeUnityLogicSessionRouter(
                "recording-v1",
                _ =>
                {
                    var created = new RecordingModule();
                    modules.Add(created);
                    return created;
                },
                initialHostGeneration: 12))
            {
                Assert.That(router.ResetForHostGeneration(13), Is.True);
                var current = router.CurrentModule;

                Assert.That(router.ResetForHostGeneration(13), Is.False);
                Assert.That(router.ResetForHostGeneration(12), Is.False);
                Assert.That(router.CurrentModule, Is.SameAs(current));
                Assert.That(router.HostGeneration, Is.EqualTo(13));
                Assert.That(router.TransportResets, Is.EqualTo(1));
                Assert.That(modules.Count, Is.EqualTo(2));
                Assert.That(modules[0].DisposeCount, Is.EqualTo(1));
                Assert.That(modules[1].DisposeCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void TransportResetClearsThePreviousGenerationFallbackTerminal()
        {
            using (var router = new ThreeUnityLogicSessionRouter(
                "shop-flight-v1",
                initialHostGeneration: 20))
            {
                Route(router, 20, Hello("failed-session", 0));
                Assert.That(router.TryDequeueOutgoing(out _), Is.True);
                Route(router, 20, Fallback("failed-session", 1));
                Assert.That(router.CurrentModule.IsFallback, Is.True);

                Assert.That(router.ResetForHostGeneration(21), Is.True);
                Assert.That(router.CurrentModule.IsFallback, Is.False);
                Assert.That(router.ActiveSessionId, Is.Null);
                Assert.That(router.TryDequeueOutgoing(out _), Is.False);
                Assert.That(
                    Route(router, 21, Hello("fresh-session", 0)),
                    Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
            }
        }

        [Test]
        public void DisposeIsIdempotentAcrossTransportGenerations()
        {
            var modules = new List<RecordingModule>();
            var router = new ThreeUnityLogicSessionRouter(
                "recording-v1",
                _ =>
                {
                    var created = new RecordingModule();
                    modules.Add(created);
                    return created;
                });

            Assert.That(router.ResetForHostGeneration(1), Is.True);
            router.Dispose();
            router.Dispose();

            Assert.That(modules.Count, Is.EqualTo(2));
            Assert.That(modules[0].DisposeCount, Is.EqualTo(1));
            Assert.That(modules[1].DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void ForeignAndPerTypeStaleMessagesAreRejectedBeforeTheModule()
        {
            var modules = new List<RecordingModule>();
            using (var router = new ThreeUnityLogicSessionRouter(
                "recording-v1",
                _ =>
                {
                    var created = new RecordingModule();
                    modules.Add(created);
                    return created;
                }))
            {
                Assert.That(Route(router, Hello("session-a", 5, null, "recording-v1")), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(Route(router, Input("session-a", 10)), Is.EqualTo(ThreeUnityLogicRouteResult.Handled));
                Assert.That(Route(router, Input("session-a", 10)), Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));
                Assert.That(Route(router, Input("session-a", 9)), Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));
                Assert.That(Route(router, Input("foreign", 100)), Is.EqualTo(ThreeUnityLogicRouteResult.Rejected));

                Assert.That(modules.Count, Is.EqualTo(1));
                Assert.That(modules[0].Handled, Is.EqualTo(2));
                Assert.That(router.SequenceRejected, Is.EqualTo(2));
                Assert.That(router.SessionRejected, Is.EqualTo(3));
            }
            Assert.That(modules[0].Disposed, Is.True);
        }

        [Test]
        public void ModuleFallbackDropsQueuedReadyAndStateBeforeItsTerminalMessage()
        {
            var module = new ShopFlightLogicModule();
            HandleDirect(module, Hello("session-a", 0));
            HandleDirect(module, Bootstrap("session-a", 1, 4));
            module.FixedTick(0.02f);

            module.ForceFallback("test-fallback");

            Assert.That(module.TryDequeueOutgoing(out var onlyMessage), Is.True);
            AssertHeader(onlyMessage, "bridge.fallback", 2, "session-a");
            Assert.That(module.TryDequeueOutgoing(out _), Is.False);
            module.Dispose();
        }

        [Test]
        public void VoxelReadyAndCollisionResyncEchoTheBoundSession()
        {
            var module = new VoxelPlayerLogicModule();
            HandleDirect(module, Hello("voxel-session", 0, null, "voxel-player-v1"));
            Assert.That(module.TryDequeueOutgoing(out var ready), Is.True);
            AssertHeader(ready, "bridge.ready", 0, "voxel-session");
            StringAssert.Contains(VoxelPlayerLogicModule.CollisionDeltaFeature, ready);
            StringAssert.Contains(ThreeUnityLogicFeatures.SessionRestart, ready);

            HandleDirect(module, "{\"protocol\":1,\"sessionId\":\"voxel-session\",\"type\":\"world.collision\",\"seq\":1,\"payload\":{\"revision\":5,\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":1,\"y\":1,\"z\":1},\"solidBits\":\"AA==\",\"fluidBits\":\"AA==\"}}");
            HandleDirect(module, "{\"protocol\":1,\"sessionId\":\"voxel-session\",\"type\":\"world.collision.delta\",\"seq\":2,\"payload\":{\"baseRevision\":4,\"revision\":6,\"origin\":{\"x\":0,\"y\":0,\"z\":0},\"size\":{\"x\":1,\"y\":1,\"z\":1},\"changeCount\":0,\"changes\":\"\"}}");

            Assert.That(module.TryDequeueOutgoing(out var resync), Is.True);
            AssertHeader(resync, "world.collision.resync", 1, "voxel-session");
            module.Dispose();
        }

        private static ThreeUnityLogicRouteResult Route(ThreeUnityLogicSessionRouter router, string json)
        {
            Assert.That(LogicEnvelopeParser.TryParseHeader(json, out var header, out var error), Is.True, error);
            return router.Handle(json, header);
        }

        private static ThreeUnityLogicRouteResult Route(
            ThreeUnityLogicSessionRouter router,
            long hostGeneration,
            string json)
        {
            Assert.That(LogicEnvelopeParser.TryParseHeader(json, out var header, out var error), Is.True, error);
            return router.Handle(hostGeneration, json, header);
        }

        private static void HandleDirect(IThreeUnityLogicModule module, string json)
        {
            Assert.That(LogicEnvelopeParser.TryParseHeader(json, out var header, out var error), Is.True, error);
            module.Handle(json, header);
        }

        private static void AssertHeader(string json, string type, long sequence, string sessionId)
        {
            Assert.That(LogicEnvelopeParser.TryParseHeader(json, out var header, out var error), Is.True, error);
            Assert.That(header.type, Is.EqualTo(type));
            Assert.That(header.seq, Is.EqualTo(sequence));
            Assert.That(header.sessionId, Is.EqualTo(sessionId));
        }

        private static string Hello(
            string sessionId,
            long sequence,
            string previousSessionId = null,
            string profile = "shop-flight-v1")
        {
            var previous = previousSessionId == null
                ? string.Empty
                : ",\"previousSessionId\":\"" + previousSessionId + "\"";
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"bridge.hello\",\"seq\":" + sequence
                + ",\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"" + profile + "\"]"
                + previous + "}}";
        }

        private static string Restart(string sessionId, long sequence, string previousSessionId)
        {
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"bridge.restart\",\"seq\":" + sequence
                + ",\"payload\":{\"previousSessionId\":\"" + previousSessionId + "\"}}";
        }

        private static string Bootstrap(string sessionId, long sequence, int generation)
        {
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"flight.bootstrap\",\"seq\":" + sequence
                + ",\"payload\":{\"generation\":" + generation
                + ",\"time\":0,\"amplitude\":0,\"flying\":false}}";
        }

        private static string Input(string sessionId, long sequence)
        {
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"player.input\",\"seq\":" + sequence
                + ",\"payload\":{\"moveX\":0}}";
        }

        private static string Fallback(string sessionId, long sequence)
        {
            return "{\"protocol\":1,\"sessionId\":\"" + sessionId
                + "\",\"type\":\"bridge.fallback\",\"seq\":" + sequence
                + ",\"payload\":{\"reason\":\"web-request\"}}";
        }

        [Serializable]
        private sealed class TestPayload
        {
            public int value;
        }

        private sealed class RecordingModule : IThreeUnityLogicModule
        {
            private string sessionId;

            public string Profile => "recording-v1";
            public string SessionId => sessionId;
            public bool IsAuthoritative { get; private set; }
            public bool IsFallback { get; private set; }
            public int Handled { get; private set; }
            public int DisposeCount { get; private set; }
            public bool Disposed => DisposeCount > 0;

            public void BindSession(string value)
            {
                if (sessionId != null && !string.Equals(sessionId, value, StringComparison.Ordinal))
                    throw new InvalidOperationException();
                sessionId = value;
            }

            public void Handle(string json, LogicEnvelopeHeader header)
            {
                Handled++;
            }

            public void FixedTick(float deltaTime)
            {
                IsAuthoritative = true;
            }

            public bool TryDequeueOutgoing(out string json)
            {
                json = null;
                return false;
            }

            public void ForceFallback(string reason)
            {
                IsFallback = true;
                IsAuthoritative = false;
            }

            public void Dispose()
            {
                DisposeCount++;
                IsFallback = true;
                IsAuthoritative = false;
            }
        }
    }
}
