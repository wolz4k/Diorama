using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Diorama.Core;
using Diorama.Editor;
using Diorama.Editor.glTF;
using Diorama.Rendering;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Diorama.UI.ViewModels
{
    public class InspectorPanelViewModel : INotifyPropertyChanged
    {
        public SceneController Controller { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public InspectorPanelViewModel(SceneController controller)
        {
            Controller = controller;
        }

        public void ReplaceMesh(string path)
        {
            if (string.IsNullOrEmpty(path) || Controller.SelectedGeometry == null) return;

            var selectedGeo = Controller.SelectedGeometry;

            var scene = Controller.SelectedGeometry.Parent.SceneOwner;

            RenderService.Current.Enqueue(() =>
            {
                var snapshot = MeshSnapshot.Take(scene, $"Replace with {Path.GetFileName(path)}");
                try
                {
                    int sharing = ReplaceOne(scene, selectedGeo, path, out var notes);
                    scene.MeshUndo.Push(snapshot);

                    if (notes != null)
                    {
                        if (sharing > 1)
                            notes.Insert(1, $"{sharing} objects in this scene use this mesh, and all of them now show the new one.");
                        notes.Add("Not what you wanted? Undo replace (in the inspector, or the scene's right-click menu) puts the old mesh back.");
                        Controller.ShowMessageDialog("Mesh replaced", notes.Select(n => "• " + n));
                    }
                }
                catch (Exception ex)
                {
                    snapshot.Restore(); // a failed import leaves the scene as it was
                    Controller.ShowMessageDialog("Could not import from file", [ex.Message]);
                }
            });
        }

        /// <summary>Replaces <paramref name="geo"/>'s mesh from the file; returns how many objects draw that mesh. Render thread only.</summary>
        private int ReplaceOne(EditorScene scene, EditorGeometryObject geo, string path, out List<string>? notes)
        {
            RenderMesh oldMesh = geo.Mesh;
            RenderMesh newMesh;
            notes = null;
            if (isGLTF(path))
                newMesh = glTFConverter.GetObjectsFromGltf(path, oldMesh, scene);
            else
                newMesh = OBJConverter.MeshFromOBJ(path, oldMesh, scene, out notes);

            // the game mesh was changed in place, so every object drawing it gets the new one
            int sharing = 0;
            foreach (var other in scene.AllGeometry())
            {
                if (other.Mesh == oldMesh || other.Mesh?.OriginalMesh == newMesh.OriginalMesh)
                {
                    other.Mesh = newMesh;
                    sharing++;
                }
            }
            geo.Mesh = newMesh;
            return sharing;
        }

        /// <summary>
        /// Replaces the selected character part, and the same part in each of the character's other LODs (see
        /// <see cref="LodParts"/>), from one file. Bone weights and the rest are fitted to each LOD's own old mesh.
        /// </summary>
        public void ReplaceMeshInAllLods(string path)
        {
            if (string.IsNullOrEmpty(path) || Controller.SelectedGeometry is not { LodGroup: >= 0 } selectedGeo) return;

            var scene = selectedGeo.Parent.SceneOwner;

            RenderService.Current.Enqueue(() =>
            {
                var snapshot = MeshSnapshot.Take(scene, $"Replace with {Path.GetFileName(path)} in every LOD");
                try
                {
                    // matched before anything changes, since a replacement moves the part's bounding box
                    var targets = new List<(int Lod, EditorGeometryObject Geo, string Was)> { (selectedGeo.LodGroup, selectedGeo, selectedGeo.Name) };
                    var lines = new List<string>();
                    for (int lod = 0; lod < scene.CharacterLodCount; lod++)
                    {
                        if (lod == selectedGeo.LodGroup) continue;
                        if (LodParts.FindInLod(scene, selectedGeo, lod) is { } match)
                            targets.Add((lod, match, match.Name));
                        else
                            lines.Add($"LOD {lod}: no part with the same material in the same place (lower LODs often merge parts or drop small ones), so it's unchanged. Replace it there by hand if it should change too.");
                    }

                    List<string>? firstNotes = null;
                    foreach (var (lod, geo, was) in targets.OrderBy(t => t.Lod))
                    {
                        ReplaceOne(scene, geo, path, out var notes);
                        firstNotes ??= notes;
                        lines.Add($"LOD {lod}: replaced {was}{(geo == selectedGeo ? " (the part you picked)" : "")}.");
                    }
                    scene.MeshUndo.Push(snapshot);

                    lines.Sort(StringComparer.Ordinal);
                    if (firstNotes != null)
                        lines.AddRange(firstNotes.Skip(1)); // what was carried over, as for one part (its first line names the file)
                    lines.Add("Pick each LOD in the LOD menu to check it. Not right? Undo replace puts every LOD back at once.");
                    Controller.ShowMessageDialog("Mesh replaced in every LOD", lines.Select(n => "• " + n));
                }
                catch (Exception ex)
                {
                    snapshot.Restore(); // a failed import leaves the scene as it was
                    Controller.ShowMessageDialog("Could not import from file", [ex.Message]);
                }
            });
        }

        public void UndoReplace()
        {
            var scene = Controller.SelectedGeometry?.Parent?.SceneOwner ?? Controller.Scenes.FirstOrDefault();
            if (scene != null)
                Controller.UndoMeshReplace(scene);
        }

        public void ExportMesh(string path)
        {
            if (Controller.SelectedGeometry == null || path == null) return;

            var selectedGeo = Controller.SelectedGeometry;

            try
            {
                if (isGLTF(path))
                {
                    glTFConverter.WriteObjectsToGltf([selectedGeo], path);
                }
                else
                {
                    OBJConverter.WriteMeshToOBJ(selectedGeo, path);
                }
            }
            catch (Exception ex)
            {
                Controller.ShowMessageDialog("Could not export the mesh", [ex.Message]);
            }

        }

        private bool isGLTF(string path) => Path.GetExtension(path).ToLower() == ".gltf";

        public void DebugMesh()
        {
            if (Controller.SelectedGeometry == null) return;

            var selectedGeo = Controller.SelectedGeometry;

            //for (int i = 3; i < dump.Length; i += 4)
            //{
            //    dump[i] = 0xff;
            //}

            using (RawFile file = new RawFile(@"A:\debug.hex"))
            {
                foreach (var buff in selectedGeo.Mesh.OriginalMesh.VertexBuffers)
                {
                    buff.Write(file);
                }

                foreach (var ind in selectedGeo.Mesh.OriginalMesh.Indices)
                {
                    file.WriteUShort(ind, true);
                }
            }
        }

        public List<string> RebuildMaterialVertex()
        {
            if (Controller.SelectedGeometry == null) return null;

            var selectedGeo = Controller.SelectedGeometry;

            selectedGeo.Material.Rebuild(selectedGeo.Mesh, out List<string> problems);

            selectedGeo.UpdateCompatibility();

            return problems;
        }
    }
}
