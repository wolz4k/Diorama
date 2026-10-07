
using BrickVault.Types;
using Diorama.Core;
using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Core.Filetypes.GSC.Components.RESH;
using Diorama.Core.Filetypes.SHADERS;
using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Core.IO;
using Diorama.Editor.Material;
using Diorama.Editor.Metadata;
using Diorama.Editor.ShaderSystem;
using Diorama.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Diorama.Editor
{
    public static class GSceneConverter
    {
        public static EditorScene FromGScene(GScene scene, NxgTextures nxg_textures, NxgTextures? cubemap_textures, out List<string> problems)
        {
            problems = new List<string>();

            EditorScene editorScene = new EditorScene();
            editorScene.OriginalScene = scene;
            editorScene.Name = Path.GetFileName(scene.Path);
            editorScene.SceneTransform = Matrix4.CreateScale(1f, 1f, -1f); // All meshes are flipped, so this unflips them

            editorScene.Metadata = GetMetadata(scene);

            Dictionary<ushort[], RenderIndicesBuffer> convertedIBuffer = new();
            Dictionary<VertexList, RenderVertexBuffer> convertedVBuffer = new();

            RenderMesh[] meshes = new RenderMesh[scene.MeshSceneBlock.Meshes.Length];
            for (int i = 0; i < scene.MeshSceneBlock.Meshes.Length; i++)
            {
                NuRenderMesh nuMesh = scene.MeshSceneBlock.Meshes[i];


                RenderVertexBuffer[] vBuffers = new RenderVertexBuffer[nuMesh.VertexBuffers.Length];
                for (int j = 0; j < vBuffers.Length; j++)
                {
                    var buffer = nuMesh.VertexBuffers[j];

                    if (!convertedVBuffer.ContainsKey(buffer))
                    {
                        var vBuffer = RenderVertexBuffer.FromBuffer(buffer);
                        convertedVBuffer.Add(buffer, vBuffer);
                        editorScene.Add(vBuffer);
                    }

                    vBuffers[j] = convertedVBuffer[buffer];
                }

                var indices = nuMesh.Indices;
                if (!convertedIBuffer.ContainsKey(indices))
                {
                    var ibu = RenderIndicesBuffer.FromBuffer(indices);
                    convertedIBuffer.Add(indices, ibu);
                    editorScene.GetOrAdd(ibu);
                }

                RenderIndicesBuffer iBuffer = convertedIBuffer[nuMesh.Indices];

                RenderMesh mesh = new RenderMesh(vBuffers, iBuffer, nuMesh.VertexBufferOffsets);
                mesh.VerticesBase = (int)nuMesh.VerticesBase;
                mesh.VerticesCount = (int)nuMesh.VerticesCount;
                mesh.IndicesBase = (int)nuMesh.IndicesBase;
                mesh.IndicesCount = (int)nuMesh.IndicesCount;

                mesh.OriginalMesh = nuMesh;

                meshes[i] = mesh;
            }

            var textures = new List<RenderTexture>();
            bool needsSharedTextures = false;

            try
            {
                //var nxg_textures = NxgTextures.Read(Path.ChangeExtension(filePath, "nxg_textures"));
                if (nxg_textures != null)
                {
                    // the other scenes this one names (a hub piece's shared texture scene), where name-only textures are
                    string ownName = Path.GetFileName(scene.Path ?? "");
                    var sharedScenes = (scene.ResourceHeader?.FileTree?.GetIndexedFiles().Values ?? Enumerable.Empty<string>())
                        .Where(p => p.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).Equals(ownName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    for (int i = 0; i < nxg_textures.TextureSet.Textures.Length; i++)
                    {
                        var texture = nxg_textures.TextureSet.Textures[i];
                        if (texture.Data == null && nxg_textures.TextureSet.Version == 1)
                            needsSharedTextures = true;
                        //if (nxg_textures.TextureSet.Version == 1 && texture.Data == null)
                        //{
                        //    texture.Header.Path = texStrings[i].Value;
                        //    NxgTextures.LoadExternalTexture(texture);
                        //}

                        var shared = sharedScenes.Count > 0 && SharedTextures.IsNameOnly(texture) ? SharedTextures.Find(sharedScenes, texture.Header.Name) : null;
                        textures.Add(RenderTexture.FromNuTexture(texture, shared));
                    }
                    editorScene.OriginalTextures = nxg_textures;
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("No texture sheet found for scene, using blank textures");
            }

            var texStrings = scene.Metadata.MetaStrings;

            //if (nxg_textures.TextureSet.Version == 1)
            //{
            //    try
            //    {
            //        var legoCityShared = FileProvider.GetFile(@"levels\lego_city\lego_city\lego_city_shared_textures_dx11.nxg_textures");
            //        if (legoCityShared != null)
            //        {
            //            var legoCityTextures = NxgTextures.Read(legoCityShared);
            //            for (int i = 0; i < nxg_textures.TextureSet.Textures.Length; i++)
            //            {
            //                var texture = nxg_textures.TextureSet.Textures[i];
            //                if (texture.Data == null)
            //                {
            //                    foreach (var legoCityTex in legoCityTextures.TextureSet.Textures)
            //                    {
            //                        if (legoCityTex.Header.Path == texStrings[i].Value)
            //                        {
            //                            nxg_textures.TextureSet.Textures[i] = legoCityTex;
            //                            textures[i] = RenderTexture.FromNuTexture(legoCityTex);
            //                            break;
            //                        }
            //                    }
            //                }
            //            }
            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        Console.WriteLine($"Could not load shared textures: {ex.Message}");
            //    }
            //}

            var cubemap_tex = new List<RenderTexture>();

            try
            {
                //var nxg_textures = NxgTextures.Read(Path.ChangeExtension(filePath, "nxg_textures"));
                if (cubemap_textures != null)
                {
                    for (int i = 0; i < cubemap_textures.TextureSet.Textures.Length; i++)
                    {
                        cubemap_tex.Add(RenderTexture.FromNuTexture(cubemap_textures.TextureSet.Textures[i]));
                    }
                    editorScene.OriginalCubemapTextures = cubemap_textures;
                }
            }
            catch (FileNotFoundException)
            { // probably don't need this message, instead if a material requests an environment map then send an output message
                Console.WriteLine("No cubemap texture sheet found for scene, using blank textures (if appropriate)");
            }

            // TODO: Just do the reference sorting here instead
            EditorMaterial[] materials = new EditorMaterial[scene.MaterialBlock.Materials.Length];
            var embeddedTextures = scene.MaterialBlock.EmbeddedTextures;
            Dictionary<uint, NuMtlOldReferencedMaterial> toReplace = new();
            Dictionary<string, EditorScene> loaded = new();
            if (embeddedTextures != null)
            {
                for (int i = 0; i < embeddedTextures.Count; i++)
                {
                    var embed = embeddedTextures[i];
                    toReplace.Add(embed.ReplacedMaterialIndex, embed);

                    if (!loaded.ContainsKey(embed.SourceGsc))
                    {
                        string referencedPath = embed.SourceGsc.Replace("nxg", "dx11"); // dx11 files will reference nxg replacement scenes
                    
                        using RawFile gsceneFile = FileProvider.GetFile(referencedPath);
                        string texturesFilePath = Path.ChangeExtension(referencedPath, "nxg_textures");
                        GScene childScene = GScene.Parse(gsceneFile);
                    
                        using RawFile texturesFile = FileProvider.GetFile(texturesFilePath);
                        NxgTextures childTextures = NxgTextures.Read(texturesFile);
                    
                        string cubemapsFilePath = texturesFilePath.Replace("_dx11.nxg_textures", "_cubemaps_dx11.nxg_textures");
                        using RawFile cubemapTexturesFile = FileProvider.GetFile(cubemapsFilePath);
                        NxgTextures childCubemaps = null;
                        if (cubemapTexturesFile != null)
                            childCubemaps = NxgTextures.Read(cubemapTexturesFile);

                        EditorScene loadedScene = GSceneConverter.FromGScene(childScene, childTextures, childCubemaps, out List<string> childProblems);
                        if (childProblems.Count == 0)
                        {
                            loaded.Add(embed.SourceGsc, loadedScene);
                        }
                    }
                }
            }

            for (int i = 0; i < materials.Length; i++)
            {
                NuMaterialData nuMaterialData = scene.MaterialBlock.Materials[i];

                EditorMaterial material = new EditorMaterial(nuMaterialData, textures)
                {
                    OriginalIndex = i
                };

                if (toReplace.ContainsKey((uint)i))
                {
                    var materialReplacement = toReplace[(uint)i];

                    var childScene = loaded[materialReplacement.SourceGsc];

                    foreach (var mat in childScene.Materials)
                    {
                        if (mat.Name == materialReplacement.MaterialName)
                        {
                            material.OverridingReference = mat;
                        }
                    }
                }

                materials[i] = material;

                editorScene.Materials.Add(material);
            }

            List<NuLightmapData> gsceneLightmaps = scene.LightmapDataBlock.Lightmaps;
            EditorLightmap[] lightmaps = new EditorLightmap[scene.LightmapDataBlock.Lightmaps.Count];
            for (int i = 0; i < lightmaps.Length; i++)
            {
                NuLightmapData nuLightmap = gsceneLightmaps[i];
                EditorLightmap lightmap = new EditorLightmap();
                lightmap.Original = nuLightmap;

                lightmap.AmbientOcclusion = ResolveTexture(textures, nuLightmap.AoTID);
                lightmap.Smooth = ResolveTexture(textures, nuLightmap.SmoothTID);
                lightmap.Directional0 = ResolveTexture(textures, nuLightmap.DirectionalTIDs0);
                lightmap.Directional1 = ResolveTexture(textures, nuLightmap.DirectionalTIDs1);
                lightmap.Directional2 = ResolveTexture(textures, nuLightmap.DirectionalTIDs2);
                lightmap.Offsets[0] = nuLightmap.TexCoordOffset0;
                lightmap.Offsets[1] = nuLightmap.TexCoordOffset1;
                lightmap.Scales[0] = nuLightmap.TexCoordScale0;
                lightmap.Scales[1] = nuLightmap.TexCoordScale1;

                lightmaps[i] = lightmap;
            }

            var display = scene.DisplayScene;
            int matrixId = -1;
            int materialId = -1;
            int lightmapId = -1;
            Dictionary<int, EditorGeometryObject> geometry = new();

            List<EditorClipObject> allClipObjects = new();

            if (display.DisplayItems != null)
            {
                for (int commandId = 0; commandId < display.DisplayItems.Count; commandId++)
                {
                    NuDefunctDisplayItem command = display.DisplayItems[commandId];
                    switch (command.Command)
                    {
                        case DisplayCommand.Material:
                            materialId = (int)command.Index;
                            break;
                        case DisplayCommand.LightMap:
                            lightmapId = (int)command.Index;
                            break;
                        case DisplayCommand.MaterialClip:
                            break;
                        case DisplayCommand.Matrix:
                            matrixId = (int)command.Index;
                            break;
                        case DisplayCommand.DynamicGeo:
                            break;
                        case DisplayCommand.Mesh:
                            NuTransformMtx local = display.TransformMtxs[matrixId];

                            Matrix4 mtx = local.AsMatrix();

                            RenderMesh mesh = meshes[command.Index];

                            EditorGeometryObject obj = new EditorGeometryObject();
                            obj.OriginalTransform = local;
                            obj.Mesh = mesh;
                            if (local.IsZero())
                            {
                                mtx = Matrix4.Identity;
                                obj.CanEditTransform = false;
                            }
                            obj.Transform = mtx;
                            if (materialId > -1)
                            {
                                obj.Material = materials[materialId];
                            }
                            if (lightmapId > 0)
                            {
                                obj.Lightmap = lightmaps[lightmapId];
                            }

                            if (scene.CharacterData.Count > 0)
                            {
                                obj.HighestDetail = scene.CharacterData[0];
                            }

                            //Meshes.Add(mesh);
                            geometry.Add(commandId, obj);
                            //editorScene.Objects.Add(obj);
                            break;
                    }
                }

                foreach (var displayClip in display.ClipObjects)
                {
                    EditorClipObject clip = new EditorClipObject();
                    foreach (var el in displayClip.Elements)
                    {
                        if (!geometry.ContainsKey(el.OldGeometryIndex)) continue;
                        var geo = geometry[el.OldGeometryIndex];
                        clip.Elements.Add(geo);
                        geo.Parent = clip; // TODO: Remove
                        geo.Material = materials[el.OldMaterialIndex];
                        geo.Original = el;
                    }

                    allClipObjects.Add(clip);
                    clip.SceneOwner = editorScene;
                }
            }
            else
            {
                foreach (var displayClip in display.ClipObjects)
                {
                    EditorClipObject clip = new EditorClipObject();
                    foreach (var el in displayClip.Elements)
                    {
                        // Some cutscene props (CUT_GLINT) have no meshes or transforms at all, so there's nothing to draw.
                        // A missing transform reads as zero, which already means "identity, not editable".
                        if (el.MeshIndex < 0 || el.MeshIndex >= meshes.Length) continue;
                        NuTransformMtx local = el.TransformIndex >= 0 && el.TransformIndex < (display.TransformMtxs?.Count ?? 0) ? display.TransformMtxs![el.TransformIndex] : new NuTransformMtx();
                        Matrix4 mtx = local.AsMatrix();
                        RenderMesh mesh = meshes[el.MeshIndex];
                        EditorGeometryObject obj = new EditorGeometryObject();
                        obj.OriginalTransform = local;
                        obj.Transform = mtx;
                        if (local.IsZero())
                        {
                            mtx = Matrix4.Identity;
                            obj.CanEditTransform = false;
                        }
                        obj.Transform = mtx;
                        obj.Mesh = mesh;
                        if (el.MaterialIndex > -1)
                        {
                            obj.Material = materials[el.MaterialIndex];
                        }
                        if (el.LightmapIndex > -1 && lightmaps.Length > el.LightmapIndex)
                        {
                            obj.Lightmap = lightmaps[el.LightmapIndex];
                        }

                        if (scene.CharacterData.Count > 0)
                        {
                            obj.HighestDetail = scene.CharacterData[0];
                        }

                        clip.Elements.Add(obj);
                        obj.Parent = clip;
                        obj.Original = el;
                        //geometry.Add(i, obj);
                    }
                    allClipObjects.Add(clip);
                    clip.SceneOwner = editorScene;
                }
            }


            for (int i = 0; i < display.SceneInstances.Count; i++)
            {
                var instance = display.SceneInstances[i];
                EditorSceneObject sceneObject = new EditorSceneObject();
                editorScene.Objects.Add(sceneObject);
                sceneObject.Name = $"SceneInstance_{i}";
                sceneObject.FadeDistances = instance.FadeDistances; // TODO: probably dangerous?
                sceneObject.ApproxSize = instance.ApproxSize;

                var geoBounds = display.BoundsCenterAndDistSqrd[i];
                sceneObject.BoundsCenterAndDistSqrd = new Vector4(geoBounds.X, geoBounds.Y, geoBounds.Z, geoBounds.W);

                var extents = display.BoundsExtentsAndRadius[i];
                sceneObject.BoundsExtentsAndRadius = new Vector4(extents.X, extents.Y, extents.Z, extents.W);

                if (instance.ClipObjectIndex > -1)
                {
                    sceneObject.ClipObject = allClipObjects[instance.ClipObjectIndex];
                    sceneObject.ClipObject.Parent = sceneObject;
                }

                if (instance.HasLods)
                {
                    sceneObject.Lods = new EditorLodGroup[4];

                    for (int j = 0; j < instance.Lods.Length; j++)
                    {
                        var lod = instance.Lods[j];
                        sceneObject.Lods[j] = new(j);
                        sceneObject.Lods[j].FadeDistance = instance.FadeDistances[j];

                        if (lod.NumInstances == 0) continue;

                        sceneObject.Lods[j].Spare = new();
                        foreach (var lodClip in LodClips(lod, display, allClipObjects, 0))
                        {
                            sceneObject.Lods[j].ClipObject = lodClip;
                            lodClip.Parent = sceneObject;
                            sceneObject.Lods[j].Spare.Add(lodClip);
                        }
                    }

                    sceneObject.UseLodGroups = true;
                }
            }

            for (int i = 0; i < display.SpecialObjects.Count; i++)
            {
                var specialObject = display.SpecialObjects[i];
                if (specialObject.Name == null)
                {
                    specialObject.Name = scene.NameTable.Names.GetString((int)specialObject.NameIndex);
                }
                EditorSpecialObject eSpecialObject = new EditorSpecialObject(specialObject);
                editorScene.SpecialObjects.Add(eSpecialObject);
                if (specialObject.InstanceIndex != -1)
                {
                    EditorSceneObject sceneObject = (EditorSceneObject)editorScene.Objects[specialObject.InstanceIndex];
                    sceneObject.SpecialObject = eSpecialObject;
                }
            }

            editorScene.Textures = new ObservableCollection<RenderTexture>(textures);
            editorScene.CubemapTextures = new ObservableCollection<RenderTexture>(cubemap_tex);

            if (scene.CharacterData.Count > 0)
            {
                var char0data = scene.CharacterData[0];

                var joints = char0data.JointData;
                var jointTransforms = char0data.Inv_Wt;

                for (int i = 0; i < joints.Count; i++)
                {
                    var mtx = jointTransforms[i].mtx.ToMatrix4().Inverted();
                    var joint = new EditorJoint(joints[i], mtx, editorScene);

                    if (joint.Original.ParentIndex != 255 && joint.Original.ParentIndex < editorScene.AllJoints.Count)
                    {
                        EditorJoint parent = (EditorJoint)editorScene.AllJoints[joint.Original.ParentIndex];
                        joint.Parent = parent;
                    }
                    else
                    {
                        editorScene.Joints.Add(joint);
                    }

                    editorScene.AllJoints.Add(joint);
                }

                var poiData = char0data.PointsOfInterest;
                for (int i = 0; i < poiData.Count; i++)
                {
                    var poi = poiData[i];
                    editorScene.PoIs.Add(new EditorPointOfInterest(poi, editorScene)
                    {
                        // some points of interest have no joint (255, or past the joints, as in ADDITIONALMODEL_HOODDOWN)
                        Parent = poi.ParentJointIdx < editorScene.AllJoints.Count ? (EditorJoint)editorScene.AllJoints[poi.ParentJointIdx] : null
                    });
                }

                EditorLayerMetadata[] metadataItems = new EditorLayerMetadata[char0data.LayerMetadata.Count];
                for (int i = 0; i < char0data.LayerMetadata.Count; i++)
                {
                    var metadata = char0data.LayerMetadata[i];

                    var editorMetadata = new EditorLayerMetadata(metadata)
                    {
                        SceneOwner = editorScene
                    };

                    // indices past the lists are tolerated: a few files point at joints they don't have (ADDITIONALMODEL_HOODDOWN)
                    if (metadata.JointIndex != 255 && metadata.JointIndex < editorScene.AllJoints.Count)
                    {
                        editorMetadata.Joint = (EditorJoint)editorScene.AllJoints[metadata.JointIndex];
                    }
                    if (metadata.SpecialIndex >= 0 && metadata.SpecialIndex < editorScene.SpecialObjects.Count)
                    {
                        editorMetadata.SpecialObject = (EditorSpecialObject)editorScene.SpecialObjects[metadata.SpecialIndex];
                    }

                    metadataItems[i] = editorMetadata;
                }

                for (int i = 0; i < char0data.Layers.Count; i++)
                {
                    var layer = char0data.Layers[i];

                    var editorLayer = new EditorLayer(layer);

                    int total = layer.NumRigids + layer.NumSkins;
                    for (int j = 0; j < total && layer.MetaDataIndex + j < metadataItems.Length; j++)
                    {
                        editorLayer.LayerItems.Add(metadataItems[layer.MetaDataIndex + j]);
                    }

                    editorScene.Layers.Add(editorLayer);
                }

                for (int i = 0; i < scene.CharacterData.Count; i++)
                {
                    var lodGroup = scene.CharacterData[i];
                    for (int j = 0; j < lodGroup.LayerMetadata.Count; j++)
                    {
                        var metadata = lodGroup.LayerMetadata[j];

                        if (metadata.SpecialIndex >= 0 && metadata.SpecialIndex < editorScene.SpecialObjects.Count)
                        {
                            EditorSpecialObject obj = (EditorSpecialObject)editorScene.SpecialObjects[metadata.SpecialIndex];
                            obj.LODGroup = i;

                            string layerName = metadata.Layer < lodGroup.Layers.Count ? lodGroup.Layers[metadata.Layer].Name ?? "" : "";
                            obj.IsBreakup = IsBreakupLayer(layerName);
                        }
                    }
                }

                PlaceBreakupParts(editorScene, scene);
            }

            editorScene.CharacterLodCount = scene.CharacterData.Count;

            if (scene.Metadata != null) // A bit of a sanity check
            {
                editorScene.LoadedTextureCount = textures.Count;
                if (scene.Metadata.MetaStrings.Count != textures.Count)
                {
                    editorScene.MetaStringsMatchedTextures = false;
                    problems.Add($"Note: the scene lists {scene.Metadata.MetaStrings.Count} texture names but its .NXG_TEXTURES file has {textures.Count} textures.");
                    problems.Add("    A few of the game's own files do this (extra dummy lightmap names), and saving keeps the list as it is.");
                    problems.Add("    If this is your mod's file, make sure its textures were saved with it (Save Textures).");
                }
                else
                { 
                    var metaStrings = scene.Metadata.MetaStrings;
                    for (int i = 0; i < metaStrings.Count; i++)
                    {
                        textures[i].GscName = metaStrings[i].Value;
                    }
                }
            }

            return editorScene;
        }

        /// <summary>
        /// The clip objects one LOD of a scene instance draws. A hierarchical LOD lists other instances, and in the hub levels
        /// (Gotham, Metropolis, Apokolips...) such a child can itself be a LOD group with no clip of its own (index -1), so
        /// it's resolved to its own most detailed LOD in turn. Indices past the lists are skipped.
        /// </summary>
        private static IEnumerable<EditorClipObject> LodClips(NuSceneInstanceLod lod, NuDisplayScene display, List<EditorClipObject> clips, int depth)
        {
            for (int k = 0; k < lod.NumInstances; k++)
            {
                int index = (int)lod.FirstInstance + k;
                if (lod.LodHeirarchical == 0)
                {
                    if (index >= 0 && index < clips.Count)
                        yield return clips[index];
                    continue;
                }

                if (index < 0 || index >= display.SceneInstances.Count)
                    continue;
                var child = display.SceneInstances[index];
                if (child.ClipObjectIndex >= 0 && child.ClipObjectIndex < clips.Count)
                    yield return clips[child.ClipObjectIndex];
                else if (depth < 8 && child.Lods != null)
                    foreach (var childLod in child.Lods.Where(l => l.NumInstances > 0).Take(1))
                        foreach (var clip in LodClips(childLod, display, clips, depth + 1))
                            yield return clip;
            }
        }

        /// <summary>
        /// Breakup parts are rigid, unskinned meshes stored relative to the character's hips (joint 1), so drawn as they are
        /// they sit a hip height below the body. Each is drawn with its LOD's bind-pose hips matrix (the inverse of
        /// Inv_Wt[1]); that's only the editor's transform, the file's own (zero) one is kept.
        /// </summary>
        private static void PlaceBreakupParts(EditorScene editorScene, GScene scene)
        {
            foreach (var sceneObject in editorScene.Objects.OfType<EditorSceneObject>())
            {
                var special = sceneObject.SpecialObject;
                if (special == null || !special.IsBreakup || special.LODGroup < 0 || special.LODGroup >= scene.CharacterData.Count)
                    continue;

                var inverses = scene.CharacterData[special.LODGroup].Inv_Wt;
                if (inverses == null || inverses.Count < 2)
                    continue;

                var m = inverses[1].mtx;
                var inverse = new Matrix4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                if (inverse.Determinant == 0)
                    continue;
                var hips = inverse.Inverted();

                var clips = new[] { sceneObject.ClipObject }.Concat(sceneObject.Lods?.Select(l => l?.ClipObject) ?? []);
                foreach (var clip in clips.Where(c => c != null).Distinct())
                    foreach (var geo in clip!.Elements)
                        if (geo.Mesh?.OriginalMesh?.SkinMtxMap is not { Count: > 0 } && !geo.CanEditTransform)
                            geo.Transform = hips;
            }
        }

        public static EditorScene FromGScene(string filePath, out List<string> problems)
        {
            GScene scene = GScene.Parse(filePath);

            NxgTextures textures = null;
            try
            {
                textures = NxgTextures.Read(Path.ChangeExtension(filePath, "nxg_textures"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not open / parse nxg_textures file!");
            }

            NxgTextures cubemap_textures = null;
            string? directory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);

            string cubemapPath = Path.Combine(
                directory ?? "",
                $"{fileName}_cubemaps_dx11.nxg_textures"
            );

            try
            {
                cubemap_textures = NxgTextures.Read(cubemapPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not open / parse cubemap.nxg_textures file!");
            }

            EditorScene editorScene = FromGScene(scene, textures, cubemap_textures, out problems);

            return editorScene;   
        }

        // Breakup layers are named TT6_BreakUp, TT6_Breakups, TT1_BlowUps, TT6_RoofBreakoff...
        private static bool IsBreakupLayer(string layerName)
        {
            return layerName.Contains("break", StringComparison.OrdinalIgnoreCase)
                || layerName.Contains("blowup", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ConvertToBool(byte val)
        {
            if (val == 1)
                return true;
            if (val == 0)
                return false;
            throw new Exception("Invalid variable contents!");
        }

        static RenderTexture ResolveTexture(List<RenderTexture> textures, int index)
        {
            if (index < 0 || textures.Count <= (index))
                return RenderTexture.GetWhiteTexture();

            return textures[index];
        }

        static EditorMetadata GetMetadata(GScene scene)
        {
            Dictionary<int, string> files = scene.ResourceHeader.FileTree.GetIndexedFiles();

            EditorMetadata metadata = new EditorMetadata();
            foreach (var reference in scene.ResourceHeader.References)
            {
                EditorResourceReference editorRef = new EditorResourceReference();
                
                if (!files.TryGetValue((int)reference.Hash, out string path))
                {
                    throw new Exception("File not found in resource header!");
                }

                editorRef.FilePath = path;
                editorRef.Type = reference.Type;
                editorRef.PlatformsAndClasses = reference.PlatformsAndClasses;

                if (reference.Checksum != null)
                {
                    editorRef.Checksum = reference.Checksum.Checksum;
                }
                
                metadata.Resources.Add(editorRef);
            }

            metadata.LoadedSignature = metadata.Signature();
            return metadata;
        }

        public static void Write(EditorScene scene)
        {
            var nuScene = scene.OriginalScene;
            ConvertResourceHeader(scene);
            ConvertMaterials(scene);
            ConvertMetadata(scene);
            ConvertCharacterData(scene);

            HandleShaders(scene);

            CreateNameTable(scene);

            string path = nuScene.Path;

#if DEBUG
            //path = path.Replace(".GSC", "_1.GSC").Replace(".GHG", "_1.GHG");
#endif

            using (RawFile file = RawFile.Create(path))
            {
                GSerializationContext ctx = new GSerializationContext();
                nuScene.Write(file, ctx);
            }
        }

        private const string defaultString = "default_string";
        /// <summary>
        /// Keeps the scene's name table and points each special object at its name in it. Joints, layers, points of
        /// interest, splines and occluders also hold offsets into this table, so it's never rebuilt: an unchanged name keeps
        /// its offset, and a renamed object reuses a matching name or has its new one added at the end.
        /// </summary>
        public static void CreateNameTable(EditorScene scene)
        {
            var table = scene.OriginalScene.NameTable;
            if (table.Names?.Buffer == null)
            {
                NuAlignedBuffer nameTable = new NuAlignedBuffer();
                nameTable.SetPadding(1);
                nameTable.AddString(defaultString);
                foreach (EditorSpecialObject specialObject in scene.SpecialObjects)
                {
                    specialObject.Original.NameIndex = (uint)nameTable.AddString(specialObject.Name);
                }
                nameTable.Finalise();
                table.Names = nameTable;
                return;
            }

            var buffer = new List<byte>(table.Names.Buffer);
            foreach (EditorSpecialObject specialObject in scene.SpecialObjects)
            {
                string name = specialObject.Name ?? "";
                if (NameAt(buffer, (int)specialObject.Original.NameIndex) == name)
                    continue;
                int existing = FindName(buffer, name);
                if (existing >= 0)
                {
                    specialObject.Original.NameIndex = (uint)existing;
                    continue;
                }
                specialObject.Original.NameIndex = (uint)buffer.Count;
                buffer.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
                buffer.Add(0);
            }
            table.Names.Buffer = buffer.ToArray();
        }

        private static string? NameAt(List<byte> buffer, int offset)
        {
            if (offset < 0 || offset >= buffer.Count) return null;
            int end = offset;
            while (end < buffer.Count && buffer[end] != 0) end++;
            return System.Text.Encoding.UTF8.GetString(buffer.GetRange(offset, end - offset).ToArray());
        }

        /// <summary>The offset of <paramref name="name"/> as a whole string in the table, or -1.</summary>
        private static int FindName(List<byte> buffer, string name)
        {
            for (int start = 0; start < buffer.Count;)
            {
                int end = start;
                while (end < buffer.Count && buffer[end] != 0) end++;
                if (end > start && NameAt(buffer, start) == name) return start;
                start = end + 1;
            }
            return -1;
        }

        public static void ConvertCharacterData(EditorScene scene)
        {
            var char0data = scene.OriginalScene.CharacterData;

            //if (char0data != null && char0data.Count > 1)
            //{
            //    var item0 = char0data[0];
            //    char0data.Clear();
            //    char0data.Add(item0);
            //}
        }

        public static void ConvertMaterials(EditorScene scene)
        {
            // A texture still showing the white stand-in was never resolved (no texture, or an index past the texture
            // file, as in scenes whose name list starts with dummy lightmaps), so it keeps the index it was read with.
            int IndexOf(RenderTexture texture, int read) => RenderTexture.IsWhitePlaceholder(texture) ? read : scene.Textures.IndexOf(texture);

            foreach (var mat in scene.Materials)
            {
                mat.Original.Diffuse0Index = IndexOf(mat.Diffuse0, mat.Original.Diffuse0Index);
                mat.Original.Diffuse1Index = IndexOf(mat.Diffuse1, mat.Original.Diffuse1Index);
                mat.Original.Normal0Index = IndexOf(mat.Normal0, mat.Original.Normal0Index);
                mat.Original.Normal1Index = IndexOf(mat.Normal1, mat.Original.Normal1Index);

                mat.Original.Specular0Index = IndexOf(mat.Specular0, mat.Original.Specular0Index);

                mat.Original.OldTid = mat.Original.Diffuse0Index;

                //mat.Original.materialFlags_glow = 1;
                //mat.Original.KGlow = 0.8f;
            }
        }

        public static void ConvertResourceHeader(EditorScene scene)
        {
            var nuScene = scene.OriginalScene;
            // Rebuilding the file tree can list the paths in a different order from the game's (ARKHAMGROUND), so only
            // rebuild it when the references were changed
            if (scene.Metadata.LoadedSignature != null && scene.Metadata.Signature() == scene.Metadata.LoadedSignature)
                return;

            var rawResources = scene.Metadata.Resources;

            List<EditorResourceReference> resources = scene.Metadata.Resources.OrderBy(x => x.Type).ToList();

            int referenceCount = scene.Metadata.Resources.Count;

            List<NuResourceReference> references = new List<NuResourceReference>();

            string[] paths = new string[referenceCount];

            for (int i = 0; i < referenceCount; i++)
            {
                paths[referenceCount - 1 - i] = resources[i].FilePath = NuExtensions.StandardiseLower(resources[i].FilePath);
            }

            NuFileTree filetree = NuFileTree.FromPaths(paths, scene.OriginalScene.ResourceHeader.FileTree.Version);

            Dictionary<int, string> fileDictionary = filetree.GetIndexedFiles();

            foreach (var reference in resources)
            {
                int hash = -1;
                foreach (int key in fileDictionary.Keys)
                {
                    if (fileDictionary[key] == reference.FilePath)
                    {
                        hash = key;
                        break;
                    }
                }

                //if (hash == -1) throw new Exception("Error when serializing resources!");

                NuResourceReference nuReference = new NuResourceReference()
                {
                    Type = reference.Type,
                    Hash = (uint)hash,
                    PlatformsAndClasses = reference.PlatformsAndClasses,
                    Discipline = -1
                };

                if (reference.Checksum != null)
                {
                    nuReference.Checksum = new NuCheckSum()
                    {
                        Checksum = reference.Checksum
                    };
                }

                references.Add(nuReference);
            }

            nuScene.ResourceHeader.References = references;
            nuScene.ResourceHeader.FileTree = filetree;
        }

        public static void ConvertMetadata(EditorScene scene)
        {
            GScene_4F originalScene = (GScene_4F)scene.OriginalScene;

            // A few scenes list more names than their texture file has (EFFECT_GRID_GLOW starts with three dummy lightmap
            // names), so the list isn't one name per texture: keep it as the file had it unless textures were added or removed.
            if (!scene.MetaStringsMatchedTextures && scene.Textures.Count == scene.LoadedTextureCount)
                return;

            // Refill the list it was read into (rather than a new one) so it keeps its ROTV/zero marker (see VectorMarkers)
            var textureStrings = originalScene.Metadata.MetaStrings ??= new List<NuDynamicString>();
            textureStrings.Clear();
            foreach (var tex in scene.Textures)
            {
                textureStrings.Add(new NuDynamicString(tex.GscName));
            }
        }

        public static void HandleShaders(EditorScene scene)
        {
            bool needed = false;
            foreach (var eMat in scene.Materials)
            {
                if (eMat.FingerprintChanged)
                {
                    needed = true;
                    break;
                }
            }

            if (!needed) return;

            string savePath = Path.GetDirectoryName(scene.OriginalScene.Path);

            List<string> shaderPaths = new();

            string[] shadersExtensions = ["pc_shaders", "ps4_shaders"];
            bool foundAnyShaders = false;

            foreach (var ext in shadersExtensions)
            {
                string shadersFilePath = Path.ChangeExtension(scene.OriginalScene.Path, ext);
                if (Path.Exists(shadersFilePath))
                {
                    foundAnyShaders = true;
                    shaderPaths.Add(shadersFilePath);
                    Console.WriteLine($"Found shaders file: {Path.GetFileName(shadersFilePath)}");
                }
            }
            //bool foundAnyShaders = false;
            //foreach (var file in scene.Metadata.Resources)
            //{
            //    string clean = file.FilePath.ToLower();
            //    string diskFilepath = Path.Combine(savePath, Path.GetFileName(clean));
            //    if (clean.Contains("shaders") && Path.Exists(diskFilepath))
            //    {
            //        foundAnyShaders = true;

            //        shaderPaths.Add(diskFilepath);
            //        Console.WriteLine($"Found shaders file: {Path.GetFileName(clean)}");
            //    }
            //}

            //if (foundAnyShaders == false)
            //{
            //    Console.WriteLine("Could not find any shaders files that are referenced in the resource header - Cannot update shaders!");
            //}

            foreach (var shaderPath in shaderPaths)
            {
                string shaderExtension = Path.GetExtension(shaderPath);

                NxgShaders shaders = null;

                using (RawFile file = new RawFile(shaderPath))
                {
                    shaders = NxgShaders.Read(file);

                    HashSet<uint> fileShaderHashes = shaders.ShaderCache.Select(s => s.ConfigHash).ToHashSet();

                    int set = -1;

                    for (int i = 0; i < EditorMaterial.MaxShaderSet; i++)
                    {
                        bool validSet = true;
                        foreach (var eMat in scene.Materials)
                        {
                            bool emptySet = true;
                            var nuMat = eMat.Original;
                            foreach(uint shaderHash in eMat.EnumerateShadersInSet(i))
                            {
                                emptySet = false;
                                if (!fileShaderHashes.Contains(shaderHash))
                                {
                                    validSet = false;
                                    break;
                                }
                                else
                                {
                                    Console.WriteLine($"Valid shader on {i}");
                                }
                            }

                            if (emptySet)
                                validSet = false;

                            if (!validSet)
                                break;
                        }

                        if (validSet)
                        {
                            set = i;
                            break;
                        }

                    }

                    if (set == -1)
                    {
                        Console.WriteLine($"Could not determine correct shader set in file: {shaderPath}");
                        Console.WriteLine("This file will be skipped and will de-synchronise");
                        continue;
                    }

                    List<NxgShader> neededShaders = new();

                    HashSet<uint> neededShaderHashes = new();

                    var setArray = EditorShaderSystem.GetShaderSet();

                    Dictionary<string, HashSet<uint>> shadersToLocate = new();

                    HashSet<NuMaterialData> handledMaterials = new();

                    foreach (var eMat in scene.Materials)
                    {
                        if (!handledMaterials.Add(eMat.Original)) continue;

                        if (eMat.FingerprintChanged)
                        {
                            var fingerprint = eMat.Fingerprint;

                            var fileIndex = fingerprint.Fingerprint.FileIndex;

                            var substitute = EditorShaderSystem.GetSet(setArray, fileIndex, fingerprint.Fingerprint.MaterialName);

                            for (int i = 0; i < EditorMaterial.MaxShaderSet; i++)
                            {
                                uint[] hashArray = eMat.GetShaderSet(i);
                                if (hashArray == null)
                                    break;

                                Array.Copy(substitute.GetSet(i), 0, hashArray, 0, hashArray.Length);
                            }

                            string path = EditorShaderSystem.IndexedFiles[fileIndex];

                            path = Path.ChangeExtension(path, shaderExtension);

                            if (!shadersToLocate.ContainsKey(path))
                                shadersToLocate.Add(path, new HashSet<uint>());

                            foreach (uint hash in eMat.EnumerateShadersInSet(set))
                            {
                                shadersToLocate[path].Add(hash);
                            }
                        }
                        else
                        {
                            foreach (uint shaderHash in eMat.EnumerateShadersInSet(set))
                            {
                                if (neededShaderHashes.Contains(shaderHash)) continue;

                                neededShaders.Add(shaders.GetShader(shaderHash));
                                neededShaderHashes.Add(shaderHash);
                            }
                        }
                    }

                    foreach ((string otherFilePath, HashSet<uint> otherShaders) in shadersToLocate)
                    {
                        RawFile newShadersFile = null;
                        if (FileProvider.State == FileProvider.FileProviderState.Archives)
                        {
                            string datName = GetDatFile(otherFilePath);
                            string filePath = otherFilePath.Substring(datName.Length + 1);
                            newShadersFile = FileProvider.GetFileFromArchive(datName, filePath);
                        }
                        else
                        {
                            newShadersFile = FileProvider.GetFile(otherFilePath);
                        }

                        using (newShadersFile)
                        {
                            NxgShaders newShaders = NxgShaders.Read(newShadersFile);

                            foreach (var shader in newShaders.ShaderCache)
                            {
                                if (otherShaders.Contains(shader.ConfigHash) && neededShaderHashes.Add(shader.ConfigHash))
                                {
                                    neededShaders.Add(shader);
                                }
                            }
                        }
                    }

                    shaders.ShaderCache = neededShaders;
                }

                using (RawFile file = RawFile.Create(shaderPath))
                {
                    shaders.Handle(new SchemaSerializer(file, true), 0);
                }
            }
        }

        public static string GetDatFile(string path)
        {
            string dat = "";

            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '\\') break;

                dat = dat + path[i];
            }

            return dat;
        }
    }
}
