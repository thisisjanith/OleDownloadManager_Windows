using System.Windows;
using OleDownloadManager.App.ViewModels;

namespace OleDownloadManager.App.Views;

public partial class ScheduleWindow : Window
{
    private readonly ScheduleViewModel _viewModel;

    public ScheduleWindow(ScheduleViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            WhenBox.Focus();
            WhenBox.SelectAll();
        };
    }

    /// <summary>True when the user cleared the schedule instead of choosing a time.</summary>
    public bool Cleared { get; private set; }

    public DateTimeOffset? Result => _viewModel.Result;

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryAccept())
        {
            DialogResult = true;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        DialogResult = true;
    }
}
