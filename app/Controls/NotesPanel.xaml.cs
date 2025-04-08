using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Transom.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Transom.Controls;

public sealed partial class NotesPanel : UserControl
{
    private NoteStore? _store;
    private Note? _current;
    private bool _loading;

    public NotesPanel()
    {
        InitializeComponent();
    }

    public void Attach(NoteStore store)
    {
        _store = store;
        _store.Changed += () => DispatcherQueue.TryEnqueue(BuildList);
    }

    public void Opened()
    {
        if (_store is null) return;
        _current ??= _store.Ordered.FirstOrDefault(n => !n.Pinned) ?? _store.Create();
        Show(_current);
        BuildList();
        Editor.Focus(FocusState.Programmatic);
        Editor.SelectionStart = Editor.Text.Length;
    }

    public void Closed()
    {
        _store?.SaveNow();
        _store?.RemoveIfEmpty(_current);
        if (_current is not null && _store?.Find(_current.Id) is null) _current = null;
        Search.Text = "";
        SavedIndicator.Visibility = Visibility.Collapsed;
    }

    public void StartNewNote()
    {
        if (_store is null) return;
        _store.RemoveIfEmpty(_current);
        _current = _store.Create();
        Show(_current);
        Editor.Focus(FocusState.Programmatic);
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => StartNewNote();

    private void Show(Note note)
    {
        _loading = true;
        Editor.Text = note.Text;
        _loading = false;
        UpdateInfo();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _store is null || _current is null) return;
        _store.Update(_current, Editor.Text);
        SavedIndicator.Visibility = Visibility.Visible;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        var words = Editor.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        EditorInfo.Text = words == 0 ? "Saved on this PC as you type" : $"{words} word{(words == 1 ? "" : "s")} · saved on this PC";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => BuildList();

    private void BuildList()
    {
        if (_store is null) return;
        NoteList.Children.Clear();
        var query = Search.Text.Trim();
        var notes = _store.Ordered
            .Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .Where(n => query.Length == 0 || n.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var note in notes) NoteList.Children.Add(NoteRow(note));
        EmptyText.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = query.Length > 0 ? "No notes match." : "Your notes will appear here.";
    }

    private FrameworkElement NoteRow(Note note)
    {
        var isCurrent = note == _current;
        var grid = new Grid
        {
            Padding = new Thickness(10, 8, 4, 8),
            CornerRadius = new CornerRadius(8),
            ColumnSpacing = 6,
            Background = isCurrent
                ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = note.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 });
        if (note.Pinned) heading.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        text.Children.Add(heading);
        var meta = note.Preview.Length > 0 ? $"{Ago(note.Modified)} · {note.Preview}" : Ago(note.Modified);
        text.Children.Add(new TextBlock
        {
            Text = meta,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        grid.Children.Add(text);

        var menu = Menu(note);
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
        Grid.SetColumn(more, 1);
        grid.Children.Add(more);
        grid.ContextFlyout = menu;

        grid.PointerEntered += (_, _) => { if (!isCurrent) grid.Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"]; };
        grid.PointerExited += (_, _) => { if (!isCurrent) grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
        grid.Tapped += (_, args) =>
        {
            if (args.OriginalSource is FrameworkElement source && IsInside(source, more)) return;
            Open(note);
        };
        return grid;
    }

    private void Open(Note note)
    {
        if (_store is null || note == _current) return;
        _store.SaveNow();
        _store.RemoveIfEmpty(_current);
        _current = note;
        Show(note);
        BuildList();
        Editor.Focus(FocusState.Programmatic);
    }

    private MenuFlyout Menu(Note note)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        var pin = new MenuFlyoutItem { Text = note.Pinned ? "Unpin" : "Pin to top", Icon = new FontIcon { Glyph = note.Pinned ? "" : "" } };
        pin.Click += (_, _) => _store?.TogglePin(note);
        menu.Items.Add(pin);

        var copy = new MenuFlyoutItem { Text = "Copy text", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(note.Text.ReplaceLineEndings("\r\n"));
            Clipboard.SetContent(package);
        };
        menu.Items.Add(copy);

        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
        delete.Click += (_, _) =>
        {
            _store?.Delete(note);
            if (note == _current)
            {
                _current = _store?.Create();
                if (_current is not null) Show(_current);
            }
        };
        menu.Items.Add(delete);
        return menu;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == container) return true;
        }
        return false;
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
        return when.ToString("d MMM");
    }
}
