using Diorama.UI;
using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Diorama.Editor;

namespace Diorama.Rendering
{
    public class CameraController
    {
        public Camera Camera;

        private const float BaseSpeed = 3.0f;
        private const float FastSpeed = 20.0f;

        private const float Sensitivity = 0.1f;

        private const float FocusDuration = 0.3f;

        public CameraController(Camera camera)
        {
            Camera = camera;
        }

        bool isFocusing = false;
        float t = 0;
        Vector3 startPos, targetPos;
        float startYaw, targetYaw;
        float startPitch, targetPitch;

        public void Update(AvaloniaKeyboardState input, double deltaTime)
        {
            float dt = (float)deltaTime;

            if (isFocusing)
            {
                t += dt;
                float alpha = Math.Clamp(t / FocusDuration, 0f, 1f);

                Camera.Position = Vector3.Lerp(startPos, targetPos, alpha);
                Camera.Yaw = LerpAngle(startYaw, targetYaw, alpha);
                Camera.Pitch = MathHelper.Lerp(startPitch, targetPitch, alpha);

                if (alpha >= 1f)
                    isFocusing = false;
            }
            else
            {
                float speed = input.IsKeyDown(Key.LeftShift) ? FastSpeed : BaseSpeed;

                Vector3 front = Camera.GetFront();

                if (input.IsKeyDown(Key.W))
                    Camera.Position += front * speed * dt;

                if (input.IsKeyDown(Key.A))
                    Camera.Position -= Vector3.Normalize(Vector3.Cross(front, Camera.Up)) * speed * dt;

                if (input.IsKeyDown(Key.S))
                    Camera.Position -= front * speed * dt;

                if (input.IsKeyDown(Key.D))
                    Camera.Position += Vector3.Normalize(Vector3.Cross(front, Camera.Up)) * speed * dt;

                if (input.IsKeyDown(Key.Space))
                    Camera.Position += Camera.Up * speed * dt;

                if (input.IsKeyDown(Key.LeftCtrl))
                    Camera.Position -= Camera.Up * speed * dt;
            }
        }

        float LerpAngle(float a, float b, float t)
        {
            float delta = (b - a + 180f) % 360f - 180f;
            return a + delta * t;
        }

        public void ProcessMouse(float deltaX, float deltaY)
        {
            deltaX *= Sensitivity;
            deltaY *= Sensitivity;

            Camera.Yaw += deltaX;
            Camera.Pitch -= deltaY; // invert Y

            // Clamp pitch
            Camera.Pitch = Math.Clamp(Camera.Pitch, -89f, 89f);
        }

        /// <summary>
        /// Assumes a DX-coordinate-space object!
        /// </summary>
        /// <param name="obj"></param>
        public void FocusOn(EditorGeometryObject obj)
        {
            startPos = Camera.Position;
            startYaw = Camera.Yaw;
            startPitch = Camera.Pitch;

            if (obj == null) return;

            Vector3 target;
            float size;
            if (GeometryBounds(obj, out Vector3 min, out Vector3 max))
            {
                // character parts carry no instance bounds, so use the part's own shape
                target = (min + max) / 2;
                size = MathF.Max((max - min).Length * 1.5f, 0.1f);
            }
            else
            {
                target = EditorUtils.FlipCoordSpace(obj.Parent.Parent.BoundsCenterAndDistSqrd.Xyz);

                float maxScale = MathF.Max(obj.Scale.X, MathF.Max(obj.Scale.Y, obj.Scale.Z));
                size = obj.Parent.Parent.ApproxSize * maxScale;
            }

            size = MathF.Min(size, 500); // stops the camera from disappearing into the oblivion

            targetPos = target + (startPos - target).Normalized() * size;

            //targetPos = (new Vector4(target, 1)).Xyz - Camera.GetFront() * 5;

            (targetYaw, targetPitch) = CalculateLookAt(startPos, target);

            t = 0;
            isFocusing = true;
        }

        /// <summary>
        /// Places the camera so the whole scene is in view, looking slightly down at it.
        /// Bounds are in DX coordinate space, like FocusOn.
        /// </summary>
        public void FrameScene(EditorScene scene) => FrameScenes([scene]);

        /// <summary>Points the camera at everything shown in <paramref name="scenes"/>, such as the pieces of a hub area.</summary>
        public void FrameScenes(IEnumerable<EditorScene> scenes)
        {
            Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
            bool any = false;

            foreach (var scene in scenes)
            {
                foreach (EditorSceneObject obj in scene.Objects.OfType<EditorSceneObject>())
                {
                    if (!scene.IsShown(obj))
                        continue;

                    Vector3 center = obj.BoundsCenterAndDistSqrd.Xyz;
                    Vector3 extents = obj.BoundsExtentsAndRadius.Xyz;

                    if (extents != Vector3.Zero && float.IsFinite(center.X + center.Y + center.Z + extents.X + extents.Y + extents.Z))
                    {
                        for (int corner = 0; corner < 8; corner++)
                        {
                            Vector3 p = EditorUtils.FlipCoordSpace(center + extents * Corner(corner));
                            min = Vector3.ComponentMin(min, p);
                            max = Vector3.ComponentMax(max, p);
                        }
                        any = true;
                        continue;
                    }

                    // characters leave their instance bounds zeroed: measure the parts instead
                    var clip = obj.ClipObject ?? obj.Lods?.FirstOrDefault()?.ClipObject;
                    foreach (var geo in clip?.Elements ?? [])
                    {
                        if (!GeometryBounds(geo, out Vector3 geoMin, out Vector3 geoMax))
                            continue;
                        min = Vector3.ComponentMin(min, geoMin);
                        max = Vector3.ComponentMax(max, geoMax);
                        any = true;
                    }
                }
            }

            if (!any) return;

            Vector3 target = (min + max) / 2;
            float radius = MathF.Max((max - min).Length / 2, 0.02f);

            // Far enough for a sphere of that radius to fit the 45 degree view, within the 1000 unit far plane
            float distance = MathF.Min(radius / MathF.Sin(MathHelper.DegreesToRadians(22.5f)) * 1.1f, 500);

            Camera.Position = target + new Vector3(0, 0.5f, 1).Normalized() * distance;
            (Camera.Yaw, Camera.Pitch) = CalculateLookAt(Camera.Position, target);
            isFocusing = false;
        }

        static Vector3 Corner(int corner) => new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);

        /// <summary>
        /// The box around a part as drawn, in view space: from its mesh's culling box if it has one, otherwise from its vertices.
        /// </summary>
        public static bool GeometryBounds(EditorGeometryObject geo, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);

            var nuMesh = geo.Mesh?.OriginalMesh;
            if (nuMesh == null)
                return false;

            List<System.Numerics.Vector3> points = new();
            var centre = nuMesh.CentreExtents[0];
            var extents = nuMesh.CentreExtents[1];
            if (extents.X != 0 || extents.Y != 0 || extents.Z != 0)
            {
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 c = Corner(corner);
                    points.Add(new System.Numerics.Vector3(centre.X + extents.X * c.X, centre.Y + extents.Y * c.Y, centre.Z + extents.Z * c.Z));
                }
            }
            else
            {
                points.AddRange(OBJConverter.ReadVertices(nuMesh).Select(v => v.Position));
            }

            foreach (var point in points)
            {
                if (!float.IsFinite(point.X + point.Y + point.Z))
                    continue;
                Vector3 p = EditorUtils.FlipCoordSpace((new Vector4(point.X, point.Y, point.Z, 1) * geo.Transform).Xyz);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }

            return min.X <= max.X;
        }

        (float yaw, float pitch) CalculateLookAt(Vector3 from, Vector3 to)
        {
            Vector3 direction = (to - from).Normalized();

            float pitch = MathF.Asin(direction.Y);

            float yaw = MathF.Atan2(direction.Z, direction.X);

            // Convert to degrees
            pitch = MathHelper.RadiansToDegrees(pitch);
            yaw = MathHelper.RadiansToDegrees(yaw);

            return (yaw, pitch);
        }
    }
}
