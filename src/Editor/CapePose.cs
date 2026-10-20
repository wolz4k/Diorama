using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Diorama.Editor
{
    /// <summary>
    /// Hangs a cape. A cape model (CAPE_DX11) rests as a flat sheet lying straight back from the neck, and the game lets
    /// it fall through its idle animation (BASIC_CAPE_MINIFG's IDLE_DEF.AN4): its skeleton (three chains down the sheet,
    /// Cape_Left1/3/5, Cape_Centre1/3/5, Cape_Right1/3/5) is posed at that animation's first frame and the sheet skinned
    /// to it. Without the animation, each chain is bent down by an estimate. Render thread only.
    /// </summary>
    public static class CapePose
    {
        /// <summary>How far each chain bends down at its first joint (the shoulders) and at its third (mid-back), in degrees.</summary>
        const float ShoulderBend = 76, BackBend = 10;

        public static void Hang(EditorScene scene, List<string> problems, string? animation = null)
        {
            if (scene.OriginalScene is not GScene_4F gscene || gscene.CharacterData.Count == 0)
                return;
            var data = gscene.CharacterData[0];
            int count = data.JointData.Count;
            if (count == 0 || data.Inv_Wt == null || data.Inv_Wt.Count < count)
                return;

            // The idle fits CAPE_DX11's 13 joints; a complex cape (31) lists the same set, so it's bent by the estimate.
            var posed = (animation != null ? Animated(data, animation) : null) ?? Bent(data);
            Skin(scene, posed, problems);
        }

        static Matrix4x4 Inverse(NuMtx mtx)
        {
            var m = mtx.mtx;
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }

        /// <summary>
        /// The skinning matrices for the pose the cape's own idle sways about (BASIC_CAPE_MINIFG's IDLE_DEF.AN4): each joint's
        /// animated local transform after its parent's. The animation's z axis is the mesh's mirrored, so z is flipped
        /// back (translations' z, and rotations' x and y). Null when the animation doesn't fit the cape's skeleton.
        /// </summary>
        static Matrix4x4[]? Animated(NuCharacterData data, string animation)
        {
            int count = data.JointData.Count;
            var pose = Core.Filetypes.ANIM.An4Animation.AveragePose(animation, count);
            if (pose == null || pose.Count != count)
                return null;
            var world = new Matrix4x4[count];
            var posed = new Matrix4x4[count];
            for (int j = 0; j < count; j++)
            {
                var (t, q, s) = pose[j];
                var local = Matrix4x4.CreateScale(s)
                    * Matrix4x4.CreateFromQuaternion(new Quaternion(-q.X, -q.Y, q.Z, q.W))
                    * Matrix4x4.CreateTranslation(t.X, t.Y, -t.Z);
                int parent = data.JointData[j].ParentIndex;
                world[j] = parent < j ? local * world[parent] : local;
                posed[j] = Inverse(data.Inv_Wt[j]) * world[j];
            }
            return posed;
        }

        /// <summary>
        /// An estimate when there's no animation to read: each chain bent down at its first joint (the shoulders) and at
        /// its third (mid-back), about the X axis through the joint, sending the sheet's +z (backwards) down.
        /// </summary>
        static Matrix4x4[] Bent(NuCharacterData data)
        {
            int count = data.JointData.Count;
            var posed = new Matrix4x4[count];
            for (int j = 0; j < count; j++)
            {
                Matrix4x4.Invert(Inverse(data.Inv_Wt[j]), out var bind);
                var bend = Matrix4x4.CreateRotationX(Bend(data.JointData[j].Name) * MathF.PI / 180, bind.Translation);
                int parent = data.JointData[j].ParentIndex;
                posed[j] = parent < j ? bend * posed[parent] : bend;
            }
            return posed;
        }

        /// <summary>Moves every skinned mesh of the scene by the joints' matrices, weighted by its vertices' bone weights.</summary>
        static void Skin(EditorScene scene, Matrix4x4[] posed, List<string> problems)
        {

            var done = new Dictionary<NuRenderMesh, Rendering.RenderMesh>();
            foreach (var geo in scene.AllGeometry().ToList())
            {
                var nuMesh = geo.Mesh?.OriginalMesh;
                if (nuMesh?.SkinMtxMap == null || nuMesh.SkinMtxMap.Count == 0)
                    continue;
                if (!done.TryGetValue(nuMesh, out var mesh))
                {
                    var map = nuMesh.SkinMtxMap;
                    var vertices = OBJConverter.ReadVertices(nuMesh);
                    foreach (var v in vertices)
                    {
                        var skin = Blend(v, map, posed);
                        if (skin is not { } s) continue;
                        v.Position = Vector3.Transform(v.Position, s);
                        v.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, s));
                        v.Tangent = Vector3.TransformNormal(v.Tangent, s);
                    }
                    OBJConverter.SetMeshData(nuMesh, vertices, OBJConverter.ReadIndices(nuMesh), null);
                    done[nuMesh] = mesh = OBJConverter.BuildRenderMesh(nuMesh, scene);
                }
                geo.Mesh = mesh;
            }
            if (done.Count == 0)
                problems.Add($"{scene.Name} has no skinned meshes to hang.");
        }

        /// <summary>A vertex's posing: its joints' bends, weighted (its blend indices go through the mesh's skin map to joints).</summary>
        static Matrix4x4? Blend(Vertex v, List<byte> map, Matrix4x4[] posed)
        {
            Span<int> indices = [v.BlendIndices.X, v.BlendIndices.Y, v.BlendIndices.Z];
            Span<float> weights = [v.BlendWeights.X, v.BlendWeights.Y, v.BlendWeights.Z];
            var sum = new Matrix4x4();
            float total = 0;
            for (int k = 0; k < 3; k++)
            {
                if (weights[k] <= 0 || indices[k] >= map.Count || map[indices[k]] >= posed.Length) continue;
                sum += posed[map[indices[k]]] * weights[k];
                total += weights[k];
            }
            return total > 0 ? sum * (1 / total) : null;
        }

        /// <summary>Cape_Left1, Cape_Centre1, Cape_Right1 bend at the shoulders; the …3 joints a little more down the back.</summary>
        static float Bend(string name) => name.EndsWith('1') && name.StartsWith("Cape_", StringComparison.OrdinalIgnoreCase) ? ShoulderBend
            : name.EndsWith('3') && name.StartsWith("Cape_", StringComparison.OrdinalIgnoreCase) ? BackBend
            : 0;
    }
}
