using Diorama.Core;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor.Attributes;
using Diorama.Rendering;
using Diorama.Rendering.Shaders;
using Diorama.UI.Controls;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;
using System.Text;
using static OpenTK.Graphics.OpenGL.GL;

namespace Diorama.Editor
{
    public class EditorPointOfInterest : EditableItem, INamedItem, IHierarchySelectable, IRenderable
    {
        public NuPointOfInterest Original;

        public EditorScene ParentScene { get; }

        public EditorPointOfInterest(NuPointOfInterest original, EditorScene parentScene)
        {
            Original = original;
            ParentScene = parentScene;
        }

        [DisplayLabel("Name")]
        public string Name { get => Original.Name; set { Set(ref Original.Name, value); OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(Purpose)); } }

        /// <summary>What a point with this name is for, read from the name (the locators every character has); empty if unknown.</summary>
        public string Purpose => Describe(Name);

        private static string Describe(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string key = name[(name.LastIndexOf(':') + 1)..]; // Super_Bigfig_Skeleton:Hat_Locator
            foreach (string suffix in new[] { "_Locator", "_Loc" })
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) key = key[..^suffix.Length];
            string? what = key.ToLowerInvariant() switch
            {
                "hat" => "where hats and helmets sit",
                "hair" => "where hair pieces sit",
                "glasses" => "where glasses sit",
                "righthand" or "lefthand" => "where items held in that hand go",
                "rightwrist" or "leftwrist" or "rightforearm" or "leftforearm" => "an attachment spot on that arm",
                "rightsheath" or "leftsheath" => "where a sheathed weapon is carried on that side",
                "backpack" => "where things worn on the back go",
                "breath" => "where breath effects come out",
                "head" => "the head's position",
                "face" => "the face's position",
                "lookat" => "the spot looked at when something looks at this character",
                "impact1" or "impact2" => "where hit effects appear",
                "hip" => "the hips' position",
                "rightfoot" or "leftfoot" or "rightleg" or "leftleg" => "a spot on that leg or foot",
                "shoulderl" or "shoulderr" => "a spot on that shoulder",
                "attachedcharacter" => "where another character is attached",
                "attachment" => "a general attachment spot",
                _ => null
            };
            return what == null ? "" : $"From its name: {what}. Move it to change where that goes on this character.";
        }

        public string DisplayName { get => Name; }

        [DisplayLabel("Offset from Parent Joint")]
        public Matrix4 Transform { 
            get => Original.Offset.mtx.ToMatrix4(); 
            set => Set(ref Original.Offset.mtx, value.ToArray()); 
        }

        public EditorJoint Parent { get; set; }

        public void Draw(Shader shader, RenderMesh mesh)
        {
            shader.SetMatrix4("model", Matrix4.CreateScale(0.001f) * Transform * (Parent?.WorldTransform ?? Matrix4.Identity));

            mesh.Draw();
        }

        public void Draw(Shader shader, RenderContext ctx)
        {
            Matrix4 parentTransform = Matrix4.Identity;
            if (Parent != null)
                parentTransform = Parent.WorldTransform;

            shader.SetMatrix4("model", Matrix4.CreateScale(0.005f * Vector3.Distance(Transform.ExtractTranslation(), ctx.CameraScenePosition)) * Transform * parentTransform);

            MeshFactory.GetIcosahedron().Draw();
        }

        public IEnumerable<IHierarchySelectable> Children => Enumerable.Empty<IHierarchySelectable>();
    }
}
