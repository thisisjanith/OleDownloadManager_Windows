using System.Windows;
using OleDownloadManager.App.ViewModels;

namespace OleDownloadManager.App.Views;

public partial class MediaWindow : Window
{
    public MediaWindow(MediaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => DialogResult = true;
        Closed += (_, _) => viewModel.CancelPending();
        Loaded += (_, _) => UrlBox.Focus();
    }
}
