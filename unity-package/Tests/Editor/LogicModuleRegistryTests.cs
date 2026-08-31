using System;
using NUnit.Framework;
using ThreeUnity.Bridge.Logic;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class LogicModuleRegistryTests
    {
        [Test]
        public void EmptyProfileCreatesNoAuthorityModule()
        {
            Assert.That(ThreeUnityLogicModuleRegistry.Create(""), Is.Null);
            Assert.That(ThreeUnityLogicModuleRegistry.Create(null), Is.Null);
        }

        [Test]
        public void VoxelProfileCreatesTheReusableVoxelModule()
        {
            var module = ThreeUnityLogicModuleRegistry.Create("voxel-player-v1");

            Assert.That(module, Is.TypeOf<VoxelPlayerLogicModule>());
            Assert.That(module.Profile, Is.EqualTo("voxel-player-v1"));
        }

        [Test]
        public void ShopFlightProfileCreatesTheReusableFlightModule()
        {
            var module = ThreeUnityLogicModuleRegistry.Create("shop-flight-v1");

            Assert.That(module, Is.TypeOf<ShopFlightLogicModule>());
            Assert.That(module.Profile, Is.EqualTo("shop-flight-v1"));
        }

        [Test]
        public void GameNamesAreRejectedAsProfiles()
        {
            Assert.Throws<ArgumentException>(() => ThreeUnityLogicModuleRegistry.Create("LittleCubes"));
        }

        [Test]
        public void VoxelModuleWaitsForAllBootstrapDataThenEmitsState()
        {
            var module = new VoxelPlayerLogicModule();
            Handle(module, "{\"protocol\":1,\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"voxel-player-v1\"]}}");
            Assert.That(module.TryDequeueOutgoing(out var ready), Is.True);
            StringAssert.Contains("\"type\":\"bridge.ready\"", ready);
            StringAssert.Contains("collision-delta-v2", ready);

            module.FixedTick(0.02f);
            Assert.That(module.IsAuthoritative, Is.False);

            Handle(module, "{\"protocol\":1,\"type\":\"player.bootstrap\",\"seq\":1,\"payload\":{\"position\":{\"x\":0.5,\"y\":10.0,\"z\":0.5},\"velocity\":{\"x\":0,\"y\":0,\"z\":0},\"yaw\":0,\"pitch\":0,\"speed\":4,\"sprintSpeed\":6.5,\"flySpeed\":8,\"gravity\":-15,\"jumpStrength\":7,\"waterJumpStrength\":4.5,\"width\":0.5,\"height\":1.8,\"eyeHeight\":1.6,\"collisionTolerance\":0.05,\"flying\":false}}");
            Handle(module, "{\"protocol\":1,\"type\":\"world.collision\",\"seq\":2,\"payload\":{\"revision\":1,\"origin\":{\"x\":0,\"y\":9,\"z\":0},\"size\":{\"x\":1,\"y\":2,\"z\":1},\"solidBits\":\"AA==\",\"fluidBits\":\"AA==\"}}");
            Handle(module, "{\"protocol\":1,\"type\":\"player.input\",\"seq\":3,\"payload\":{\"moveX\":0,\"moveZ\":1,\"yaw\":0,\"pitch\":0,\"jumpHeld\":false,\"sprintHeld\":false,\"flyToggle\":true}}");

            module.FixedTick(0.02f);

            Assert.That(module.IsAuthoritative, Is.True);
            Assert.That(module.TryDequeueOutgoing(out var state), Is.True);
            StringAssert.Contains("\"type\":\"player.state\"", state);
            StringAssert.Contains("\"ackInputSeq\":3", state);
            StringAssert.Contains("\"flying\":true", state);

            module.FixedTick(0.02f);
            Assert.That(module.TryDequeueOutgoing(out _), Is.False, "The 30 Hz state budget should coalesce this 50 Hz tick.");
            module.FixedTick(0.02f);
            Assert.That(module.TryDequeueOutgoing(out var secondState), Is.True);
            StringAssert.Contains("\"flying\":true", secondState, "A retained input must not replay a fly-toggle edge.");
        }

        [Test]
        public void VoxelModuleNeutralizesExpiredInputAndReportsRecovery()
        {
            var module = new VoxelPlayerLogicModule();
            Handle(module, "{\"protocol\":1,\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"voxel-player-v1\"]}}");
            Assert.That(module.TryDequeueOutgoing(out _), Is.True);
            Handle(module, "{\"protocol\":1,\"type\":\"player.bootstrap\",\"seq\":1,\"payload\":{\"position\":{\"x\":0.5,\"y\":10.0,\"z\":0.5},\"velocity\":{\"x\":0,\"y\":0,\"z\":0},\"yaw\":0,\"pitch\":0,\"speed\":4,\"sprintSpeed\":6.5,\"flySpeed\":8,\"gravity\":-15,\"jumpStrength\":7,\"waterJumpStrength\":4.5,\"width\":0.5,\"height\":1.8,\"eyeHeight\":1.6,\"collisionTolerance\":0.05,\"flying\":true}}");
            Handle(module, "{\"protocol\":1,\"type\":\"world.collision\",\"seq\":2,\"payload\":{\"revision\":1,\"origin\":{\"x\":0,\"y\":9,\"z\":0},\"size\":{\"x\":1,\"y\":2,\"z\":1},\"solidBits\":\"AA==\",\"fluidBits\":\"AA==\"}}");
            Handle(module, "{\"protocol\":1,\"type\":\"player.input\",\"seq\":3,\"payload\":{\"moveX\":0,\"moveZ\":1,\"yaw\":0,\"pitch\":0,\"jumpHeld\":true,\"sprintHeld\":true,\"flyToggle\":false}}");

            for (var tick = 0; tick < 26; tick++)
                module.FixedTick(0.02f);
            var expired = module.GetInputFreshnessMetrics();
            Assert.That(expired.Fresh, Is.False);
            Assert.That(expired.Expirations, Is.EqualTo(1));
            Assert.That(expired.NeutralizedTicks, Is.GreaterThan(0));

            Handle(module, "{\"protocol\":1,\"type\":\"player.input\",\"seq\":4,\"payload\":{\"moveX\":0,\"moveZ\":0,\"yaw\":0,\"pitch\":0,\"jumpHeld\":false,\"sprintHeld\":false,\"flyToggle\":false}}");
            var recovered = module.GetInputFreshnessMetrics();
            Assert.That(recovered.Fresh, Is.True);
            Assert.That(recovered.Recoveries, Is.EqualTo(1));
        }

        [Test]
        public void ShopFlightModuleBootstrapsAndAcknowledgesCommands()
        {
            var module = new ShopFlightLogicModule();
            Handle(module, "{\"protocol\":1,\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"shop-flight-v1\"]}}");
            Assert.That(module.TryDequeueOutgoing(out var ready), Is.True);
            StringAssert.Contains("\"type\":\"bridge.ready\"", ready);

            Handle(module, "{\"protocol\":1,\"type\":\"flight.bootstrap\",\"seq\":1,\"payload\":{\"generation\":4,\"time\":0,\"amplitude\":0,\"flying\":false}}");
            Handle(module, "{\"protocol\":1,\"type\":\"flight.command\",\"seq\":2,\"payload\":{\"generation\":4,\"flying\":true}}");
            module.FixedTick(0.02f);

            Assert.That(module.IsAuthoritative, Is.True);
            Assert.That(module.TryDequeueOutgoing(out var state), Is.True);
            StringAssert.Contains("\"type\":\"flight.state\"", state);
            StringAssert.Contains("\"generation\":4", state);
            StringAssert.Contains("\"ackCommandSeq\":2", state);
            StringAssert.Contains("\"flying\":true", state);
        }

        [Test]
        public void ShopFlightModuleSuppressesIdleStatesButKeepsAHeartbeat()
        {
            var module = new ShopFlightLogicModule();
            Handle(module, "{\"protocol\":1,\"type\":\"bridge.hello\",\"seq\":0,\"payload\":{\"gameId\":\"test-game\",\"capabilities\":[\"shop-flight-v1\"]}}");
            Assert.That(module.TryDequeueOutgoing(out _), Is.True);
            Handle(module, "{\"protocol\":1,\"type\":\"flight.bootstrap\",\"seq\":1,\"payload\":{\"generation\":1,\"time\":0,\"amplitude\":0,\"flying\":false}}");

            module.FixedTick(0.02f);
            Assert.That(module.TryDequeueOutgoing(out _), Is.True, "The first authoritative state must be immediate.");
            for (var tick = 0; tick < 9; tick++)
            {
                module.FixedTick(0.02f);
                Assert.That(module.TryDequeueOutgoing(out _), Is.False, "Unchanged fixed ticks should not cross the bridge.");
            }

            module.FixedTick(0.02f);
            Assert.That(module.TryDequeueOutgoing(out var heartbeat), Is.True);
            StringAssert.Contains("\"type\":\"flight.state\"", heartbeat);
            var telemetry = module.GetStateEmissionMetrics();
            Assert.That(telemetry.Suppressed, Is.EqualTo(9));
            Assert.That(telemetry.Heartbeats, Is.EqualTo(1));
        }

        private static void Handle(IThreeUnityLogicModule module, string json)
        {
            Assert.That(LogicEnvelopeParser.TryParseHeader(json, out var header, out var error), Is.True, error);
            module.Handle(json, header);
        }
    }
}
