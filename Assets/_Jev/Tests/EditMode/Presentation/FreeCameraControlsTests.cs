using System.Reflection;
using Jev.Gameplay.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class FreeCameraControlsTests
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        GameObject root;
        RtsCameraController rig;
        UnityEngine.Camera camera;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Free camera test");
            camera = new GameObject("View").AddComponent<UnityEngine.Camera>();
            camera.transform.SetParent(root.transform, false);
            rig = root.AddComponent<RtsCameraController>();
            Set("viewCamera", camera);
        }

        [TearDown] public void TearDown() => Object.DestroyImmediate(root);
        void Set(string name, object value) => typeof(RtsCameraController).GetField(name, Private).SetValue(rig, value);

        [Test]
        public void FreeCameraFocusCanCrossAllMapEdgesWithoutShrinkingZoom()
        {
            Set("distance", 54f); Set("pitch", 22f); Set("yaw", 47f);
            camera.fieldOfView = 60; camera.aspect = 2.4f;
            foreach (float x in new[] { -300f, 300f }) foreach (float z in new[] { -300f, 300f })
            {
                var focus = new Vector3(x, 2, z);
                rig.Focus(focus, true);
                Assert.That(root.transform.position, Is.EqualTo(focus));
                Assert.That(Vector3.Distance(camera.transform.position, focus), Is.EqualTo(54).Within(.001));
                Assert.That(Vector3.Dot(camera.transform.forward, (focus - camera.transform.position).normalized), Is.GreaterThan(.99999));
            }
        }

        [Test]
        public void OptionalLookAtBoundaryRemainsAvailable()
        {
            Set("restrictLookAtToMap", true);
            rig.Focus(new Vector3(300, 7, -300), true);
            Assert.That(root.transform.position, Is.EqualTo(new Vector3(46, 7, -38)));
        }

        [Test]
        public void OrbitChangesYawAndTiltAndNeverFlipsPastPitchLimits()
        {
            var apply = typeof(RtsCameraController).GetMethod("ApplyOrbitDelta", Private);
            apply.Invoke(rig, new object[] { new Vector2(100, 100) });
            rig.Focus(Vector3.zero, true);
            Assert.That(root.transform.eulerAngles.y, Is.EqualTo(18).Within(.001));
            Assert.That(camera.transform.localEulerAngles.x, Is.EqualTo(34).Within(.001));
            foreach (var pair in new[] { new Vector2(10000, 22), new Vector2(-10000, 78) })
            {
                apply.Invoke(rig, new object[] { new Vector2(0, pair.x) });
                rig.Focus(Vector3.zero, true);
                Assert.That(camera.transform.localEulerAngles.x, Is.EqualTo(pair.y).Within(.001));
                Assert.That(Vector3.Dot(camera.transform.forward, -camera.transform.position.normalized), Is.GreaterThan(.99999));
            }
        }

        [Test]
        public void WorldOrbitContinuesAcrossUiUntilRelease()
        {
            var gesture = new CameraOrbitGesture();
            Assert.That(gesture.Update(true, true, true, false), Is.True);
            Assert.That(gesture.Update(false, true, true, true), Is.True);
            Assert.That(gesture.Update(false, true, false, true), Is.True);
            Assert.That(gesture.Update(false, false, true, false), Is.False);
        }

        [Test]
        public void PressOnUiOrOutsideViewCannotBecomeOrbitByDraggingIntoWorld()
        {
            foreach (var insideAndOverUi in new[] { new[] { true, true }, new[] { false, false } })
            {
                var gesture = new CameraOrbitGesture();
                Assert.That(gesture.Update(true, true, insideAndOverUi[0], insideAndOverUi[1]), Is.False);
                Assert.That(gesture.Update(false, true, true, false), Is.False);
            }
        }

        [Test]
        public void LosingFocusOrDisablingControlsRequiresNewMousePress()
        {
            var gesture = (CameraOrbitGesture)typeof(RtsCameraController).GetField("orbit", Private).GetValue(rig);
            gesture.Update(true, true, true, false);
            rig.ControlsEnabled = false; rig.ControlsEnabled = true;
            Assert.That(gesture.Update(false, true, true, false), Is.False);
            gesture.Update(true, true, true, false);
            typeof(RtsCameraController).GetMethod("OnApplicationFocus", Private).Invoke(rig, new object[] { false });
            Assert.That(gesture.Update(false, true, true, false), Is.False);
        }

        [Test]
        public void UninitializedPointerAndOtherEditorPanelsCannotTriggerEdgePan()
        {
            var inside = typeof(RtsCameraController).GetMethod("PointerInsideViewport", BindingFlags.NonPublic | BindingFlags.Static);
            var size = new Vector2(1920, 1080);
            foreach (var point in new[] { Vector2.zero, new Vector2(0, 10), new Vector2(10, 0), new Vector2(1920, 10), new Vector2(10, 1080), new Vector2(-1, 500) })
                Assert.That(inside.Invoke(null, new object[] { point, size, true }), Is.False, "Rejected border/default/off-window position: " + point);
            // A valid last-known coordinate must also be rejected when the pointer is over an Editor panel.
            Assert.That(inside.Invoke(null, new object[] { new Vector2(1, 1), size, false }), Is.False);
            Assert.That(inside.Invoke(null, new object[] { new Vector2(1, 1), size, true }), Is.True);
        }
    }
}
