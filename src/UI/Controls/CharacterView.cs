using Avalonia.Controls;
using Diorama.Editor;
using Diorama.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Diorama.UI.Controls
{
    /// <summary>
    /// A 3D view of one character, for hosting in another app's window (Flux's character page): Diorama's viewport
    /// with a scene controller of its own and no Diorama window. Orbit with the right mouse button, as in Diorama;
    /// a left click picks a part (<see cref="PartPicked"/>), and <see cref="Highlight"/> outlines one.
    /// </summary>
    public class CharacterView : ContentControl
    {
        private readonly ViewportNewControl viewport;
        private readonly SceneController controller;
        private bool highlighting;

        /// <summary>
        /// A part was clicked: the name of the scene it's in (the character's body has the character's name, a part its
        /// own, as in the look) and its material's (DX11_BODY_FRONT_GAME…). UI thread.
        /// </summary>
        public event Action<string, string>? PartPicked;

        public CharacterView()
        {
            if (AppSettings.Settings == null) AppSettings.Initialize();
            if (RenderService.Current == null) _ = new RenderService();
            var renderer = new ViewportRenderer();
            controller = new SceneController(renderer, null!);
            renderer.Controller = controller;
            viewport = new ViewportNewControl(controller);
            Content = viewport;
            controller.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SceneController.SelectedHierarchyObject) && !highlighting
                    && controller.SelectedHierarchyObject is EditorGeometryObject geo && geo.Parent?.SceneOwner is { } scene)
                    PartPicked?.Invoke(scene.Name, geo.Material?.Name ?? "");
            };
        }

        /// <summary>Shows the character a look (Flux's CharacterLook as JSON) describes; <paramref name="done"/> gets its problems, on the render thread.</summary>
        public void Show(string lookJson, Action<List<string>>? done = null) =>
            viewport.ShowCharacter(CharacterAssembler.FromJson(lookJson), done);

        /// <summary>
        /// Outlines the largest shown piece whose scene and material names <paramref name="match"/> accepts (the torso:
        /// the body's BODY_FRONT), or nothing when none does or <paramref name="match"/> is null. UI thread.
        /// </summary>
        public void Highlight(Func<string, string, bool>? match)
        {
            EditorGeometryObject? best = null;
            if (match != null)
                foreach (var scene in controller.Scenes.ToList())
                    foreach (var obj in scene.Objects.OfType<EditorSceneObject>().Where(scene.IsShown))
                        foreach (var geo in (obj.ClipObject ?? obj.Lods?.FirstOrDefault()?.ClipObject)?.Elements ?? [])
                            if (match(scene.Name, geo.Material?.Name ?? "") && (best == null || (geo.Mesh?.VerticesCount ?? 0) > (best.Mesh?.VerticesCount ?? 0)))
                                best = geo;
            highlighting = true;
            try { controller.SelectedHierarchyObject = best; }
            finally { highlighting = false; }
        }
    }
}
