using Avalonia.Input;
using Avalonia.Threading;
using Diorama.Core;
using Diorama.Core.Filetypes.GSC;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Editor;
using Diorama.Editor.glTF;
using Diorama.UI;
using Diorama.UI.ViewModels;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Diorama.Rendering
{
    public class SceneController : INotifyPropertyChanged
    {
        private MainWindow MainWindow;

        public ObservableCollection<EditorScene> Scenes { get; } = new();
        public IRenderer Renderer;

        public EditorSceneObject? SelectedSceneObject =>
            SelectedHierarchyObject switch
            {
                EditorSceneObject obj => obj,
                EditorGeometryObject geo => geo.Parent.Parent,
                _ => null
            };

        public EditorGeometryObject? SelectedGeometry =>
            SelectedHierarchyObject as EditorGeometryObject;

        public ObservableCollection<RenderTexture> SelectedTextures => SelectedGeometry?.Parent.SceneOwner.Textures;

        private IHierarchySelectable? selectedHierarchyObject;
        public IHierarchySelectable? SelectedHierarchyObject
        {
            get => selectedHierarchyObject;
            set
            {
                if (selectedHierarchyObject == value)
                    return;

                selectedHierarchyObject = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSceneObject));
                OnPropertyChanged(nameof(SelectedGeometry));
                OnPropertyChanged(nameof(SelectedTextures));
            }
        }
        public ICommand SafeRestructure { get; }
        public ICommand SaveSceneCommand { get; }
        public ICommand RemoveSceneCommand { get; }
        public ICommand ExportSceneCommand { get; }
        public ICommand ExportPartsObjCommand { get; }
        public ICommand ReplacePartsObjCommand { get; }
        public ICommand UndoMeshReplaceCommand { get; }
        public ICommand SaveSceneAsCommand { get; }

        /// <summary>
        /// Writes the scene to its own path, or to <paramref name="saveAs"/> (which it then belongs to). The first time a
        /// file that already exists is overwritten its original is kept as .bak. Saving somewhere new also copies the scene's
        /// companions (X_DX11.NXG_TEXTURES, .GSC.RES, shaders) under the new name, so textures and the rest stay with it.
        /// </summary>
        private void SaveScene(EditorScene scene, string? saveAs)
        {
            var notes = new List<string>();
            string oldPath = scene.OriginalScene.Path;
            try
            {
                if (saveAs != null)
                {
                    scene.OriginalScene.Path = saveAs;
                    // the textures are written from memory below (edits included), so they aren't copied
                    notes.AddRange(CopyCompanions(oldPath, saveAs, skip: scene.OriginalTextures != null ? ".NXG_TEXTURES" : null));
                }
                string path = scene.OriginalScene.Path;
                if (File.Exists(path) && !File.Exists(path + ".bak"))
                {
                    File.Copy(path, path + ".bak");
                    notes.Add($"The file as it was before is kept as {Path.GetFileName(path)}.bak (only the first save makes one).");
                }

                GSceneConverter.Write(scene);
                notes.Insert(0, $"Saved {path}.");
                if (saveAs != null && scene.OriginalTextures != null)
                {
                    var textureNotes = new List<string>();
                    SaveTextures(scene, Path.Combine(Path.GetDirectoryName(saveAs) ?? "", Path.GetFileNameWithoutExtension(saveAs) + ".NXG_TEXTURES"), textureNotes);
                    notes.AddRange(textureNotes);
                }
                else if (TexturesChanged(scene))
                    notes.Add("Changed textures are saved separately: Save Textures in the scene's right-click menu.");
                ShowMessageDialog("Scene saved", notes.Select(n => "• " + n));
            }
            catch (Exception ex)
            {
                if (saveAs != null) scene.OriginalScene.Path = oldPath;
                ShowMessageDialog("Could not save the scene", [ex.Message]);
            }
        }

        /// <summary>Whether textures were replaced, added, removed or reordered since they were read or last saved.</summary>
        private static bool TexturesChanged(EditorScene scene) =>
            scene.OriginalTextures?.TextureSet?.Textures is { } saved
            && (saved.Length != scene.Textures.Count || scene.Textures.Where((t, i) => !ReferenceEquals(t.Original, saved[i])).Any());

        /// <summary>Writes the scene's textures, as they are now, to <paramref name="path"/> (an .NXG_TEXTURES), keeping a .bak of the first overwrite.</summary>
        private static void SaveTextures(EditorScene scene, string path, List<string> notes)
        {
            var nxg = scene.OriginalTextures;
            var empty = scene.Textures.Select((t, i) => (t, i)).Where(x => !string.IsNullOrEmpty(x.t.Original?.Header?.Name) && x.t.Original.ImageHeader == null).ToList();
            if (empty.Count > 0)
                throw new InvalidOperationException($"Texture {string.Join(", ", empty.Select(x => x.i))} has no image yet. Replace it with a .DDS (click its preview in Edit Textures) or remove it, then save again.");

            // built in memory first, so a failure halfway doesn't leave a broken file behind
            byte[] bytes = nxg.ToBytes(scene.Textures.Select(t => t.Original));

            if (File.Exists(path) && !File.Exists(path + ".bak"))
            {
                File.Copy(path, path + ".bak");
                notes.Add($"The texture file as it was before is kept as {Path.GetFileName(path)}.bak (only the first save makes one).");
            }

            File.WriteAllBytes(path, bytes);
            nxg.Path = path;
            notes.Insert(0, $"Saved {scene.Textures.Count} textures to {path}.");
        }

        /// <summary>
        /// A check for the editor's save path (which needs OpenGL, so the tests can't run it): opens each scene listed in
        /// <paramref name="listPath"/> (one path per line), saves it through the editor to listPath.out.bin, and writes
        /// SAME / DIFF / FAIL per scene to listPath.results, and what opening each would have reported to listPath.notes,
        /// then exits. Run as Diorama.exe --save-sweep list.txt.
        /// </summary>
        public void RunSaveSweep(string listPath)
        {
            var results = new List<string>();
            var loadNotes = new List<string>();
            string outFile = listPath + ".out.bin";
            foreach (var file in File.ReadAllLines(listPath).Where(l => l.Trim().Length > 0))
            {
                try
                {
                    var scene = GSceneConverter.FromGScene(file, out var problems);
                    if (problems?.Count > 0)
                        loadNotes.Add(file + " :: " + string.Join(" | ", problems));
                    scene.OriginalScene.Path = outFile;
                    GSceneConverter.Write(scene);
                    bool same = File.ReadAllBytes(outFile).AsSpan().SequenceEqual(File.ReadAllBytes(file));
                    results.Add((same ? "SAME " : "DIFF ") + file);
                }
                catch (Exception ex)
                {
                    results.Add("FAIL " + file + " " + ex.ToString().Replace(Environment.NewLine, " | "));
                }
            }
            File.WriteAllLines(listPath + ".results", results);
            File.WriteAllLines(listPath + ".notes", loadNotes);
            Environment.Exit(0);
        }

        /// <summary>Copies X_DX11.NXG_TEXTURES, X_DX11.GSC.RES and the like from beside <paramref name="from"/> to beside <paramref name="to"/>, renamed, unless they're there already.</summary>
        private static IEnumerable<string> CopyCompanions(string from, string to, string? skip = null)
        {
            string? fromDir = Path.GetDirectoryName(from), toDir = Path.GetDirectoryName(to);
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toDir) || from.StartsWith("dat:") || !Directory.Exists(fromDir))
                yield break;
            string fromStem = Path.GetFileNameWithoutExtension(from), toStem = Path.GetFileNameWithoutExtension(to);
            foreach (var file in Directory.EnumerateFiles(fromDir, fromStem + ".*"))
            {
                string rest = Path.GetFileName(file)[fromStem.Length..]; // ".NXG_TEXTURES", ".GSC.RES"...
                if (rest.Equals(Path.GetExtension(from), StringComparison.OrdinalIgnoreCase) || rest.Equals(skip, StringComparison.OrdinalIgnoreCase) || rest.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = Path.Combine(toDir, toStem + rest);
                if (File.Exists(target) || string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, target);
                yield return $"Copied {Path.GetFileName(file)} beside it as {Path.GetFileName(target)}.";
            }
        }

        /// <summary>Takes back the scene's last mesh replacement (single part or a parts OBJ), on the render thread.</summary>
        public void UndoMeshReplace(EditorScene scene)
        {
            RenderService.Current.Enqueue(() =>
            {
                if (scene.MeshUndo.Count == 0)
                {
                    ShowMessageDialog("Nothing to undo", ["No mesh in this scene has been replaced since it was opened."]);
                    return;
                }
                var snapshot = scene.MeshUndo.Pop();
                snapshot.Restore();
                ShowMessageDialog("Mesh replacement undone", [$"Undid: {snapshot.Label}.",
                    scene.MeshUndo.Count > 0 ? $"{scene.MeshUndo.Count} earlier replacement(s) can still be undone." : "The meshes are back as they were when the scene was opened (or last saved)."]);
            });
        }
        public ICommand EditResourceHeaderCommand { get; }
        public ICommand EditTexturesCommand { get; }
        public ICommand SaveTexturesCommand { get; }

        public CameraController CameraController { get; }
        public Camera Camera { get; }

        public SceneController(IRenderer renderer, MainWindow window)
        {
            Renderer = renderer;
            MainWindow = window;

            Camera = new Camera(Vector3.Zero);
            CameraController = new CameraController(Camera);

            // the LOD picker and Breakup toggle only show while a character is open
            Scenes.CollectionChanged += (_, _) => RenderOptions.AnyCharacter = Scenes.Any(s =>
                s.CharacterLodCount > 1 || s.SpecialObjects.OfType<EditorSpecialObject>().Any(o => o.IsBreakup));

            SafeRestructure = new RelayCommand<IHierarchySelectable>(async (IHierarchySelectable? sender) =>
            {
                SelectedHierarchyObject = null;
            });

            SaveSceneCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                string path = sender.OriginalScene.Path;

                bool hasPath = !string.IsNullOrEmpty(path);
                string extension = hasPath ? Path.GetExtension(path) : "gsc";

                if (!hasPath || path.StartsWith("dat:"))
                {
                    string outputPath = await MainWindow?.OpenSaveMenu("Save GScene", extension);

                    if (outputPath == null) return;

                    sender.OriginalScene.Path = outputPath;
                }

                SaveScene(sender, null);
            });

            SaveSceneAsCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender == null) return;
                string path = sender.OriginalScene.Path;
                string extension = !string.IsNullOrEmpty(path) ? Path.GetExtension(path) : "gsc";
                string? outputPath = await MainWindow?.OpenSaveMenu("Save GScene As", extension);
                if (outputPath == null) return;
                SaveScene(sender, outputPath);
            });

            RemoveSceneCommand = new RelayCommand<EditorScene>((EditorScene? sender) =>
            {
                if (sender != null)
                {
                    Scenes.Remove(sender);
                }
            });

            ExportSceneCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                string outputPath = await MainWindow?.OpenSaveMenu("Export glTF Scene", "glTF");

                if (outputPath == null) return;

                List<EditorGeometryObject> geometries = new();

                foreach (EditorSceneObject sceneObject in sender.Objects)
                {
                    if (sceneObject.ClipObject != null)
                    {
                        geometries.AddRange(sceneObject.ClipObject.Elements);
                    }
                    else if (sceneObject.UseLodGroups)
                    {
                        if (sceneObject.Lods[0]?.ClipObject != null)
                        {
                            geometries.AddRange(sceneObject.Lods[0].ClipObject.Elements);
                        }
                    }
                }

                glTFConverter.WriteObjectsToGltf(geometries, outputPath);
            });

            ExportPartsObjCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender == null) return;

                string? outputPath = await MainWindow?.OpenSaveMenu("Export Parts as OBJ", "obj");
                if (outputPath == null) return;

                try
                {
                    int count = OBJConverter.ExportParts(sender, outputPath);
                    ShowMessageDialog("Parts exported", [
                        $"Wrote {count} parts to {Path.GetFileName(outputPath)}, with an .MTL and the textures beside it.",
                        "Each part is a separate object in Blender. Keep the \"__m\" number at the end of each name: it is how Replace Parts from OBJ finds the part again.",
                        "When exporting from Blender, keep Y up / -Z forward and tick UV Coordinates, Normals and Colors."]);
                }
                catch (Exception ex)
                {
                    ShowMessageDialog("Could not export the parts", [ex.Message]);
                }
            });

            ReplacePartsObjCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender == null) return;

                string? inputPath = await MainWindow?.OpenFileMenu("Replace Parts from OBJ", "obj");
                if (inputPath == null) return;

                RenderService.Current.Enqueue(() =>
                {
                    var snapshot = MeshSnapshot.Take(sender, $"Replace parts from {Path.GetFileName(inputPath)}");
                    try
                    {
                        var notes = OBJConverter.ImportParts(sender, inputPath);
                        if (notes.Count > 0 && notes[0].StartsWith("Replaced"))
                        {
                            sender.MeshUndo.Push(snapshot);
                            notes.Add("Not what you wanted? Undo Mesh Replacement in the scene's right-click menu puts the old parts back.");
                        }
                        ShowMessageDialog("Parts replaced", notes.Select(n => "• " + n));
                    }
                    catch (Exception ex)
                    {
                        snapshot.Restore();
                        ShowMessageDialog("Could not import the parts", [ex.Message]);
                    }
                });
            });

            UndoMeshReplaceCommand = new RelayCommand<EditorScene>((EditorScene? sender) =>
            {
                if (sender != null)
                    UndoMeshReplace(sender);
            });

            EditResourceHeaderCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender != null)
                {
                    ResourceHeaderViewModel headerVm = new ResourceHeaderViewModel(sender.Metadata);

                    var modal = new EditResourceHeaderWindow(headerVm);

                    await modal.ShowDialog(MainWindow);
                }
            });

            EditTexturesCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender != null)
                {
                    EditTexturesViewModel editTexturesVm = new EditTexturesViewModel(sender.Textures);

                    var modal = new EditTexturesWindow(editTexturesVm);

                    await modal.ShowDialog(MainWindow);
                }
            });

            SaveTexturesCommand = new RelayCommand<EditorScene>(async (EditorScene? sender) =>
            {
                if (sender?.OriginalTextures == null) return;

                string path = sender.OriginalTextures.Path;
                if (string.IsNullOrEmpty(path) || path.StartsWith("dat:"))
                {
                    path = await MainWindow?.OpenSaveMenu("Save Nxg_Textures", "nxg_textures");
                    if (path == null) return;
                }

                var notes = new List<string>();
                try
                {
                    SaveTextures(sender, path, notes);
                    ShowMessageDialog("Textures saved", notes.Select(n => "• " + n));
                }
                catch (Exception ex)
                {
                    ShowMessageDialog("Could not save the textures", [ex.Message]);
                }
            });
        }

        public void Initialize()
        {
        }

        private readonly Queue<Action> glQueue = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(propertyName));
        }

        public void EnqueueGL(Action action)
        {
            lock (glQueue)
            {
                glQueue.Enqueue(action);
            }
        }

        private void ExecuteGLQueue()
        {
            while (true)
            {
                Action action;

                lock (glQueue)
                {
                    if (glQueue.Count == 0)
                        return;

                    action = glQueue.Dequeue();
                }

                action();
            }
        }

        public void ShowMessageDialog(string title, IEnumerable<string> messages)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                MessageWindow problemModal = new MessageWindow(title, (IEnumerable<string>)messages);

                await problemModal.ShowDialog(MainWindow);
            });
        }

        private void ShowSceneLoadProblems(List<string> problems)
        {
            if (problems == null || problems.Count == 0) return;

            Dispatcher.UIThread.Post(async () =>
            {
                MessageWindow problemModal = new MessageWindow("Problems when opening file!", (IEnumerable<string>)problems);

                await problemModal.ShowDialog(MainWindow);
            });
        }

        public void AddScene(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            if (ext == ".gsc" || ext == ".ghg")
            {
                EditorScene scene = GSceneConverter.FromGScene(path, out List<string> problems);
                Scenes.Add(scene);
                CameraController.FrameScene(scene);
                ShowSceneLoadProblems(problems);
            }
        }

        public void AddScene(GScene gscene, NxgTextures nxg_textures, NxgTextures? cubemap_textures)
        {
            EditorScene scene = GSceneConverter.FromGScene(gscene, nxg_textures, cubemap_textures, out List<string> problems);
            Scenes.Add(scene);
            CameraController.FrameScene(scene);
            ShowSceneLoadProblems(problems);
        }

        public void Render()
        {
            //ExecuteGLQueue();

            //Renderer.Render(Scenes.ToList(), Camera);
        }

        public void OnClick(int x, int y)
        {
            ((ViewportRenderer)Renderer).Pick(x, y, (obj) =>
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    SelectedHierarchyObject = obj;
                });
            });
        }

        public void SetWidthHeight(int width, int height)
        {
            //EnqueueGL(() =>
            //{
            //    GL.Viewport(0, 0, width, height);
            //    //Renderer.SetFramebufferSize(width, height);
            //});

            Camera.SetProjection(width, height);
        }
    }
}
