using System.Windows.Controls;
using ZykeMark.App.Desktop.ViewModels;

namespace ZykeMark.App.Desktop.Views;

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public DashboardPage(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
