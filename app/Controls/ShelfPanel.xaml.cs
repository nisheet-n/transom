using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Transom.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace Transom.Controls;

public sealed partial class ShelfPanel : UserControl
{
    private ShelfService? _shelf;
    private nint _hwnd;

    public ShelfPanel()
    {
        InitializeComponent();
    }

    public void Attach(ShelfService shelf, nint hwnd)
    {
        _shelf = shelf;
        _hwnd = hwnd;
        _shelf.Changed += () => DispatcherQueue.TryEnqueue(Build);
    }

    public void Opened() => Build();

    public static async Task<List<string>> ReadDroppedPathsAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems)) return [];
        var items = await view.GetStorageItemsAsync();
        return items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Add to shelf";
    }

    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        _shelf?.Add(await ReadDroppedPathsAsync(e.DataView));
        deferral.Complete();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var files = await picker.PickMultipleFilesAsync();
        _shelf?.Add(files.Select(f => f.Path));
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _shelf?.Clear();

    private void Build()
    {
        if (_shelf is null) return;
        ItemList.Children.Clear();
        var items = _shelf.Items;
        foreach (var item in items) ItemList.Children.Add(Row(item));

        var bytes = items.Sum(i => i.Size);
        Summary.Text = items.Count == 0 ? "" : $"{items.Count} item{(items.Count == 1 ? "" : "s")}{(bytes > 0 ? $" · {FormatSize(bytes)}" : "")}";
        Footer.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement Row(ShelfItem item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(6, 6, 4, 6),
            ColumnSpacing = 10,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CanDrag = item.Exists,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumb = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform };
        var fallback = new FontIcon
        {
            Glyph = item.IsFolder ? "" : "",
            FontSize = 16,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var layers = new Grid();
        layers.Children.Add(fallback);
        layers.Children.Add(thumb);
        thumb.ImageOpened += (_, _) => fallback.Visibility = Visibility.Collapsed;
        var tile = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = layers,
        };
        grid.Children.Add(tile);
        _ = LoadThumbnailAsync(item, thumb);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var folder = Path.GetFileName(Path.GetDirectoryName(item.Path) ?? "") ?? "";
        var meta = !item.Exists ? "Missing: moved or deleted"
            : item.IsFolder ? $"Folder · {folder}"
            : $"{FormatSize(item.Size)} · {folder}";
        text.Children.Add(new TextBlock
        {
            Text = meta,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources[item.Exists ? "TextFillColorTertiaryBrush" : "SystemFillColorCriticalBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var menu = Menu(item);
        var more = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = menu,
        };
        ToolTipService.SetToolTip(more, "More");
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);
        grid.ContextFlyout = menu;
        ToolTipService.SetToolTip(grid, item.Path);

        grid.PointerEntered += (_, _) => grid.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        grid.PointerExited += (_, _) => grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        grid.DoubleTapped += (_, _) => Open(item);
        grid.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            if (await StorageItemAsync(item) is { } storage)
            {
                args.Data.SetStorageItems([storage]);
                args.Data.RequestedOperation = DataPackageOperation.Copy;
            }
            deferral.Complete();
        };
        return grid;
    }

    private MenuFlyout Menu(ShelfItem item)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        Add("Open", "", () => Open(item), item.Exists);
        Add("Show in folder", "", () => ShowInFolder(item), item.Exists);
        Add("Copy", "", async () =>
        {
            if (await StorageItemAsync(item) is not { } storage) return;
            var package = new DataPackage();
            package.SetStorageItems([storage]);
            Clipboard.SetContent(package);
        }, item.Exists);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from shelf", "", () => _shelf?.Remove(item.Id));
        return menu;
    }

    private static void Open(ShelfItem item)
    {
        if (!item.Exists) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static void ShowInFolder(ShelfItem item)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static async Task<IStorageItem?> StorageItemAsync(ShelfItem item)
    {
        try
        {
            return item.IsFolder
                ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                : await StorageFile.GetFileFromPathAsync(item.Path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private static async Task LoadThumbnailAsync(ShelfItem item, Image target)
    {
        try
        {
            var storage = await StorageItemAsync(item);
            StorageItemThumbnail? thumbnail = storage switch
            {
                StorageFile file => await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64),
                StorageFolder folder => await folder.GetThumbnailAsync(ThumbnailMode.SingleItem, 64),
                _ => null,
            };
            if (thumbnail is null) return;
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            target.Source = image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
