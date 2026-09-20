using Jev.Gameplay.Camera;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class GroundViewBoundsTests
    {
        readonly Vector2 low = new Vector2(-46, -38), high = new Vector2(46, 38);

        [TestCase(0, 24)] [TestCase(45, 24)] [TestCase(90, 24)]
        [TestCase(0, 54)] [TestCase(45, 54)] [TestCase(90, 54)] [TestCase(270, 54)]
        public void PanningRotatingAndMaximumZoomKeepAllGroundCornersOnMap(float yaw, float requestedDistance)
        {
            const float fov = 45, pitch = 53, aspect = 16f / 9;
            float distance = GroundViewBounds.FitDistance(requestedDistance, 10, 54, low, high, fov, aspect, yaw, pitch);
            Assert.That(distance, Is.InRange(10, requestedDistance));
            foreach (float x in new[] { -200f, 200f }) foreach (float z in new[] { -200f, 200f })
            {
                var focus = GroundViewBounds.ClampLookAt(new Vector3(x, 7, z), low, high, fov, aspect, yaw, pitch, distance);
                Assert.That(focus.y, Is.EqualTo(7));
                // Independent camera-space rays verify the scalar projection/clamp helper.
                var rotation = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(pitch, 0, 0);
                var cameraOffset = rotation * Vector3.back * distance;
                float tangent = Mathf.Tan(fov * Mathf.Deg2Rad * .5f);
                foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 })
                {
                    var ray = rotation * new Vector3(sx * tangent * aspect, sy * tangent, 1);
                    var corner = focus + cameraOffset + ray * (-cameraOffset.y / ray.y);
                    Assert.That(corner.x, Is.InRange(low.x - .001f, high.x + .001f));
                    Assert.That(corner.z, Is.InRange(low.y - .001f, high.y + .001f));
                }
            }
        }

        [Test]
        public void OversizedMinimumViewFallsBackToMapCenterWithoutChangingLookAtHeight()
        {
            var minimum = new Vector2(-2, -3); var maximum = new Vector2(4, 5);
            float distance = GroundViewBounds.FitDistance(54, 10, 54, minimum, maximum, 45, 16f / 9, 45, 53);
            Assert.That(distance, Is.EqualTo(10));
            var result = GroundViewBounds.ClampLookAt(new Vector3(100, 17, -100), minimum, maximum, 45, 16f / 9, 45, 53, distance);
            Assert.That(result, Is.EqualTo(new Vector3(1, 17, 1)));
        }
    }
}
