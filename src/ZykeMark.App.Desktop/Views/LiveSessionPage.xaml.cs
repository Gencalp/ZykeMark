using System.Windows.Controls;
using ZykeMark.App.Desktop.ViewModels;

namespace ZykeMark.App.Desktop.Views;

public partial class LiveSessionPage : Page
{
    public LiveSessionPage()
    {
        InitializeComponent();
    }

    public LiveSessionPage(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
