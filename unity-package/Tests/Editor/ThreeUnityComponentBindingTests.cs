using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityComponentBindingTests
    {
        private const string DoorSamplePath = "Packages/com.three-unity.bridge/Samples~/Component Binding Door/component-binding-door.threeunity";
        private const string DoorImportedAssetPath = "Assets/ComponentBindingDoorImporterSmoke.threeunity";
        private const string StaticSamplePath = "Packages/com.three-unity.bridge/Samples~/Imported Triangle/triangle.threeunity";
        private const string StaticImportedAssetPath = "Assets/ComponentBindingStaticImporterSmoke.threeunity";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(DoorImportedAssetPath);
            AssetDatabase.DeleteAsset(StaticImportedAssetPath);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(DoorImportedAssetPath);
            AssetDatabase.DeleteAsset(StaticImportedAssetPath);
        }

        [Test]
        public void RegisteredDescriptorCreatesConfiguresAndReusesComponent()
        {
            var bindingType = $"TestDoor-{Guid.NewGuid():N}";
            ThreeUnityComponentBindings.Register<TestDoorData, TestDoor>(bindingType, (door, data) =>
                door.Configure(data.openAngle, data.duration, data.startsOpen));
            Assert.Throws<InvalidOperationException>(() =>
                ThreeUnityComponentBindings.Register<TestDoorData, TestDoor>(bindingType, (door, data) => { }));

            var root = new GameObject("Binding Test Root");
            var target = new GameObject("Door Pivot");
            target.transform.SetParent(root.transform, false);
            target.AddComponent<ThreeUnityMetadata>().Initialize(
                "door-node",
                1,
                "{}",
                new[]
                {
                    new ThreeUnityComponentDescriptor
                    {
                        type = bindingType,
                        dataJson = "{\"openAngle\":95,\"duration\":0.45,\"startsOpen\":true}",
                    },
                    new ThreeUnityComponentDescriptor
                    {
                        type = "Unmapped-Test-Component",
                        dataJson = "{}",
                    },
                });
            var applicator = root.AddComponent<ThreeUnityComponentApplicator>();

            try
            {
                var first = applicator.Apply();
                Assert.That(first.Applied, Is.EqualTo(1));
                Assert.That(first.Unmapped, Is.EqualTo(1));
                Assert.That(first.Failed, Is.Zero);

                var door = target.GetComponent<TestDoor>();
                Assert.That(door, Is.Not.Null);
                Assert.That(door.OpenAngle, Is.EqualTo(95f));
                Assert.That(door.Duration, Is.EqualTo(0.45f));
                Assert.That(door.StartsOpen, Is.True);

                var second = applicator.Apply();
                Assert.That(second.Applied, Is.EqualTo(1));
                Assert.That(second.Unmapped, Is.EqualTo(1));
                Assert.That(second.Failed, Is.Zero);
                Assert.That(target.GetComponents<TestDoor>(), Has.Length.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ImporterAddsApplicatorOnlyWhenDescriptorsExist()
        {
            File.Copy(Path.GetFullPath(DoorSamplePath), Path.GetFullPath(DoorImportedAssetPath));
            AssetDatabase.ImportAsset(DoorImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var doorRoot = AssetDatabase.LoadAssetAtPath<GameObject>(DoorImportedAssetPath);
            Assert.That(doorRoot, Is.Not.Null);
            Assert.That(doorRoot.GetComponent<ThreeUnityComponentApplicator>(), Is.Not.Null);
            var doorMetadata = doorRoot.GetComponentsInChildren<ThreeUnityMetadata>(true)
                .Single(metadata => metadata.Components.Any(descriptor => descriptor.type == "Door"));
            Assert.That(doorMetadata.SourceNodeId, Is.Not.Empty);
            Assert.That(doorMetadata.Components.Single().dataJson, Does.Contain("\"openAngle\":95"));

            File.Copy(Path.GetFullPath(StaticSamplePath), Path.GetFullPath(StaticImportedAssetPath));
            AssetDatabase.ImportAsset(StaticImportedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var staticRoot = AssetDatabase.LoadAssetAtPath<GameObject>(StaticImportedAssetPath);
            Assert.That(staticRoot, Is.Not.Null);
            Assert.That(staticRoot.GetComponent<ThreeUnityComponentApplicator>(), Is.Null);
        }

        [Serializable]
        private sealed class TestDoorData
        {
            public float openAngle;
            public float duration;
            public bool startsOpen;
        }

        private sealed class TestDoor : MonoBehaviour
        {
            public float OpenAngle { get; private set; }
            public float Duration { get; private set; }
            public bool StartsOpen { get; private set; }

            public void Configure(float openAngle, float duration, bool startsOpen)
            {
                OpenAngle = openAngle;
                Duration = duration;
                StartsOpen = startsOpen;
            }
        }
    }
}
