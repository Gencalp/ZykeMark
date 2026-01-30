using Wpf.Ui.Controls;
using ZykeMark.App.Desktop.ViewModels;
using ZykeMark.App.Desktop.Views;

namespace ZykeMark.App.Desktop;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Navigate to Dashboard by default
        NavigateTo<DashboardPage>();
    }

    private void NavigateTo<T>() where T : System.Windows.Controls.Page, new()
    {
        var page = new T { DataContext = _viewModel };
        ContentFrame.Navigate(page);
    }

    private void NavDashboard_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<DashboardPage>();
    private void NavLiveSession_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<LiveSessionPage>();
    private void NavSessions_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<SessionsPage>();
    private void NavSettings_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<SettingsPage>();
}
