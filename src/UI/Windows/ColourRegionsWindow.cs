using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Editor;
using Diorama.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Diorama.UI.Windows
{
    /// <summary>
    /// Colour by region: one brick colour per region of a part (the areas sharing a colour, or each separate piece),
    /// previewed on the model as they're picked. Closing without Apply puts the part back.
    /// </summary>
    public class ColourRegionsWindow : ModalWindow
    {
        readonly SceneController controller;
        readonly EditorScene scene;
        readonly EditorGeometryObject geo;
        readonly NuRenderMesh mesh;
        readonly Vertex[] vertices;
        readonly ushort[] indices;
        readonly MeshSnapshot snapshot;
        bool applied;

        int[] region = [];
        List<ColourRegion> regions = new();
        Vector3[] chosen = [];
        int highlighted = -1;

        readonly ListBox list = new() { MaxHeight = 380, Width = 460 };
        readonly RadioButton byColour = new() { Content = "the colours it has", GroupName = "split" };
        readonly RadioButton byPiece = new() { Content = "its separate pieces", GroupName = "split" };
        readonly CheckBox showPink = new() { Content = "Show the picked row's region in pink", IsChecked = true };
        readonly CheckBox white = new() { Content = "Give it a plain white texture, so only these colours show" };
        readonly TextBlock splitNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Width = 460 };

        Vector3?[]? pending; // the latest preview not yet painted
        bool splitting;

        public ColourRegionsWindow() : base("Colour by region") { }

        /// <param name="snapshot">Taken (on the render thread) before the window opened; restored if it closes without Apply.</param>
        /// <param name="intro">What led here, such as an import's colour regions; null for the inspector's button.</param>
        /// <param name="offerWhite">Tick the white texture box: the shape came from elsewhere, so its old texture doesn't fit it.</param>
        public ColourRegionsWindow(SceneController controller, EditorScene scene, EditorGeometryObject geo, Vertex[] vertices, ushort[] indices,
            MeshSnapshot snapshot, string? intro, bool offerWhite, RegionSplit split = RegionSplit.Auto) : this()
        {
            this.controller = controller;
            this.scene = scene;
            this.geo = geo;
            this.mesh = geo.Mesh.OriginalMesh;
            this.vertices = vertices;
            this.indices = indices;
            this.snapshot = snapshot;

            white.IsChecked = offerWhite;
            white.IsVisible = !ObjTexture.IsPlainWhite(geo.Material?.Diffuse0) && geo.Material?.Diffuse0 != null && !RenderTexture.IsWhitePlaceholder(geo.Material.Diffuse0);
            ToolTip.SetTip(white, "The part's texture is multiplied with these colours. A shape from another game or program doesn't fit the old texture's layout, so a plain white one leaves just the colours. Other parts using the same texture turn white too.");

            var apply = new Button { Content = "Apply", IsDefault = true };
            var cancel = new Button { Content = "Cancel" };
            apply.Click += (_, _) => Apply();
            cancel.Click += (_, _) => Close();

            byColour.IsCheckedChanged += (_, _) => { if (!splitting && byColour.IsChecked == true) Split(RegionSplit.Colours); };
            byPiece.IsCheckedChanged += (_, _) => { if (!splitting && byPiece.IsChecked == true) Split(RegionSplit.Pieces); };
            list.SelectionChanged += (_, _) => { highlighted = showPink.IsChecked == true ? list.SelectedIndex : -1; Preview(); };
            showPink.IsCheckedChanged += (_, _) => { highlighted = showPink.IsChecked == true ? list.SelectedIndex : -1; Preview(); };

            var panel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Width = 460,
                Text = (intro != null ? intro + "\n\n" : "") +
                    "Pick a colour for each region of " + geo.Name + ". Most LEGO parts take their brick colour from these vertex colours; the model updates as you pick. " +
                    "Click a row to see its region in pink.",
            });
            var splitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            splitRow.Children.Add(new TextBlock { Text = "Split by", VerticalAlignment = VerticalAlignment.Center });
            splitRow.Children.Add(byColour);
            splitRow.Children.Add(byPiece);
            panel.Children.Add(splitRow);
            panel.Children.Add(splitNote);
            panel.Children.Add(list);
            panel.Children.Add(showPink);
            panel.Children.Add(white);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(apply);
            panel.Children.Add(buttons);
            Content = panel;

            Split(split);
        }

        void Split(RegionSplit split)
        {
            region = VertexColours.FindRegions(vertices, indices, split, out regions, out var used);
            chosen = regions.Select(r => r.Colour).ToArray();
            highlighted = -1;

            // set the radio buttons without coming back here
            splitting = true;
            byColour.IsChecked = used == RegionSplit.Colours;
            byPiece.IsChecked = used == RegionSplit.Pieces;
            splitting = false;

            int distinct = regions.Count;
            splitNote.Text = used == RegionSplit.Colours
                ? $"{distinct} colour{(distinct == 1 ? "" : "s")}: each area that shares a colour is one row."
                : regions.Count == 1
                    ? "It's all one piece. Split by colour instead if it has areas of different colours, or colour it in Blender."
                    : "Each row is a separate piece of geometry (not joined to the rest).";

            list.Items.Clear();
            for (int r = 0; r < regions.Count; r++)
                list.Items.Add(Row(r));
            Preview();
        }

        Control Row(int r)
        {
            var picker = new ColorPicker { Color = ToColor(chosen[r]), Palette = new LegoPalette(), Width = 70 };
            picker.ColorChanged += (_, e) =>
            {
                chosen[r] = new Vector3(e.NewColor.R / 255f, e.NewColor.G / 255f, e.NewColor.B / 255f);
                Preview();
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var text = new TextBlock
            {
                Text = $"{regions[r].Label}  ({regions[r].Vertices:N0} points)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(picker);
            grid.Children.Add(text);
            return grid;
        }

        static Color ToColor(Vector3 c) => Color.FromRgb((byte)MathF.Round(c.X * 255), (byte)MathF.Round(c.Y * 255), (byte)MathF.Round(c.Z * 255));

        Vector3?[] VertexColoursNow(bool pink)
        {
            var result = new Vector3?[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                result[i] = pink && region[i] == highlighted ? new Vector3(1, 0, 1) : chosen[region[i]];
            return result;
        }

        /// <summary>Repaints the part on the render thread, at most one repaint waiting (picking drags fire many changes).</summary>
        void Preview()
        {
            if (applied) return;
            bool queue = System.Threading.Interlocked.Exchange(ref pending, VertexColoursNow(true)) == null;
            if (queue)
                RenderService.Current.Enqueue(() =>
                {
                    if (System.Threading.Interlocked.Exchange(ref pending, null) is { } colours)
                        Paint(colours);
                });
        }

        void Paint(Vector3?[] colours)
        {
            var all = scene.OriginalScene.MeshSceneBlock.Meshes;
            if (!VertexColours.Paint(mesh, all, colours))
                return;
            var rendered = OBJConverter.BuildRenderMesh(mesh, scene);
            foreach (var g in scene.AllGeometry().Where(g => g.Mesh?.OriginalMesh == mesh))
                g.Mesh = rendered;
        }

        void Apply()
        {
            applied = true;
            System.Threading.Interlocked.Exchange(ref pending, null);
            var colours = VertexColoursNow(false);
            bool makeWhite = white.IsVisible && white.IsChecked == true;
            var listed = regions.Select((r, i) => $"{r.Label}: #{VertexColours.Hex(chosen[i])}").Take(12).ToList();
            RenderService.Current.Enqueue(() =>
            {
                var notes = new List<string>();
                if (!VertexColours.Paint(mesh, scene.OriginalScene.MeshSceneBlock.Meshes, colours))
                {
                    snapshot.Restore();
                    controller.ShowMessageDialog("Couldn't colour the part", ["This part's vertices keep no colours (its vertex layout has no colour), so it gets its colour from its texture or material instead."]);
                    return;
                }
                var rendered = OBJConverter.BuildRenderMesh(mesh, scene);
                foreach (var g in scene.AllGeometry().Where(g => g.Mesh?.OriginalMesh == mesh))
                    g.Mesh = rendered;
                notes.Add($"Coloured {geo.Name}: " + string.Join(", ", listed) + (regions.Count > listed.Count ? ", ..." : "") + ".");
                if (makeWhite)
                    ObjTexture.MakeWhite(geo, scene, notes, "its colours are set by region");
                scene.MeshUndo.Push(snapshot);
                notes.Add("How the game tints it isn't checked here: some parts are recoloured by the character's .CD colours. Check it in game.");
                notes.Add("Not right? Undo replace puts the old colours back.");
                notes.Add(OBJConverter.SaveReminder);
                controller.ShowMessageDialog("Colours applied", notes.Select(n => "• " + n));
            });
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (!applied)
                RenderService.Current.Enqueue(snapshot.Restore);
        }

        /// <summary>Common LEGO colours, for the picker's palette tab.</summary>
        sealed class LegoPalette : IColorPalette
        {
            static readonly uint[] Colours =
            [
                0xF4F4F4, 0xA0A5A9, 0x6C6E68, 0x1B2A34, 0x898788, 0xAA7F2E, 0x582A12, 0xE4CD9E, 0x958A73,
                0xC91A09, 0x720E0F, 0xFE8A18, 0xF2CD37, 0x4B9F4A, 0x184632, 0x0055BF, 0x0A3463, 0x81007B,
            ];
            public int ColorCount => 9;
            public int ShadeCount => 2;
            public Color GetColor(int colorIndex, int shadeIndex) => Color.FromUInt32(0xFF000000 | Colours[shadeIndex * 9 + colorIndex]);
        }
    }
}
