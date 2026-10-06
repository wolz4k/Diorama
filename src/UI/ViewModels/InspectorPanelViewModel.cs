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
                try
                {
                    RenderMesh oldMesh = selectedGeo.Mesh;
                    RenderMesh newMesh;
                    List<string> notes = null;
                    if (isGLTF(path))
                    {
                        newMesh = glTFConverter.GetObjectsFromGltf(path, oldMesh, scene);
                    }
                    else
                    {
                        newMesh = OBJConverter.MeshFromOBJ(path, oldMesh, scene, out notes);
                    }

                    // the game mesh was changed in place, so every object drawing it gets the new one
                    int sharing = 0;
                    foreach (var geo in scene.AllGeometry())
                    {
                        if (geo.Mesh == oldMesh || geo.Mesh?.OriginalMesh == newMesh.OriginalMesh)
                        {
                            geo.Mesh = newMesh;
                            sharing++;
                        }
                    }
                    selectedGeo.Mesh = newMesh;

                    if (notes != null)
                    {
                        if (sharing > 1)
                            notes.Insert(1, $"{sharing} objects in this scene use this mesh, and all of them now show the new one.");
                        Controller.ShowMessageDialog("Mesh replaced", notes.Select(n => "• " + n));
                    }
                }
                catch (Exception ex)
                {
                    Controller.ShowMessageDialog("Could not import from file", [ex.Message]);
                }
            });
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
