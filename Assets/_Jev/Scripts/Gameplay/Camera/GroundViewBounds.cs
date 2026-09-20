using System;
using UnityEngine;

namespace Jev.Gameplay.Camera
{
    /// <summary>Perspective frustum projected onto the horizontal plane through the camera rig's look-at point.</summary>
    public static class GroundViewBounds
    {
        public static bool TryOffsets(float fieldOfView, float aspect, float yaw, float pitch, float distance, out Vector2 minimum, out Vector2 maximum)
        {
            minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            double p = pitch * Math.PI / 180, y = yaw * Math.PI / 180;
            double sp = Math.Sin(p), cp = Math.Cos(p), sy = Math.Sin(y), cy = Math.Cos(y);
            double tangent = Math.Tan(fieldOfView * Math.PI / 360);
            for (int vertical = -1; vertical <= 1; vertical += 2)
                for (int horizontal = -1; horizontal <= 1; horizontal += 2)
                {
                    double rayY = vertical * tangent * cp - sp;
                    if (rayY >= -0.00001) return false; // A corner sees the horizon instead of finite ground.
                    double travel = -distance * sp / rayY;
                    double localX = travel * horizontal * tangent * aspect;
                    double localZ = -distance * cp + travel * (vertical * tangent * sp + cp);
                    var offset = new Vector2((float)(cy * localX + sy * localZ), (float)(-sy * localX + cy * localZ));
                    minimum = Vector2.Min(minimum, offset); maximum = Vector2.Max(maximum, offset);
                }
            return true;
        }

        public static float FitDistance(float requested, float minimumDistance, float maximumDistance, Vector2 worldMinimum, Vector2 worldMaximum,
            float fieldOfView, float aspect, float yaw, float pitch)
        {
            float limit = maximumDistance;
            if (TryOffsets(fieldOfView, aspect, yaw, pitch, 1, out var low, out var high))
            {
                var extent = high - low; var map = worldMaximum - worldMinimum;
                limit = Mathf.Min(limit, map.x / Mathf.Max(.00001f, extent.x), map.y / Mathf.Max(.00001f, extent.y)) * .99999f;
            }
            else limit = minimumDistance;
            return Mathf.Clamp(requested, minimumDistance, Mathf.Max(minimumDistance, limit));
        }

        public static Vector3 ClampLookAt(Vector3 requested, Vector2 worldMinimum, Vector2 worldMaximum,
            float fieldOfView, float aspect, float yaw, float pitch, float distance)
        {
            if (TryOffsets(fieldOfView, aspect, yaw, pitch, distance, out var low, out var high))
            {
                var allowedMinimum = worldMinimum - low; var allowedMaximum = worldMaximum - high;
                if (allowedMinimum.x <= allowedMaximum.x && allowedMinimum.y <= allowedMaximum.y)
                {
                    requested.x = Mathf.Clamp(requested.x, allowedMinimum.x, allowedMaximum.x);
                    requested.z = Mathf.Clamp(requested.z, allowedMinimum.y, allowedMaximum.y);
                    return requested;
                }
            }
            var center = (worldMinimum + worldMaximum) * .5f;
            requested.x = center.x; requested.z = center.y;
            return requested;
        }
    }
}
