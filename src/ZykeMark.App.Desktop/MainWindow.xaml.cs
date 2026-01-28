using System.Windows;
using ZykeMark.App.Desktop.ViewModels;

namespace ZykeMark.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
