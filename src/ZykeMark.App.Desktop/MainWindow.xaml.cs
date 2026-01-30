using Wpf.Ui.Controls;
using ZykeMark.App.Desktop.ViewModels;
using ZykeMark.App.Desktop.Views;

namespace ZykeMark.App.Desktop;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private Button? _activeNavButton;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        // Subscribe to navigation requests from ViewModel
        _viewModel.NavigationRequested += OnNavigationRequested;

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Navigate to Dashboard by default
        NavigateTo<DashboardPage>(NavDashboard);
    }

    private void OnNavigationRequested(string pageName)
    {
        switch (pageName)
        {
            case "Dashboard":
                NavigateTo<DashboardPage>(NavDashboard);
                break;
            case "LiveSession":
                NavigateTo<LiveSessionPage>(NavLiveSession);
                break;
            case "Sessions":
                NavigateTo<SessionsPage>(NavSessions);
                break;
            case "Settings":
                NavigateTo<SettingsPage>(NavSettings);
                break;
        }
    }

    private void NavigateTo<T>(Button navButton) where T : System.Windows.Controls.Page, new()
    {
        var page = new T { DataContext = _viewModel };
        ContentFrame.Navigate(page);
        UpdateActiveNavButton(navButton);
    }

    private void UpdateActiveNavButton(Button newActiveButton)
    {
        // Reset previous active button
        if (_activeNavButton is not null)
        {
            _activeNavButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        }

        // Set new active button
        _activeNavButton = newActiveButton;
        if (_activeNavButton is not null)
        {
            _activeNavButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        }
    }

    private void NavDashboard_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<DashboardPage>(NavDashboard);
    private void NavLiveSession_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<LiveSessionPage>(NavLiveSession);
    private void NavSessions_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<SessionsPage>(NavSessions);
    private void NavSettings_Click(object sender, System.Windows.RoutedEventArgs e) => NavigateTo<SettingsPage>(NavSettings);
}
