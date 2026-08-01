using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnapDoc.Export;
using SnapDoc.Models;
using SnapDoc.Services;

namespace SnapDoc.Views;

/// <summary>
/// The workspace: a gallery of captures taken this session, and the multi-capture "Export all"
/// that turns the whole sequence into one documentation file -- the core value proposition.
/// </summary>
public partial class MainWindow : Window
{
    private Point? _dragStart;
    private readonly CollectionViewSource _view = new();
    private Capture? _selected;
    private RecordingClip? _selectedRecording;
    private bool _suppressDetailEvents;

    public MainWindow()
    {
        InitializeComponent();

        _view.Source = App.Workspace.Captures;
        _view.Filter += FilterCaptures;
        _view.SortDescriptions.Add(new SortDescription(nameof(Capture.CreatedAt), ListSortDirection.Descending));
        CaptureList.ItemsSource = _view.View;

        App.Workspace.Captures.CollectionChanged += Captures_CollectionChanged;
        UpdateCountBadge();

        RecordingList.ItemsSource = App.Workspace.Recordings;
        App.Workspace.Recordings.CollectionChanged += (_, _) =>
        {
            UpdateRecordingsEmptyState();
            if (HomePage.Visibility == Visibility.Visible) RefreshHomeDashboard();
        };
        UpdateRecordingsEmptyState();

        RefreshHomeDashboard(); // Home is the default page shown on launch
    }

    private void FilterCaptures(object sender, FilterEventArgs e)
    {
        if (e.Item is not Capture cap) { e.Accepted = false; return; }
        var q = SearchBox.Text?.Trim();
        e.Accepted = string.IsNullOrEmpty(q)
            || cap.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || cap.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Captures_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            AutoSavedText.Text = $"Auto-saved {DateTime.Now:h:mm tt}";
        UpdateCountBadge();
        if (HomePage.Visibility == Visibility.Visible) RefreshHomeDashboard();
    }

    private void UpdateCountBadge()
    {
        int n = App.Workspace.Captures.Count;
        CountBadgeText.Text = n == 1 ? "1 capture" : $"{n} captures";
    }

    // ---- search / sort / view toggle ----

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.View.Refresh();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view.View == null) return; // fires once during InitializeComponent, before the view is wired up
        _view.SortDescriptions.Clear();
        bool newestFirst = SortCombo.SelectedIndex == 0;
        _view.SortDescriptions.Add(new SortDescription(nameof(Capture.CreatedAt),
            newestFirst ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    private void GridViewBtn_Click(object sender, RoutedEventArgs e)
    {
        GridViewBtn.IsChecked = true;
        ListViewBtn.IsChecked = false;
        CaptureList.ItemsPanel = (ItemsPanelTemplate)FindResource("GridPanel");
        CaptureList.ItemTemplate = (DataTemplate)FindResource("CardTemplate");
    }

    private void ListViewBtn_Click(object sender, RoutedEventArgs e)
    {
        ListViewBtn.IsChecked = true;
        GridViewBtn.IsChecked = false;
        CaptureList.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
        CaptureList.ItemTemplate = (DataTemplate)FindResource("RowTemplate");
    }

    // ---- details panel ----

    private void CaptureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = CaptureList.SelectedItems.Count;
        DeleteSelectedBtn.IsEnabled = count > 0;
        DeleteSelectedBtn.Content = count > 1 ? $"Delete ({count})" : "Delete";

        _selected = CaptureList.SelectedItem as Capture;
        if (_selected == null)
        {
            DetailsContent.Visibility = Visibility.Collapsed;
            DetailsPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        RecordingDetailsContent.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;
        DetailsPlaceholder.Visibility = Visibility.Collapsed;

        _suppressDetailEvents = true;
        DetailTitleBox.Text = _selected.Title;
        DetailDescriptionBox.Text = _selected.Caption;
        _suppressDetailEvents = false;

        int viewIndex = CaptureList.Items.IndexOf(_selected);
        DetailPositionText.Text = viewIndex >= 0 ? $"{viewIndex + 1} of {CaptureList.Items.Count}" : "";

        DetailTagsList.ItemsSource = _selected.Tags;
        DetailCreatedText.Text = _selected.CreatedAt.ToString("g");
        DetailDimensionsText.Text = $"{_selected.PixelWidth} x {_selected.PixelHeight}";
        DetailCaptureTypeText.Text = _selected.CaptureKind;
        DetailSizeText.Text = GetFileSizeText(_selected);
        SavedText.Text = "";
    }

    private static string GetFileSizeText(Capture cap) => GetFileSizeText(cap.FilePath);

    private static string GetFileSizeText(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return "Not saved yet";
        double kb = new FileInfo(filePath).Length / 1024.0;
        return kb >= 1024 ? $"{kb / 1024:0.0} MB" : $"{kb:0} KB";
    }

    private void DetailTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDetailEvents || _selected == null) return;
        _selected.Title = DetailTitleBox.Text;
        SavedText.Text = "";
    }

    private void DetailDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDetailEvents || _selected == null) return;
        _selected.Caption = DetailDescriptionBox.Text;
        SavedText.Text = "";
    }

    // Title/Description already write straight into the model as you type (so nothing is lost
    // if you switch captures without clicking this), but nothing else reflected that back --
    // the gallery card kept showing the old title. Save makes the change visible and confirmed.
    private void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Title = DetailTitleBox.Text;
        _selected.Caption = DetailDescriptionBox.Text;
        _view.View.Refresh();
        SavedText.Text = $"Saved {DateTime.Now:h:mm tt}";
    }

    private void TagInputBox_TextChanged(object sender, TextChangedEventArgs e)
        => TagPlaceholder.Visibility = string.IsNullOrEmpty(TagInputBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void TagInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _selected == null) return;
        var tag = TagInputBox.Text.Trim();
        if (tag.Length == 0) return;
        if (!_selected.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            _selected.Tags.Add(tag);
        TagInputBox.Clear();
        e.Handled = true;
    }

    private void RemoveTag_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selected != null && sender is FrameworkElement { DataContext: string tag })
            _selected.Tags.Remove(tag);
    }

    // ---- sidebar navigation: switches which page fills the middle column ----

    private void NavHome_Click(object sender, MouseButtonEventArgs e) => ShowHomePage();
    private void NavScreenshots_Click(object sender, MouseButtonEventArgs e) => ShowGalleryPage();
    private void NavRecordings_Click(object sender, MouseButtonEventArgs e) => ShowRecordingsPage();

    private void ShowHomePage()
    {
        GalleryPage.Visibility = Visibility.Collapsed;
        RecordingsPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
        SetActiveNav(NavHome);
        CaptureList.SelectedItem = null;
        RecordingList.SelectedItem = null;
        RefreshHomeDashboard();
    }

    private void UpdateRecordingsEmptyState()
    {
        bool has = App.Workspace.Recordings.Count > 0;
        RecordingsEmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        RecordingList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RecordingCountBadgeText.Text = App.Workspace.Recordings.Count == 1
            ? "1 recording" : $"{App.Workspace.Recordings.Count} recordings";
    }

    private void RecordingList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordingList.SelectedItem is not RecordingClip rec || !File.Exists(rec.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(rec.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Couldn't open recording", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RecordingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = RecordingList.SelectedItems.Count;
        RecordingDeleteSelectedBtn.IsEnabled = count > 0;
        RecordingDeleteSelectedBtn.Content = count > 1 ? $"Delete ({count})" : "Delete";

        _selectedRecording = RecordingList.SelectedItem as RecordingClip;
        if (_selectedRecording == null)
        {
            RecordingDetailsContent.Visibility = Visibility.Collapsed;
            DetailsContent.Visibility = Visibility.Collapsed;
            DetailsPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        DetailsContent.Visibility = Visibility.Collapsed;
        RecordingDetailsContent.Visibility = Visibility.Visible;
        DetailsPlaceholder.Visibility = Visibility.Collapsed;

        _suppressDetailEvents = true;
        RecordingDetailTitleBox.Text = _selectedRecording.Title;
        RecordingDetailDescriptionBox.Text = _selectedRecording.Caption;
        _suppressDetailEvents = false;

        RecordingDetailCreatedText.Text = _selectedRecording.CreatedAt.ToString("g");
        RecordingDetailDurationText.Text = string.IsNullOrEmpty(_selectedRecording.DurationText) ? "—" : _selectedRecording.DurationText;
        RecordingDetailSizeText.Text = GetFileSizeText(_selectedRecording.FilePath);
        RecordingDetailPathText.Text = _selectedRecording.FilePath;
        RecordingSavedText.Text = "";
    }

    // Title/Description write straight into the model as you type (so nothing is lost switching
    // recordings without clicking Save), same as the capture details panel -- Save just makes the
    // change visible in the gallery card and confirms it, see SaveRecordingDetails_Click.
    private void RecordingDetailTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDetailEvents || _selectedRecording == null) return;
        _selectedRecording.Title = RecordingDetailTitleBox.Text;
        RecordingSavedText.Text = "";
    }

    private void RecordingDetailDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDetailEvents || _selectedRecording == null) return;
        _selectedRecording.Caption = RecordingDetailDescriptionBox.Text;
        RecordingSavedText.Text = "";
    }

    private void SaveRecordingDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecording == null) return;
        _selectedRecording.Title = RecordingDetailTitleBox.Text;
        _selectedRecording.Caption = RecordingDetailDescriptionBox.Text;
        RecordingList.Items.Refresh(); // RecordingClip isn't INotifyPropertyChanged -- this is what
                                        // makes the gallery card's title actually update, same trick
                                        // SaveDetails_Click uses for captures.
        RecordingSavedText.Text = $"Saved {DateTime.Now:h:mm tt}";
    }

    private void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecording == null || !File.Exists(_selectedRecording.FilePath)) return;
        try { Process.Start(new ProcessStartInfo(_selectedRecording.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Couldn't open recording", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowRecordingInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRecording == null || !File.Exists(_selectedRecording.FilePath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selectedRecording.FilePath}\"")); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Couldn't open folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RecordingSelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingList.SelectedItems.Count >= RecordingList.Items.Count && RecordingList.Items.Count > 0)
            RecordingList.UnselectAll();
        else
            RecordingList.SelectAll();
    }

    private void RecordingDeleteSelectedBtn_Click(object sender, RoutedEventArgs e) => DeleteSelectedRecordings();

    private void RecordingList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedRecordings();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RecordingList.SelectAll();
            e.Handled = true;
        }
    }

    private void DeleteSelectedRecordings()
    {
        var chosen = RecordingList.SelectedItems.Cast<RecordingClip>().ToList();
        if (chosen.Count == 0) return;

        string message = chosen.Count == 1
            ? $"Delete \"{chosen[0].Title}\"?\n\nThis also removes the saved video from disk."
            : $"Delete {chosen.Count} recordings?\n\nThis also removes their saved videos from disk.";
        if (MessageBox.Show(message, "Delete recordings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var rec in chosen)
            App.Workspace.DeleteRecording(rec);
    }

    private void RecordingGridViewBtn_Click(object sender, RoutedEventArgs e)
    {
        RecordingGridViewBtn.IsChecked = true;
        RecordingListViewBtn.IsChecked = false;
        RecordingList.ItemsPanel = (ItemsPanelTemplate)FindResource("RecordingGridPanel");
        RecordingList.ItemTemplate = (DataTemplate)FindResource("RecordingCardTemplate");
    }

    private void RecordingListViewBtn_Click(object sender, RoutedEventArgs e)
    {
        RecordingListViewBtn.IsChecked = true;
        RecordingGridViewBtn.IsChecked = false;
        RecordingList.ItemsPanel = (ItemsPanelTemplate)FindResource("RecordingListPanel");
        RecordingList.ItemTemplate = (DataTemplate)FindResource("RecordingRowTemplate");
    }

    private void ShowGalleryPage()
    {
        HomePage.Visibility = Visibility.Collapsed;
        RecordingsPage.Visibility = Visibility.Collapsed;
        GalleryPage.Visibility = Visibility.Visible;
        SetActiveNav(NavScreenshots);
        RecordingList.SelectedItem = null; // otherwise its Details panel state lingers behind the now-hidden page
    }

    private void ShowRecordingsPage()
    {
        HomePage.Visibility = Visibility.Collapsed;
        GalleryPage.Visibility = Visibility.Collapsed;
        RecordingsPage.Visibility = Visibility.Visible;
        SetActiveNav(NavRecordings);
        CaptureList.SelectedItem = null; // otherwise its Details panel state lingers behind the now-hidden page
    }

    // ---- Home dashboard ----

    private void HomeCaptureFullScreen_Click(object sender, RoutedEventArgs e) => CaptureController.CaptureCurrentMonitor();
    private void HomeCaptureWindow_Click(object sender, RoutedEventArgs e) => CaptureController.StartWindowCapture();

    /// <summary>Recomputed on demand (nav click, or a capture/recording arriving while Home is
    /// already showing) rather than kept continuously in sync -- this is a summary view, not a
    /// live-bound list, so that's simpler and plenty responsive for how often it actually changes.</summary>
    private void RefreshHomeDashboard()
    {
        StatScreenshotsText.Text = App.Workspace.Captures.Count.ToString();
        StatRecordingsText.Text = App.Workspace.Recordings.Count.ToString();

        var weekAgo = DateTime.Now.AddDays(-7);
        int thisWeek = App.Workspace.Captures.Count(c => c.CreatedAt >= weekAgo)
                     + App.Workspace.Recordings.Count(r => r.CreatedAt >= weekAgo);
        StatThisWeekText.Text = thisWeek.ToString();

        long totalBytes = App.Workspace.Captures.Sum(c => SafeFileLength(c.FilePath))
                         + App.Workspace.Recordings.Sum(r => SafeFileLength(r.FilePath));
        StatStorageText.Text = FormatBytes(totalBytes);

        var recent = App.Workspace.Captures.Cast<object>()
            .Concat(App.Workspace.Recordings.Cast<object>())
            .OrderByDescending(ActivityCreatedAt)
            .Take(8)
            .ToList();

        RecentActivityEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentActivityList.Children.Clear();
        foreach (var item in recent)
            RecentActivityList.Children.Add(BuildRecentActivityRow(item));
    }

    private static DateTime ActivityCreatedAt(object item) => item switch
    {
        Capture c => c.CreatedAt,
        RecordingClip r => r.CreatedAt,
        _ => DateTime.MinValue,
    };

    private static long SafeFileLength(string? path)
    {
        try { return string.IsNullOrEmpty(path) ? 0 : new FileInfo(path).Length; }
        catch { return 0; } // file locked/moved/gone -- don't let a stale entry break the total
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private Border BuildRecentActivityRow(object item)
    {
        bool isRecording = item is RecordingClip;
        string title = item is Capture c ? c.Title : ((RecordingClip)item).Title;

        var row = new Border
        {
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = isRecording ? "▶" : "🖼",
                FontSize = 14,
                Foreground = (Brush)FindResource("BlueAccent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(iconBorder, 0);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = isRecording ? "Recording" : "Screenshot",
            FontSize = 10, Foreground = (Brush)FindResource("TextSecondary"),
        });
        Grid.SetColumn(textStack, 1);

        var timeText = new TextBlock
        {
            Text = ActivityCreatedAt(item).ToString("g"), FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(timeText, 2);

        grid.Children.Add(iconBorder);
        grid.Children.Add(textStack);
        grid.Children.Add(timeText);
        row.Child = grid;

        row.MouseLeftButtonUp += (_, _) => JumpToActivityItem(item);
        return row;
    }

    private void JumpToActivityItem(object item)
    {
        if (item is Capture cap)
        {
            ShowGalleryPage();
            CaptureList.SelectedItem = cap;
            CaptureList.ScrollIntoView(cap);
        }
        else if (item is RecordingClip rec)
        {
            ShowRecordingsPage();
            RecordingList.SelectedItem = rec;
            RecordingList.ScrollIntoView(rec);
        }
    }

    private void SetActiveNav(Border active)
    {
        foreach (var nav in new[] { NavHome, NavScreenshots, NavRecordings })
        {
            bool isActive = ReferenceEquals(nav, active);
            nav.Background = isActive ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3D)) : Brushes.Transparent;
            var label = (TextBlock)((StackPanel)nav.Child).Children[1];
            label.Foreground = isActive
                ? (Brush)FindResource("TextPrimary")
                : (Brush)FindResource("TextSecondary");
            label.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Ctrl+Shift+1   Capture a region\n" +
            "Ctrl+Shift+2   Capture the current monitor\n" +
            "Ctrl+Shift+3   Capture a window\n" +
            "Ctrl+Shift+R   Start/stop recording\n" +
            "Ctrl+Shift+P   Pause/resume recording\n\n" +
            "Double-click a capture to open it in the editor.\n" +
            "Drag a thumbnail onto another app to share the file.",
            "SnapDoc — Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Closing the workspace just hides it -- the app keeps running in the tray. EXCEPT when the
    // app itself is actually exiting (App.RequestExit sets IsExiting before calling Shutdown()) --
    // cancelling then would silently abort that whole shutdown sequence (see IsExiting's doc
    // comment), leaving the process alive with e.g. the recording toolbar stuck on screen.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.IsExiting) return;
        e.Cancel = true;
        Hide();
        CaptureController.HideIdleRecordingToolbar();
    }

    private void CaptureRegion_Click(object s, RoutedEventArgs e)  => CaptureController.StartRegionCapture();
    private void RecordVideo_Click(object s, RoutedEventArgs e)    => CaptureController.ShowRecordingToolbar();

    // Small "more capture options" dropdown next to the Screenshot button.
    private void CaptureMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var monitor = new MenuItem { Header = "Capture monitor\tCtrl+Shift+2" };
        monitor.Click += (_, _) => CaptureController.CaptureCurrentMonitor();
        var window = new MenuItem { Header = "Capture window\tCtrl+Shift+3" };
        window.Click += (_, _) => CaptureController.StartWindowCapture();
        menu.Items.Add(monitor);
        menu.Items.Add(window);

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var cap = CaptureList.SelectedItem as Capture ?? App.Workspace.Captures.LastOrDefault();
        if (cap == null)
        {
            MessageBox.Show("Capture something first.", "Nothing to edit");
            return;
        }
        var editor = new EditorWindow(cap);
        editor.Show();
        editor.Activate();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Hide();

    // ---- select / delete ----

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        // Toggle: if everything currently shown is already selected, clear instead -- so the
        // same button works for both "select everything" and "start over".
        if (CaptureList.SelectedItems.Count >= CaptureList.Items.Count && CaptureList.Items.Count > 0)
            CaptureList.UnselectAll();
        else
            CaptureList.SelectAll();
    }

    private void CaptureList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedCaptures();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CaptureList.SelectAll();
            e.Handled = true;
        }
    }

    private void DeleteSelectedBtn_Click(object sender, RoutedEventArgs e) => DeleteSelectedCaptures();

    private void DeleteSelectedCaptures()
    {
        var chosen = CaptureList.SelectedItems.Cast<Capture>().ToList();
        if (chosen.Count == 0) return;

        string message = chosen.Count == 1
            ? $"Delete \"{chosen[0].Title}\"?\n\nThis also removes the saved PNG from disk."
            : $"Delete {chosen.Count} captures?\n\nThis also removes their saved PNGs from disk.";
        if (MessageBox.Show(message, "Delete captures", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;

        foreach (var cap in chosen)
            App.Workspace.Delete(cap);

        StatusText.Text = chosen.Count == 1 ? "Deleted 1 capture." : $"Deleted {chosen.Count} captures.";
    }

    private void CaptureList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CaptureList.SelectedItem is Capture cap)
        {
            var editor = new EditorWindow(cap);
            editor.Show();
            editor.Activate();
        }
    }

    // Drag a gallery thumbnail out of the window: hand the OS the real PNG file on disk so
    // browsers, email, chat apps etc. can accept it as a normal file drop.
    private void CaptureList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(null);

    private void CaptureList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Capture cap)
            return;

        try
        {
            if (string.IsNullOrEmpty(cap.FilePath) || !File.Exists(cap.FilePath))
                App.Workspace.SavePng(cap);
        }
        catch { return; }

        var data = new DataObject(DataFormats.FileDrop, new[] { cap.FilePath });
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        // Export selected captures if any are selected, otherwise the whole workspace, in
        // the order shown (newest-first). For a step guide you usually want oldest-first, so
        // we reverse to reading order.
        var chosen = CaptureList.SelectedItems.Count > 0
            ? CaptureList.SelectedItems.Cast<Capture>().ToList()
            : App.Workspace.Captures.ToList();

        if (chosen.Count == 0)
        {
            MessageBox.Show("Capture something first.", "Nothing to export");
            return;
        }
        chosen.Reverse(); // reading order

        var exporters = new IExporter[]
        {
            new MarkdownExporter(), new PdfExporter(), new WordExporter(), new PowerPointExporter()
        };
        var filter = string.Join("|", exporters.Select(x => $"{x.FormatName} (*{x.Extension})|*{x.Extension}"));

        var dlg = new SaveFileDialog { FileName = DocTitleBox.Text, Filter = filter };
        if (dlg.ShowDialog() != true) return;

        var chosenExporter = exporters[dlg.FilterIndex - 1];
        var options = new ExportOptions
        {
            DocumentTitle = string.IsNullOrWhiteSpace(DocTitleBox.Text) ? "SnapDoc Export" : DocTitleBox.Text,
            AsStepGuide = StepGuideCheck.IsChecked == true,
            IncludeOcrText = false
        };

        try
        {
            StatusText.Text = $"Exporting {chosen.Count} captures…";
            await chosenExporter.ExportAsync(chosen, dlg.FileName, options);
            StatusText.Text = $"Exported {chosen.Count} captures to {dlg.FileName}";
            MessageBox.Show($"Exported to {dlg.FileName}", "SnapDoc", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed.";
            MessageBox.Show(ex.ToString(), "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
