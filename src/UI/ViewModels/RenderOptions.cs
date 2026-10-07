using Diorama.Editor;
using Diorama.Editor.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Diorama.UI.ViewModels
{
    public class RenderOptions : EditableItem
    {
        [DisplayLabel("Only Color 0")]
        public static bool Color0 { get; set; } = false;

        [DisplayLabel("Only Color 0 R")]
        public static bool Color0R { get; set; } = false;

        [DisplayLabel("Only Color 0 G")]
        public static bool Color0G { get; set; } = false;

        [DisplayLabel("Only Color 0 B")]
        public static bool Color0B { get; set; } = false;

        [DisplayLabel("Only Color 0 A")]
        public static bool Color0A { get; set; } = false;

        [DisplayLabel("Only Color 1")]
        public static bool Color1 { get; set; } = false;

        [DisplayLabel("Only Color 1 R")]
        public static bool Color1R { get; set; } = false;

        [DisplayLabel("Only Color 1 G")]
        public static bool Color1G { get; set; } = false;

        [DisplayLabel("Only Color 1 B")]
        public static bool Color1B { get; set; } = false;

        [DisplayLabel("Only Color 1 A")]
        public static bool Color1A { get; set; } = false;

        [DisplayLabel("Show Specular")]
        public static bool ShowSpecular { get; set; } = true;

        [DisplayLabel("Show Env Map")]
        public static bool ShowEnvMap { get; set; } = true;

        public static bool ShowPoIs { get; set; } = false;

        // Characters carry up to four LODs: 0 is the cutscene model, 1-3 are in-game from most to least detailed
        public static int CharacterLod { get; set; } = 0;

        public static bool ShowBreakup { get; set; } = false;

        // Unlit models are flat colour, which hides their shape: this darkens surfaces turned away from the viewer
        public static bool ShapeShading { get; set; } = true;

        private static bool anyCharacter;
        private static event Action? AnyCharacterChanged;

        /// <summary>Whether an open scene is a character with LODs, so the LOD picker and Breakup toggle mean something.</summary>
        public static bool AnyCharacter
        {
            get => anyCharacter;
            set
            {
                if (anyCharacter == value) return;
                anyCharacter = value;
                AnyCharacterChanged?.Invoke();
            }
        }

        /// <summary><see cref="AnyCharacter"/> for the toolbar's bindings, which need a change notification.</summary>
        public bool ShowCharacterOptions => anyCharacter;

        public RenderOptions()
        {
            AnyCharacterChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ShowCharacterOptions)));
        }
    }
}
