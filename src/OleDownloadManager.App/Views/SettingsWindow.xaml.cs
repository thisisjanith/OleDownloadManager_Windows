using System.Windows;
using OleDownloadManager.App.ViewModels;

namespace OleDownloadManager.App.Views;

public partial class SettingsWindow : Window
{
    private bool _saved;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            _saved = true;
            DialogResult = true;
        };
        Closed += (_, _) =>
        {
            if (!_saved)
            {
                viewModel.RevertPreview();
            }
        };
    }
}
