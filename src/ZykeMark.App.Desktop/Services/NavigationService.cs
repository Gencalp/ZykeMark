using System.Windows.Controls;

namespace ZykeMark.App.Desktop.Services;

/// <summary>
/// Simple navigation service to allow ViewModels to request page navigation.
/// </summary>
public sealed class NavigationService
{
    private Frame? _frame;
    private readonly Dictionary<string, Func<Page>> _pageFactories = new();

    /// <summary>
    /// Registers the navigation frame used for page navigation.
    /// </summary>
    public void RegisterFrame(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>
    /// Registers a page factory for a named page.
    /// </summary>
    public void RegisterPage(string pageName, Func<Page> pageFactory)
    {
        _pageFactories[pageName] = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
    }

    /// <summary>
    /// Navigates to the named page.
    /// </summary>
    public bool NavigateTo(string pageName)
    {
        if (_frame is null)
        {
            return false;
        }

        if (!_pageFactories.TryGetValue(pageName, out var factory))
        {
            return false;
        }

        var page = factory();
        return _frame.Navigate(page);
    }

    /// <summary>
    /// Fired when navigation is requested.
    /// </summary>
    public event Action<string>? NavigationRequested;

    /// <summary>
    /// Requests navigation to a page by name. This allows external handling of navigation.
    /// </summary>
    public void RequestNavigation(string pageName)
    {
        NavigationRequested?.Invoke(pageName);
    }
}
