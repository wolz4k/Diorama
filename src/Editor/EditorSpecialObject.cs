using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor.Attributes;
using Diorama.UI.Controls;
using System;
using System.Collections.Generic;
using System.Text;

namespace Diorama.Editor
{
    public class EditorSpecialObject : EditableItem, INamedItem
    {
        public NuSpecialObject Original;

        public EditorSpecialObject(NuSpecialObject original)
        {
            Original = original;
        }

        [DisplayLabel("Special Object Name")]
        public string Name { get => Original.Name; set { Set(ref Original.Name, value); OnPropertyChanged(nameof(DisplayName)); } }

        public string DisplayName { get => Name; }

        public int LODGroup = -1;

        // The character layer it's on (TT5_Head, TT13_Hat…), -1 if none: the character preview shows the layers a .CD lists
        public int Layer = -1;

        // The joint a rigid (unskinned) character piece hangs on, -1 if none: its vertices are stored relative to it
        public int HangJoint = -1;

        // Part of the brick-by-brick copy a character falls apart into when smashed
        public bool IsBreakup;
    }
}
