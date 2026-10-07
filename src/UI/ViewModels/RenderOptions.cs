using Diorama.Editor;
using Diorama.Editor.Attributes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Diorama.UI.ViewModels
{
    public class RenderOptions : EditableItem
    {
        [DisplayLabel("Only Color 0", "Show only the meshes' first vertex colours: the brick colour on most LEGO parts")]
        public static bool Color0 { get; set; } = false;

        [DisplayLabel("Only Color 0 R", "Show only the red channel of the first vertex colours, as grey")]
        public static bool Color0R { get; set; } = false;

        [DisplayLabel("Only Color 0 G", "Show only the green channel of the first vertex colours, as grey")]
        public static bool Color0G { get; set; } = false;

        [DisplayLabel("Only Color 0 B", "Show only the blue channel of the first vertex colours, as grey")]
        public static bool Color0B { get; set; } = false;

        [DisplayLabel("Only Color 0 A", "Show only the alpha of the first vertex colours, as grey")]
        public static bool Color0A { get; set; } = false;

        [DisplayLabel("Only Color 1", "Show only the second vertex colours: on blended level surfaces, how much of each texture layer shows")]
        public static bool Color1 { get; set; } = false;

        [DisplayLabel("Only Color 1 R", "Show only the red channel of the second vertex colours, as grey")]
        public static bool Color1R { get; set; } = false;

        [DisplayLabel("Only Color 1 G", "Show only the green channel of the second vertex colours, as grey")]
        public static bool Color1G { get; set; } = false;

        [DisplayLabel("Only Color 1 B", "Show only the blue channel of the second vertex colours, as grey")]
        public static bool Color1B { get; set; } = false;

        [DisplayLabel("Only Color 1 A", "Show only the alpha of the second vertex colours, as grey")]
        public static bool Color1A { get; set; } = false;

        [DisplayLabel("Show Specular", "Add shiny highlights (needs View > Material Lighting)")]
        public static bool ShowSpecular { get; set; } = true;

        [DisplayLabel("Show Env Map", "Add reflections of the surroundings on reflective materials (needs View > Material Lighting)")]
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
