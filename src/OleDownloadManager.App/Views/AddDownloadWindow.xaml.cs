using System.Windows;
using OleDownloadManager.App.ViewModels;

namespace OleDownloadManager.App.Views;

public partial class AddDownloadWindow : Window
{
    public AddDownloadWindow(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // PasswordBox does not support binding by design; the view model pulls the value on submit.
        viewModel.PasswordProvider = () => PasswordBox.Password;
        viewModel.CloseRequested += (_, added) =>
        {
            DialogResult = added;
        };
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }
}
