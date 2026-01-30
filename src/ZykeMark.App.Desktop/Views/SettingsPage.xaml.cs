using System.Windows.Controls;
using ZykeMark.App.Desktop.ViewModels;

namespace ZykeMark.App.Desktop.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsPage(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
