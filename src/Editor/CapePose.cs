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
    /// it fall through an animation (BASIC_CAPE_MINIFG's IDLE_DEF.AN4, in a compressed format nothing here reads yet).
    /// Its skeleton is three chains down the sheet (Cape_Left1/3/5, Cape_Centre1/3/5, Cape_Right1/3/5), so bending each
    /// chain down at its joints and skinning the sheet to them gives the hanging cape. Render thread only.
    /// </summary>
    public static class CapePose
    {
        /// <summary>How far each chain bends down at its first joint (the shoulders) and at its third (mid-back), in degrees.</summary>
        const float ShoulderBend = 76, BackBend = 10;

        public static void Hang(EditorScene scene, List<string> problems)
        {
            if (scene.OriginalScene is not GScene_4F gscene || gscene.CharacterData.Count == 0)
                return;
            var data = gscene.CharacterData[0];
            int count = data.JointData.Count;
            if (count == 0 || data.Inv_Wt == null || data.Inv_Wt.Count < count)
                return;

            // Each joint's bind position, and where its posed sheet goes: its own bend (about the X axis, through the
            // joint, sending the sheet's +z (backwards) down) after its parent's.
            var posed = new Matrix4x4[count];
            for (int j = 0; j < count; j++)
            {
                var m = data.Inv_Wt[j].mtx;
                var inverse = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                Matrix4x4.Invert(inverse, out var bind);
                var bend = Matrix4x4.CreateRotationX(Bend(data.JointData[j].Name) * MathF.PI / 180, bind.Translation);
                int parent = data.JointData[j].ParentIndex;
                posed[j] = parent < j ? bend * posed[parent] : bend;
            }

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
