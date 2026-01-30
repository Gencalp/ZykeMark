using System.Windows.Controls;
using ZykeMark.App.Desktop.ViewModels;

namespace ZykeMark.App.Desktop.Views;

public partial class SessionsPage : Page
{
    public SessionsPage()
    {
        InitializeComponent();
    }

    public SessionsPage(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
