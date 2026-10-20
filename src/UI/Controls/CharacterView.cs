using Avalonia.Controls;
using Diorama.Editor;
using Diorama.Rendering;
using System;
using System.Collections.Generic;

namespace Diorama.UI.Controls
{
    /// <summary>
    /// A 3D view of one character, for hosting in another app's window (Flux's character page): Diorama's viewport
    /// with a scene controller of its own and no Diorama window. Orbit with the right mouse button, as in Diorama.
    /// </summary>
    public class CharacterView : ContentControl
    {
        private readonly ViewportNewControl viewport;

        public CharacterView()
        {
            if (AppSettings.Settings == null) AppSettings.Initialize();
            if (RenderService.Current == null) _ = new RenderService();
            var renderer = new ViewportRenderer();
            var controller = new SceneController(renderer, null!);
            renderer.Controller = controller;
            viewport = new ViewportNewControl(controller);
            Content = viewport;
        }

        /// <summary>Shows the character a look (Flux's CharacterLook as JSON) describes; <paramref name="done"/> gets its problems, on the render thread.</summary>
        public void Show(string lookJson, Action<List<string>>? done = null) =>
            viewport.ShowCharacter(CharacterAssembler.FromJson(lookJson), done);
    }
}
