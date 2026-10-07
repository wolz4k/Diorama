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
        var nu = texture?.Original;
        if (nu == null || nu.Data == null || nu.Width == 0)
        {
            TextureFacts.Text = texture == null ? "" : "No image data in this slot.";
            return;
        }

        string format = nu.FourCC switch
        {
            0x31545844 => "DXT1 (BC1, no or 1-bit alpha)",
            0x33545844 => "DXT3 (BC2)",
            0x35545844 => "DXT5 (BC3, with alpha)",
            0x30315844 => nu.Dx10Format == 98 ? "BC7 (DX10)" : $"DX10 format {nu.Dx10Format}",
            _ => "uncompressed",
        };
        string text = $"{nu.Width} × {nu.Height}, {format}, {nu.MipCount} mipmap level{(nu.MipCount == 1 ? "" : "s")}{(nu.IsCubemap ? ", cubemap" : "")}";
        if (string.IsNullOrEmpty(nu.Header?.Name))
            text += ". It comes from the shared LEGO texture page (LEGOTPAGE), so replacing it here isn't saved into this scene.";
        TextureFacts.Text = text;
    }

    private async void ExportTexture_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not EditTexturesViewModel vm || vm.Texture?.Original is not { ImageHeader: not null, Data: not null } nu)
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

        if (files.Count > 0 && DataContext is EditTexturesViewModel vm)
        {
            string filePath = files[0].Path.LocalPath;

            NuTexture texture = NuTexture.Load(filePath, vm.Texture.Original.Header);

            var tex = vm.Texture;

            int slot = TexturePicker.GetSlot(vm.Texture);

            TexturePicker.SetSlot(slot, RenderTexture.GetWhiteTexture()); // trigger re-draw

            RenderService.Current.Enqueue(() =>
            {
                tex.Reload(texture);
                TexturePicker.SetSlot(slot, vm.Texture);
                PART_MainTexture.Reload();
                Dispatcher.UIThread.Post(() => ShowFacts(tex));
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