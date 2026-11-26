using System.Windows.Controls;
using System.Windows.Input;
using FastVRecorder.App.ViewModels;

namespace FastVRecorder.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnFolderLostFocus(object sender, System.Windows.RoutedEventArgs e) => CommitFolder();

    private void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFolder();
            e.Handled = true;
        }
    }

    private void CommitFolder()
    {
        if (DataContext is SettingsViewModel vm) vm.CommitFolderInput();
    }
}
