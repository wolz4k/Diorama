using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

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
        /// the meshes changed (to redraw). The source's meshes drawn with a material in <paramref name="separate"/> (see-through
        /// ones) go into meshes of their own, added to the model and drawn with <paramref name="separateMaterial"/>.
        /// </summary>
        public static List<string> Transplant(GScene_4F source, GScene_4F target, out List<NuRenderMesh> changed, ISet<int>? separate = null, int separateMaterial = -1)
        {
            var changedMeshes = new List<NuRenderMesh>();
            changed = changedMeshes;
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
            int rigid = 0;
            string? rigidJoint = null;

            for (int lod = 0; lod < target.CharacterData.Count; lod++)
            {
                var fromDrawn = MeshesOf(source, Math.Min(lod, source.CharacterData.Count - 1)).Where(m => !m.Breakup).ToList();
                var from = fromDrawn.Select(m => m.Mesh).Distinct().ToList();
                var apart = fromDrawn.Where(m => separate != null && separate.Contains(m.Material)).Select(m => m.Mesh).ToHashSet();
                // this model's own meshes take the new geometry; its breakup pieces (and own meshes left over) are emptied
                var drawn = MeshesOf(target, lod);
                var own = drawn.Where(m => !m.Breakup).Select(m => m.Mesh).Distinct().Where(m => !done.Contains(m)).ToList();
                var rest = drawn.Where(m => m.Breakup).Select(m => m.Mesh).Distinct().Where(m => !done.Contains(m) && !own.Contains(m)).ToList();
                if (own.Count == 0 || from.Count == 0) continue;

                // every vertex with the joints (of this model) its weights name
                var vertices = new List<Vertex>();
                var joints = new List<int[]>();
                var triangles = new List<int>();
                var clear = new List<int>();
                foreach (var mesh in from)
                {
                    int start = vertices.Count;
                    var into = apart.Contains(mesh) ? clear : triangles;
                    foreach (var v in OBJConverter.ReadVertices(mesh))
                    {
                        int Joint(ushort i)
                        {
                            int joint = mesh.SkinMtxMap is { Count: > 0 } map ? map[Math.Min(i, map.Count - 1)] : i;
                            var (mine, by) = joint < jointMap.Length ? jointMap[joint] : (0, null);
                            if (by != null) usedMissing.Add($"{sourceJoints[joint]} (follows {by})");
                            return mine;
                        }
                        joints.Add(new[] { Joint(v.BlendIndices.X), Joint(v.BlendIndices.Y), Joint(v.BlendIndices.Z), Joint(v.BlendIndices.W) });
                        vertices.Add(v);
                    }
                    into.AddRange(OBJConverter.ReadIndices(mesh).Select(i => i + start));
                }
                float[] Weights(Vertex v) => new[] { v.BlendWeights.X, v.BlendWeights.Y, v.BlendWeights.Z, v.BlendWeights.W };

                // Rigid pieces (no weights: the other game hangs them on a joint by a transform) would fold away when
                // skinned, so they go wholly on the joint the rest of the part mostly moves with.
                var weighted = Enumerable.Range(0, vertices.Count).Where(i => Weights(vertices[i]).Any(w => w > 0)).ToList();
                if (weighted.Count < vertices.Count)
                {
                    int common = weighted.Count == 0 ? (jointMap.Length > 0 ? jointMap[0].Joint : Math.Max(0, target.CharacterData[0].JointData.FindIndex(j => j.ParentIndex == 255)))
                        : weighted.GroupBy(i => joints[i][Array.IndexOf(Weights(vertices[i]), Weights(vertices[i]).Max())]).OrderByDescending(g => g.Count()).First().Key;
                    for (int i = 0; i < vertices.Count; i++)
                        if (!Weights(vertices[i]).Any(w => w > 0))
                        {
                            vertices[i].BlendWeights = new Vector4(1, 0, 0, 0);
                            joints[i] = new[] { common, common, common, common };
                        }
                    rigid += vertices.Count - weighted.Count;
                    rigidJoint = common < target.CharacterData[0].JointData.Count ? target.CharacterData[0].JointData[common].Name : "the model itself";
                }
                IEnumerable<int> Uses(int vertex) => joints[vertex].Where((j, k) => Weights(vertices[vertex])[k] > 0 || k == 0);

                // A mesh's joint map holds a fixed number of joints (27 in this game, padded with 255), so the triangles are
                // grouped into meshes that each need no more joints than that: each goes, in order of the joints it uses (so
                // neighbours stay together), to the group it adds fewest joints to, a new group only when none has room.
                int room = own.Max(m => m.SkinMtxMap?.Count ?? 0);
                List<(List<int> Triangles, List<int> Joints)> Group(List<int> tris, int most)
                {
                    var groups = new List<(List<int> Triangles, List<int> Joints)>();
                    var order = Enumerable.Range(0, tris.Count / 3).Select(t => (t, Need: tris.Skip(t * 3).Take(3).SelectMany(Uses).Distinct().OrderBy(j => j).ToList()))
                        .OrderBy(x => x.Need[0]).ThenBy(x => x.Need.Count > 1 ? x.Need[1] : -1).ToList();
                    foreach (var (t, need) in order)
                    {
                        if (room > 0 && need.Count > room) throw new InvalidDataException($"A triangle of LOD {lod} is weighted to {need.Count} joints; a mesh holds {room}.");
                        var fits = groups.Where(g => room == 0 || g.Joints.Union(need).Count() <= room).OrderBy(g => need.Count(j => !g.Joints.Contains(j))).ToList();
                        var group = fits.Count > 0 && (need.All(fits[0].Joints.Contains) || groups.Count >= most || room == 0) ? fits[0] : default;
                        if (group.Triangles == null) groups.Add(group = (new List<int>(), new List<int>()));
                        group.Triangles.AddRange(tris.Skip(t * 3).Take(3));
                        foreach (int j in need) if (!group.Joints.Contains(j)) group.Joints.Add(j);
                    }
                    return groups;
                }

                // one group into one mesh: its vertices, joint map and blend shapes
                void Fill(NuRenderMesh mesh, List<int> tris, List<int> palette, NuRenderMesh[]? others)
                {
                    var local = new Dictionary<int, ushort>();
                    var meshVertices = new List<Vertex>();
                    var meshIndices = new List<ushort>();
                    foreach (int vertex in tris)
                    {
                        if (!local.TryGetValue(vertex, out var at))
                        {
                            var v = Copy(vertices[vertex]);
                            var w = Weights(v);
                            ushort Slot(int k) => room > 0 ? (ushort)Math.Max(0, w[k] > 0 || k == 0 ? palette.IndexOf(joints[vertex][k]) : 0) : (ushort)joints[vertex][k];
                            v.BlendIndices = new VectorI4(Slot(0), Slot(1), Slot(2), Slot(3));
                            local[vertex] = at = (ushort)meshVertices.Count;
                            meshVertices.Add(v);
                        }
                        meshIndices.Add(at);
                    }
                    if (meshVertices.Count > ushort.MaxValue)
                        throw new InvalidDataException($"LOD {lod} has {meshVertices.Count:N0} vertices in one mesh, more than one mesh can hold (65,535).");
                    var map = palette.Select(j => (byte)j).ToList();
                    while (map.Count < (mesh.SkinMtxMap?.Count ?? 0)) map.Add(255); // the game's joint maps are padded with 255
                    // blend shapes (a cape's, a face's) follow: each new vertex moves like the nearest old one
                    OBJConverter.RemapBlendShapes(mesh, OBJConverter.ReadVertices(mesh), meshVertices);
                    OBJConverter.SetMeshData(mesh, meshVertices, meshIndices.ToArray(), others);
                    if (room > 0) mesh.SkinMtxMap = map;
                    changedMeshes.Add(mesh);
                    done.Add(mesh);
                }

                var solid = Group(triangles, own.Count);
                if (solid.Count > own.Count)
                    throw new InvalidDataException($"LOD {lod} of the other part moves on more joints than fit: it needs {solid.Count} meshes of up to {room} joints, and this model has {own.Count}. Build on a part with more meshes (or fewer joints).");
                for (int g = 0; g < solid.Count; g++)
                    Fill(own[g], solid[g].Triangles, solid[g].Joints, all);
                foreach (var extra in own.Skip(solid.Count).Concat(rest))
                {
                    Empty(extra, vertices[0], all);
                    changed.Add(extra);
                    done.Add(extra);
                }

                // see-through pieces: meshes of their own, like this model's first, drawn with the given material
                var clearGroups = clear.Count > 0 && separateMaterial >= 0 ? Group(clear, 1) : new(); // as few as hold their joints
                var (clip, element) = FindElement(target, lod, own[0]);
                foreach (var (tris, palette) in clearGroups)
                {
                    if (clip == null || element == null) throw new InvalidDataException($"LOD {lod}'s mesh isn't drawn by an element of its own, so no see-through mesh can be added beside it.");
                    var mesh = MeshLike(own[0]);
                    target.MeshSceneBlock.Meshes = target.MeshSceneBlock.Meshes.Append(mesh).ToArray();
                    all = target.MeshSceneBlock.Meshes;
                    Fill(mesh, tris, palette, null);
                    var added = CopyElement(element);
                    added.MeshIndex = (short)(all.Length - 1);
                    added.MaterialIndex = (short)separateMaterial;
                    clip.Elements = clip.Elements.Append(added).ToArray();
                }
                int groupCount = solid.Count;
                notes.Add($"LOD {lod}: {from.Count} part{(from.Count == 1 ? "" : "s")} of {vertices.Count:N0} vertices and {(triangles.Count + clear.Count) / 3:N0} triangles{(groupCount > 1 ? $", in {groupCount} meshes (each holds up to {room} joints)" : "")}{(clearGroups.Count > 0 ? $"; {clear.Count / 3:N0} see-through triangles in {clearGroups.Count} mesh{(clearGroups.Count == 1 ? "" : "es")} of their own" : "")}.");
            }

            if (changed.Count == 0)
                throw new InvalidDataException("Neither model has character geometry to move (no LOD draws a mesh).");

            notes.Insert(0, $"Moved the other model's geometry into this one: shape, UVs, vertex colours and bone weights; this model's materials and shaders, so the game can draw it.");
            if (rigid > 0)
                notes.Add($"{rigid:N0} vertices weren't weighted to any joint (the other game hangs those pieces on one by a transform); they now move with {rigidJoint}.");
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

        /// <summary>The clip object and element that draw <paramref name="mesh"/> in a LOD (this game's layout), or nulls.</summary>
        static (NuClipObject? Clip, NuClipItem? Element) FindElement(GScene_4F scene, int lod, NuRenderMesh mesh)
        {
            var display = scene.DisplayScene;
            int index = Array.IndexOf(scene.MeshSceneBlock.Meshes, mesh);
            foreach (var md in scene.CharacterData[lod].LayerMetadata)
            {
                if (md.SpecialIndex < 0 || md.SpecialIndex >= display.SpecialObjects.Count) continue;
                var special = display.SpecialObjects[md.SpecialIndex];
                if (special.ClipObjectIndex >= display.ClipObjects.Count) continue;
                var clip = display.ClipObjects[(int)special.ClipObjectIndex];
                var element = clip.Elements.FirstOrDefault(e => e.MeshIndex == index);
                if (element != null) return (clip, element);
            }
            return (null, null);
        }

        static NuClipItem CopyElement(NuClipItem e) => new NuClipItem
        {
            OldGeometryIndex = e.OldGeometryIndex, OldMaterialIndex = e.OldMaterialIndex, MaterialIndex = e.MaterialIndex, TransformIndex = e.TransformIndex,
            LightmapIndex = e.LightmapIndex, TransformIndex2 = e.TransformIndex2, MeshIndex = e.MeshIndex, LightmapType = e.LightmapType,
            TransformType = e.TransformType, GeomType = e.GeomType, Unused = e.Unused, RequiresLightState = e.RequiresLightState, IsFaceOn = e.IsFaceOn,
        };

        /// <summary>A new mesh laid out like <paramref name="like"/> (its buffers' layouts and flags), with nothing in it yet.</summary>
        static NuRenderMesh MeshLike(NuRenderMesh like) => new NuRenderMesh
        {
            VertexBuffers = (VertexList[])like.VertexBuffers.Clone(),
            VertexBufferFlags = like.VertexBufferFlags.Select(f => f == 0 || f == 0x503 ? 0x502u : f).ToArray(),
            VertexBufferOffsets = new int[like.VertexBufferOffsets.Length],
            Indices = Array.Empty<ushort>(),
            IndicesFlags = like.IndicesFlags,
            VbInstBits = like.VbInstBits,
            SkinMtxMap = like.SkinMtxMap?.ToList(),
            DefunctOptFlags = like.DefunctOptFlags,
            CentreExtents = (System.Numerics.Vector4[])like.CentreExtents.Clone(),
            DensityDiscDiameter = like.DensityDiscDiameter,
        };

        static Vertex Copy(Vertex v) => new Vertex
        {
            Position = v.Position, Normal = v.Normal, Tangent = v.Tangent, ColorSet0 = v.ColorSet0, ColorSet1 = v.ColorSet1,
            UVSet01 = v.UVSet01, UVSet23 = v.UVSet23, BlendIndices = v.BlendIndices, BlendWeights = v.BlendWeights,
        };

        /// <summary>A mesh reduced to one invisible triangle, so a part this model had doesn't show.</summary>
        static void Empty(NuRenderMesh mesh, Vertex sample, NuRenderMesh[] all)
        {
            var v = new List<Vertex> { sample, sample, sample };
            OBJConverter.RemapBlendShapes(mesh, OBJConverter.ReadVertices(mesh), v);
            OBJConverter.SetMeshData(mesh, v, new ushort[] { 0, 1, 2 }, all);
        }
    }
}
