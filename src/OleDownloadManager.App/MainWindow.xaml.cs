using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OleDownloadManager.App.ViewModels;
using OleDownloadManager.Core.Models;

namespace OleDownloadManager.App;

/// <summary>Main window. Code-behind only handles view plumbing (selection, drag-and-drop, placement).</summary>
public partial class MainWindow : Window
{
    private const double DetailsWidth = 300;
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.SearchFocusRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDetailsColumn();

        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Restores the saved size and position, keeping the window on a visible screen.</summary>
    public void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        var width = Math.Min(placement.Width, SystemParameters.VirtualScreenWidth);
        var height = Math.Min(placement.Height, SystemParameters.VirtualScreenHeight);

        // Require the title bar area to be reachable; otherwise centre on the primary screen.
        var visible = placement.Left + 100 < screenRight && placement.Left + width - 100 > screenLeft &&
                      placement.Top >= screenTop - 8 && placement.Top + 40 < screenBottom;
        if (!visible)
        {
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = width;
        Height = height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    public WindowPlacement CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DetailsVisible))
        {
            UpdateDetailsColumn();
        }
    }

    private void UpdateDetailsColumn() => DetailsColumn.Width = new GridLength(_viewModel.DetailsVisible ? DetailsWidth : 0);

    /// <summary>Lets the Name column absorb spare width so the other columns stay fully visible.</summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DownloadList.View is not GridView grid || grid.Columns.Count == 0)
        {
            return;
        }

        var others = grid.Columns.Skip(1).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        grid.Columns[0].Width = Math.Max(220, DownloadList.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 6);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.UpdateSelection(DownloadList.SelectedItems.Cast<DownloadItemViewModel>());

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(DownloadList, source) is ListViewItem { DataContext: DownloadItemViewModel item })
        {
            if (item.CanOpen)
            {
                _viewModel.OpenFileCommand.Execute(null);
            }
            else
            {
                _viewModel.DetailsVisible = true;
            }
        }
    }

    private void OnRevealRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadItemViewModel item })
        {
            DownloadList.SelectedItem = item;
            _viewModel.RevealCommand.Execute(null);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V anywhere outside a text field adds the link on the clipboard.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox and not PasswordBox)
        {
            _viewModel.PasteUrlCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static string? ExtractDroppedText(IDataObject data)
    {
        if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is System.IO.MemoryStream unicode)
        {
            return System.Text.Encoding.Unicode.GetString(unicode.ToArray()).TrimEnd('\0');
        }

        if (data.GetDataPresent(DataFormats.UnicodeText))
        {
            return data.GetData(DataFormats.UnicodeText) as string;
        }

        return data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasText = e.Data.GetDataPresent("UniformResourceLocatorW") || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effects = hasText && e.Source is not TextBox ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        _viewModel.AddFromText(ExtractDroppedText(e.Data));
        e.Handled = true;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised by File > Exit; the application decides how to shut down.</summary>
    public event EventHandler? ExitRequested;
}
