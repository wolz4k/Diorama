using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Diorama.Editor
{
    /// <summary>
    /// Brings a character part's geometry from another game's model into one of this game's, so the game can draw it:
    /// LEGO Marvel's Avengers' Falcon wings (NU20 0x52) into LEGO DC Super-Villains' feather wings (0x58). The other
    /// game's materials can't come along (they hold shader data for that game's compiled shaders), so the part takes
    /// the open model's materials and shaders, and keeps its own shape, UVs, vertex colours and bone weights. Weights
    /// follow the joints by name, so both models need the same skeleton (the same part kind: wings for wings).
    /// Tried in game: Falcon's wings on Hawkman show and move with Basic_Wings_Feather.
    /// </summary>
    public static class PartTransplant
    {
        /// <summary>
        /// The meshes a character LOD draws, with each one's material: the meshes its specials' clip objects name. Older
        /// scenes (0x52) name a display command instead, whose Mesh command names the mesh.
        /// </summary>
        public static List<(NuRenderMesh Mesh, int Material, bool Breakup)> MeshesOf(GScene_4F scene, int lod)
        {
            var list = new List<(NuRenderMesh, int, bool)>();
            var display = scene.DisplayScene;
            var meshes = scene.MeshSceneBlock.Meshes;
            var data = scene.CharacterData[lod];
            foreach (var md in data.LayerMetadata)
            {
                if (md.SpecialIndex < 0 || md.SpecialIndex >= display.SpecialObjects.Count) continue;
                string layer = md.Layer < data.Layers.Count ? data.Layers[md.Layer].Name ?? "" : "";
                bool breakup = GSceneConverter.IsBreakupLayer(layer);
                var special = display.SpecialObjects[md.SpecialIndex];
                if (special.ClipObjectIndex >= display.ClipObjects.Count) continue;
                foreach (var el in display.ClipObjects[(int)special.ClipObjectIndex].Elements)
                {
                    if (display.DisplayItems is { Count: > 0 } items)
                    {
                        if (el.OldGeometryIndex >= 0 && el.OldGeometryIndex < items.Count && items[el.OldGeometryIndex].Command == DisplayCommand.Mesh
                            && items[el.OldGeometryIndex].Index < meshes.Length)
                            list.Add((meshes[items[el.OldGeometryIndex].Index], el.OldMaterialIndex, breakup));
                    }
                    else if (el.MeshIndex >= 0 && el.MeshIndex < meshes.Length)
                        list.Add((meshes[el.MeshIndex], el.MaterialIndex, breakup));
                }
            }
            return list;
        }

        /// <summary>The layer a character model draws its parts on (bit mask, as a part's .CD "Default Layers" names it), or 0.</summary>
        public static int LayerMask(GScene_4F scene)
        {
            if (scene.CharacterData.Count == 0) return 0;
            var data = scene.CharacterData[0];
            int mask = 0;
            foreach (var md in data.LayerMetadata)
            {
                string layer = md.Layer < data.Layers.Count ? data.Layers[md.Layer].Name ?? "" : "";
                if (!GSceneConverter.IsBreakupLayer(layer) && md.Layer < 31) mask |= 1 << md.Layer;
            }
            return mask;
        }

        /// <summary>
        /// Replaces <paramref name="target"/>'s character geometry with <paramref name="source"/>'s, LOD by LOD (a LOD the
        /// source lacks takes its last one). Each target LOD's first mesh gets all of the source LOD's meshes; its other
        /// meshes and the breakup parts are emptied, so nothing of the old part shows. Returns notes for the modder and
        /// the meshes changed (to redraw).
        /// </summary>
        public static List<string> Transplant(GScene_4F source, GScene_4F target, out List<NuRenderMesh> changed)
        {
            changed = new();
            if (source.CharacterData.Count == 0 || target.CharacterData.Count == 0)
                throw new InvalidDataException("Both models need to be character parts (with joints and LODs), like wings, capes or hats.");

            // Weights follow joints by name: the other game's joint 4 is this one's joint with the same name.
            var targetJoints = target.CharacterData[0].JointData.Select(j => j.Name ?? "").ToList();
            var sourceJoints = source.CharacterData[0].JointData.Select(j => j.Name ?? "").ToList();
            var missing = sourceJoints.Where(n => !targetJoints.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            var jointMap = MapJoints(source.CharacterData[0].JointData, target.CharacterData[0].JointData);

            var notes = new List<string>();
            var done = new HashSet<NuRenderMesh>();
            var all = target.MeshSceneBlock.Meshes;
            var usedMissing = new HashSet<string>();

            for (int lod = 0; lod < target.CharacterData.Count; lod++)
            {
                var from = MeshesOf(source, Math.Min(lod, source.CharacterData.Count - 1)).Where(m => !m.Breakup).Select(m => m.Mesh).Distinct().ToList();
                // the part's own meshes first: the first gets the new geometry, the rest (and the breakup parts) are emptied
                var to = MeshesOf(target, lod).OrderBy(m => m.Breakup).Select(m => m.Mesh).Distinct().Where(m => !done.Contains(m)).ToList();
                if (to.Count == 0 || from.Count == 0) continue;

                var vertices = new List<Vertex>();
                var indices = new List<ushort>();
                var palette = new List<byte>();
                foreach (var mesh in from)
                {
                    int start = vertices.Count;
                    foreach (var v in OBJConverter.ReadVertices(mesh))
                    {
                        ushort Map(ushort i)
                        {
                            int joint = mesh.SkinMtxMap is { Count: > 0 } map ? map[Math.Min(i, map.Count - 1)] : i;
                            var (own, by) = joint < jointMap.Length ? jointMap[joint] : (0, null);
                            if (by != null) usedMissing.Add($"{sourceJoints[joint]} (follows {by})");
                            int at = palette.IndexOf((byte)own);
                            if (at < 0) { palette.Add((byte)own); at = palette.Count - 1; }
                            return (ushort)at;
                        }
                        v.BlendIndices = new VectorI4(Map(v.BlendIndices.X), Map(v.BlendIndices.Y), Map(v.BlendIndices.Z), Map(v.BlendIndices.W));
                        vertices.Add(v);
                    }
                    indices.AddRange(OBJConverter.ReadIndices(mesh).Select(i => (ushort)(i + start)));
                }
                if (vertices.Count > ushort.MaxValue)
                    throw new InvalidDataException($"LOD {lod} has {vertices.Count:N0} vertices, more than one mesh can hold (65,535).");

                // The game's joint maps are padded to a fixed length with 255.
                int length = to[0].SkinMtxMap?.Count ?? 0;
                while (palette.Count < length) palette.Add(255);

                OBJConverter.SetMeshData(to[0], vertices, indices.ToArray(), all);
                to[0].SkinMtxMap = palette;
                changed.Add(to[0]);
                done.Add(to[0]);
                foreach (var extra in to.Skip(1))
                {
                    Empty(extra, vertices[0], all);
                    changed.Add(extra);
                    done.Add(extra);
                }
                notes.Add($"LOD {lod}: {from.Count} part{(from.Count == 1 ? "" : "s")} of {vertices.Count:N0} vertices and {indices.Count / 3:N0} triangles.");
            }

            if (changed.Count == 0)
                throw new InvalidDataException("Neither model has character geometry to move (no LOD draws a mesh).");

            notes.Insert(0, $"Moved the other model's geometry into this one: shape, UVs, vertex colours and bone weights; this model's materials and shaders, so the game can draw it.");
            if (usedMissing.Count > 0)
                notes.Add($"Warning: this model's skeleton has no joint named {string.Join(", ", usedMissing.Order())}, so those parts hold still on the joint named instead of moving on their own. Pick a model of the same kind (wings for wings).");
            else if (missing.Count > 1 && missing.Count == sourceJoints.Count)
                notes.Add("Warning: the two skeletons share no joint names, so the part won't move properly.");

            int mask = LayerMask(target), sourceMask = LayerMask(source);
            if (mask != 0 && mask != sourceMask)
                notes.Add($"This model draws its parts on layer mask {mask}; the other game's part used {sourceMask}. In the part's .CD (Flux), set Default Layers, and the Cutscene, Hat, Hair, Cape and Christmas Hat Layers, to {mask}, or the game draws an empty layer.");
            notes.Add("Save As a new name for your mod (e.g. the other game's model name, which the part's .CD loads), never over this game's file. Its textures and shaders are copied beside it.");
            notes.Add("Colour: the part shows its vertex colours times the character's Tint Colour for this attachment (Flux). Textures from the other game don't come along.");
            return notes;
        }

        /// <summary>
        /// This model's joint for each of the other's: the one with the same name; for the top joint (named after the
        /// part: Armour_Hulkling, SkinnedHair_PeggyCarter), this model's top joint; otherwise its nearest parent's, which
        /// is then named (By) for the report.
        /// </summary>
        static (int Joint, string? By)[] MapJoints(List<NuJointData> from, List<NuJointData> to)
        {
            int top = Math.Max(0, to.FindIndex(j => j.ParentIndex == 255));
            var map = new (int, string?)[from.Count];
            var done = new bool[from.Count];
            (int, string?) Map(int i)
            {
                if (done[i]) return map[i];
                done[i] = true; // a loop in the parents ends here
                int own = to.FindIndex(j => string.Equals(j.Name, from[i].Name, StringComparison.OrdinalIgnoreCase));
                if (own >= 0) map[i] = (own, null);
                else if (from[i].ParentIndex == 255 || from[i].ParentIndex >= from.Count) map[i] = (top, null);
                else { var (parent, _) = Map(from[i].ParentIndex); map[i] = (parent, to[parent].Name); }
                return map[i];
            }
            for (int i = 0; i < from.Count; i++) Map(i);
            return map;
        }

        /// <summary>A mesh reduced to one invisible triangle, so a part this model had doesn't show.</summary>
        static void Empty(NuRenderMesh mesh, Vertex sample, NuRenderMesh[] all)
        {
            var v = new List<Vertex> { sample, sample, sample };
            OBJConverter.SetMeshData(mesh, v, new ushort[] { 0, 1, 2 }, all);
        }
    }
}
