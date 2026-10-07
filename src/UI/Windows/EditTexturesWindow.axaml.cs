using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Diorama.Core.Filetypes.GSC.Components;
using Diorama.Core.Filetypes.TEXTURES;
using Diorama.Rendering;
using Diorama.UI.ViewModels;
using Diorama.UI.Windows;

namespace Diorama;

public partial class EditTexturesWindow : ModalWindow
{
    public EditTexturesWindow() : base("Edit Textures")
    {
        InitializeComponent();
    }

    public EditTexturesWindow(EditTexturesViewModel viewModel) : this()
    {
        DataContext = viewModel;
        PART_MainTexture.OnClick += OnTextureButtonClick;
        AddNewTexture.Click += AddNewTexture_Click;
        RemoveTexture.Click += RemoveTexture_Click;
        ExportTexture.Click += ExportTexture_Click;
        ExportAllTextures.Click += ExportAllTextures_Click;
        RestoreTexture.Click += RestoreTexture_Click;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditTexturesViewModel.Texture))
                ShowFacts(viewModel.Texture);
        };
        ShowFacts(viewModel.Texture);
    }

    /// <summary>Size, compression and mipmaps of the picked texture: what a replacement .DDS should match.</summary>
    private void ShowFacts(RenderTexture? texture)
    {
        var nu = texture?.Pixels;
        if (nu == null || nu.Data == null || nu.Width == 0)
        {
            TextureFacts.Text = texture == null
                ? "Pick a texture on the left to see its size and format. Export DDS saves it to paint over; clicking the preview replaces it with your .DDS."
                : "No image data in this slot.";
            return;
        }

        string text = Describe(nu);
        if (string.IsNullOrEmpty(nu.Header?.Name))
            text += ". It comes from the shared LEGO texture page (LEGOTPAGE), so replacing it here isn't saved into this scene.";
        else if (texture!.SharedFrom != null)
            text += $". This scene only names it: the image is stored in {texture.SharedFrom}, which other parts of the level use too. Replacing it here saves a copy into this scene's own texture file (not yet tested in game).";
        TextureFacts.Text = text;
    }

    private static string FormatName(NuTexture nu) => nu.FourCC switch
    {
        0x31545844 => "DXT1 (BC1, no or 1-bit alpha)",
        0x33545844 => "DXT3 (BC2)",
        0x35545844 => "DXT5 (BC3, with alpha)",
        0x30315844 => nu.Dx10Format == 98 ? "BC7 (DX10)" : $"DX10 format {nu.Dx10Format}",
        _ => "uncompressed",
    };

    private static string Describe(NuTexture nu) =>
        $"{nu.Width} × {nu.Height}, {FormatName(nu)}, {nu.MipCount} mipmap level{(nu.MipCount == 1 ? "" : "s")}{(nu.IsCubemap ? ", cubemap" : "")}";

    /// <summary>What a modder should know about <paramref name="replacement"/> compared with the image it replaces.</summary>
    private static List<string> CompareReplacement(NuTexture old, NuTexture replacement)
    {
        var notes = new List<string>();
        if (old.Width > 0)
            notes.Add($"Replaced. It was {Describe(old)}.");
        if (replacement.MipCount <= 1 && old.MipCount > 1)
            notes.Add($"Yours has no mipmaps (the original had {old.MipCount}), so it may shimmer from a distance. Save the DDS with mipmaps if your editor offers it.");
        if (!System.Numerics.BitOperations.IsPow2(replacement.Width) || !System.Numerics.BitOperations.IsPow2(replacement.Height))
            notes.Add("Its size isn't a power of two (256, 512, 1024...), which the game's own textures always are.");
        if (old.FourCC == 0x35545844 && replacement.FourCC == 0x31545844)
            notes.Add("The original is DXT5, which has an alpha channel; yours is DXT1, so any transparency is lost.");
        if (old.Width > 0 && (replacement.Width != old.Width || replacement.Height != old.Height))
            notes.Add("Its size differs from the original's. That's fine if it's the same layout (UVs are 0-1), just bigger or smaller.");
        notes.Add("Save Textures (scene right-click menu) to keep it.");
        return notes;
    }

    private async void ExportTexture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel vm || vm.Texture?.Pixels is not { ImageHeader: not null, Data: not null } nu)
            return;

        string name = Path.GetFileName((nu.Header?.Name is { Length: > 0 } n ? n : "texture").Replace('/', '\\'));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Texture",
            SuggestedFileName = Path.ChangeExtension(name, ".dds"),
            DefaultExtension = "dds",
            FileTypeChoices = [new FilePickerFileType("DDS image") { Patterns = ["*.dds"] }],
        });
        string? path = file?.TryGetLocalPath();
        if (path == null)
            return;

        // the texture is stored as a whole DDS file: its header, then the pixels of every mipmap
        using var output = File.Create(path);
        output.Write(nu.ImageHeader);
        output.Write(nu.Data);
    }

    private void RestoreTexture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel { Texture: { } tex })
            return;
        if (!tex.IsReplaced)
        {
            TextureFacts.Text += Environment.NewLine + "This texture hasn't been replaced, so there's nothing to restore.";
            return;
        }

        int slot = TexturePicker.GetSlot(tex);
        TexturePicker.SetSlot(slot, RenderTexture.GetWhiteTexture()); // trigger re-draw
        RenderService.Current.Enqueue(() =>
        {
            tex.RestoreAsRead();
            TexturePicker.SetSlot(slot, tex);
            PART_MainTexture.Reload();
            Dispatcher.UIThread.Post(() =>
            {
                ShowFacts(tex);
                TextureFacts.Text += Environment.NewLine + "Restored the image the scene was opened with.";
            });
        });
    }

    private async void ExportAllTextures_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export all textures to…" });
        string? folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder == null)
            return;

        try
        {
            int written = ExportAll(vm.Textures, folder);
            TextureFacts.Text = $"Exported {written} texture{(written == 1 ? "" : "s")} to {folder}. To put one back, pick it here and click the preview.";
        }
        catch (Exception ex)
        {
            TextureFacts.Text = $"Export stopped: {ex.Message}";
        }
    }

    /// <summary>Writes each texture worth painting over to <paramref name="folder"/> as name.dds; returns how many.</summary>
    public static int ExportAll(IEnumerable<RenderTexture> textures, string folder)
    {
        int written = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            // lightmaps (named "Lightmap") and slots with no image aren't worth painting over
            if (texture.Pixels is not { ImageHeader: not null, Data: not null, Width: > 0 } nu || texture.ShortName is "" or "Lightmap")
                continue;

            string name = string.Concat(texture.ShortName.Split(Path.GetInvalidFileNameChars()));
            string file = name;
            for (int n = 2; !used.Add(file); n++) // two textures with one name get _2, _3...
                file = $"{name}_{n}";

            // a whole DDS file: its header, then the pixels of every mipmap
            using var output = File.Create(Path.Combine(folder, file + ".dds"));
            output.Write(nu.ImageHeader);
            output.Write(nu.Data);
            written++;
        }
        return written;
    }

    private async void OnTextureButtonClick()
    {
        if (StorageProvider == null)
            throw new Exception("Unable to access filesystem");

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Replace DDS Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DDS files") { Patterns = new[] { "*.DDS" } }
            }
        });

        if (files.Count > 0 && DataContext is EditTexturesViewModel { Texture: not null } vm)
        {
            string filePath = files[0].Path.LocalPath;

            var tex = vm.Texture;
            var old = tex.Pixels;
            uint oldLevel = tex.Original.Header.Level;

            NuTexture texture;
            try
            {
                texture = NuTexture.Load(filePath, tex.Original.Header);
                // what the viewer can draw, and what the game's own textures use
                if (texture.FourCC is not (0x31545844 or 0x33545844 or 0x35545844) && !(texture.FourCC == 0x30315844 && texture.Dx10Format == 98))
                    throw new InvalidDataException($"This DDS is {FormatName(texture)}. Save it as DXT1 (BC1, no transparency) or DXT5 (BC3, with transparency), which is what the game's textures use.");
                if (texture.Width == 0 || texture.Height == 0 || texture.Data == null)
                    throw new InvalidDataException("This DDS has no image in it.");
            }
            catch (Exception ex)
            {
                tex.Original.Header.Level = oldLevel;
                TextureFacts.Text = "Not replaced: " + ex.Message;
                return;
            }
            var comparison = CompareReplacement(old, texture);

            int slot = TexturePicker.GetSlot(vm.Texture);

            TexturePicker.SetSlot(slot, RenderTexture.GetWhiteTexture()); // trigger re-draw

            RenderService.Current.Enqueue(() =>
            {
                tex.Reload(texture);
                TexturePicker.SetSlot(slot, vm.Texture);
                PART_MainTexture.Reload();
                Dispatcher.UIThread.Post(() =>
                {
                    ShowFacts(tex);
                    TextureFacts.Text += Environment.NewLine + string.Join(Environment.NewLine, comparison);
                });
                //vm.Texture = tex;
                //TexturePicker.RefreshTexture(vm.Texture);
            });
        }
    }

    private void AddNewTexture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel vm)
            return;

        RenderService.Current.Enqueue(() =>
        {
            NuTexGenHdr texHdr = new NuTexGenHdr();
            texHdr.RandomiseChecksum();
            NuTexture tex = new NuTexture()
            {
                Header = texHdr
            };

            RenderTexture newTexture = RenderTexture.FromNuTexture(tex);

            Dispatcher.UIThread.Post(() =>
            {
                vm.Textures.Add(newTexture);
                vm.Texture = newTexture;
                TexturePicker.ApplyFilter();
                TexturePicker.MakeVisible(vm.Textures.Count - 1);
            });
        });
    }

    private void RemoveTexture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel vm || vm.Texture == null)
            return;

        var tex = vm.Texture;

        int index = vm.Textures.IndexOf(tex);

        vm.Textures.Remove(tex);

        vm.Texture = null;

        TexturePicker.ApplyFilter();

        TexturePicker.MakeVisible(index);

        tex.Delete();
    }
}