using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ZykeMark.App.Desktop.Commands;
using ZykeMark.App.Desktop.Services;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;

namespace ZykeMark.App.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    // Cached brushes for performance
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(244, 67, 54));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(76, 175, 80));
    private static readonly SolidColorBrush PrimaryBrush = new(Color.FromRgb(107, 31, 173));
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(61, 61, 61));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(255, 152, 0));

    private readonly SessionOrchestrationService _service;
    private readonly SessionDiscoveryService _discoveryService;
    private readonly DispatcherTimer _durationTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly List<FrameSample> _samples = new();
    private readonly List<FrameSample> _pausedSamples = new(); // Samples collected before pause
    private SessionMetadata? _metadata;
    private DateTime? _startUtc;
    private SessionState _state = SessionState.Idle;
    private string _statusMessage = string.Empty;
    private string _lastError = string.Empty;
    private string _processName = string.Empty;
    private string _buildVersion = string.Empty;
    private string _presentMonPath = string.Empty;
    private string _sessionSearchQuery = string.Empty;
    private SessionListItem? _selectedSession;
    private IReadOnlyList<SessionListItem> _allSessions = Array.Empty<SessionListItem>();
    private int _countdownSeconds;
    private int? _selectedProcessId;
    private ProcessInfo? _selectedProcess;
    private int _selectedSortIndex;
    private string _selectedTheme = "Dark";
    private CancellationTokenSource? _countdownCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fired when navigation is requested. The MainWindow subscribes to this.
    /// </summary>
    public event Action<string>? NavigationRequested;

    public MainWindowViewModel()
    {
        _service = new SessionOrchestrationService();
        _discoveryService = new SessionDiscoveryService();
        _service.ChunkReceived += OnChunkReceived;
        _service.Error += OnServiceError;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDuration();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;

        Sessions = new ObservableCollection<SessionListItem>();
        RunningProcesses = new ObservableCollection<ProcessInfo>();

        // Commands
        StartCommand = new RelayCommand(StartSessionWithCountdown, CanStartSession);
        EndCommand = new RelayCommand(EndSession, CanEndSession);
        PauseCommand = new RelayCommand(PauseSession, () => State == SessionState.Running);
        ResumeCommand = new RelayCommand(ResumeSession, () => State == SessionState.Paused);
        ToggleSessionCommand = new RelayCommand(ToggleSession, () => true);
        ExportPdfCommand = new RelayCommand(ExportPdf, () => !string.IsNullOrWhiteSpace(SessionFolder));
        OpenFolderCommand = new RelayCommand(() => _service.OpenSessionFolder(), () => !string.IsNullOrWhiteSpace(SessionFolder));
        CopyErrorCommand = new RelayCommand(CopyErrorDetails, () => !string.IsNullOrWhiteSpace(LastError));
        RefreshSessionsCommand = new RelayCommand(RefreshSessions, () => true);
        ResetFiltersCommand = new RelayCommand(ResetFilters, () => true);
        BrowsePresentMonPathCommand = new RelayCommand(BrowsePresentMonPath, () => true);
        ValidatePresentMonPathCommand = new RelayCommand(ValidatePresentMonPath, () => true);
        OpenSelectedSessionFolderCommand = new RelayCommand(OpenSelectedSessionFolder, () => SelectedSession is not null);
        OpenSelectedSessionReportCommand = new RelayCommand(OpenSelectedSessionReport, () => SelectedSession?.HasReport == true);
        OpenLastSessionFolderCommand = new RelayCommand(OpenLastSessionFolder, () => HasSessions);
        NavigateToSessionsCommand = new RelayCommand(() => NavigationRequested?.Invoke("Sessions"), () => true);
        NavigateToLiveSessionCommand = new RelayCommand(() => NavigationRequested?.Invoke("LiveSession"), () => true);
        RefreshProcessListCommand = new RelayCommand(RefreshProcessList, () => true);
        ApplyThemeCommand = new RelayCommand(ApplyTheme, () => true);
        OpenSessionFolderForItemCommand = new RelayCommand<SessionListItem>(OpenSessionFolderForItem, _ => true);
        OpenSessionReportForItemCommand = new RelayCommand<SessionListItem>(OpenSessionReportForItem, item => item?.HasReport == true);

        StatusText = "Idle";
        DurationText = "00:00:00";
        VerdictSummary = "Awaiting session.";
        NextStepsSummary = "Start a session to see recommendations.";
        DataQualityEtwRisk = "None";
        DataQualityOutlierRisk = "Low";
        DataQualityWarnings = "None";

        // Load saved settings
        LoadSettings();

        // Initial data loads
        RefreshSessions();
        RefreshProcessList();
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand EndCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand ToggleSessionCommand { get; }
    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyErrorCommand { get; }
    public RelayCommand RefreshSessionsCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand BrowsePresentMonPathCommand { get; }
    public RelayCommand ValidatePresentMonPathCommand { get; }
    public RelayCommand OpenSelectedSessionFolderCommand { get; }
    public RelayCommand OpenSelectedSessionReportCommand { get; }
    public RelayCommand OpenLastSessionFolderCommand { get; }
    public RelayCommand NavigateToSessionsCommand { get; }
    public RelayCommand NavigateToLiveSessionCommand { get; }
    public RelayCommand RefreshProcessListCommand { get; }
    public RelayCommand ApplyThemeCommand { get; }
    public RelayCommand<SessionListItem> OpenSessionFolderForItemCommand { get; }
    public RelayCommand<SessionListItem> OpenSessionReportForItemCommand { get; }

    public ObservableCollection<SessionListItem> Sessions { get; }
    public ObservableCollection<ProcessInfo> RunningProcesses { get; }

    public SessionListItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetField(ref _selectedSession, value))
            {
                OpenSelectedSessionFolderCommand.RaiseCanExecuteChanged();
                OpenSelectedSessionReportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText { get; private set; } = string.Empty;
    public string DurationText { get; private set; } = string.Empty;
    public string SessionFolder => _service.CurrentSessionFolder ?? "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(ShowStatusBanner));
            }
        }
    }

    public string LastError
    {
        get => _lastError;
        private set
        {
            if (SetField(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetField(ref _processName, value))
            {
                // Clear the selected process ID when the user manually types a process name
                // This prevents using an old PID with a new manually-typed process name
                if (_selectedProcess is not null && value != _selectedProcess.DisplayName && value != _selectedProcess.FullDisplayName)
                {
                    _selectedProcess = null;
                    _selectedProcessId = null;
                    OnPropertyChanged(nameof(SelectedProcess));
                }
                OnPropertyChanged(nameof(IsProcessValid));
                OnPropertyChanged(nameof(ProcessValidationMessage));
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BuildVersion
    {
        get => _buildVersion;
        set => SetField(ref _buildVersion, value);
    }

    public string PresentMonPath
    {
        get => _presentMonPath;
        set => SetField(ref _presentMonPath, value);
    }

    public string SessionSearchQuery
    {
        get => _sessionSearchQuery;
        set
        {
            if (SetField(ref _sessionSearchQuery, value))
            {
                FilterSessions();
            }
        }
    }

    // Session status properties for UI
    public bool IsSessionRunning => State == SessionState.Running;
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public bool ShowStatusBanner => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasSessions => !HasNoSessions;

    // Dynamic tooltips for disabled state explanation
    public string StartButtonTooltip => State == SessionState.Running
        ? "A session is already running. Stop it first."
        : "Start Capture (Ctrl+Enter)";

    public string StopButtonTooltip => State != SessionState.Running
        ? "No session is currently running."
        : "Stop Capture (Ctrl+Enter)";

    public string ExportPdfTooltip => string.IsNullOrWhiteSpace(SessionFolder)
        ? "Complete a session first to export a PDF report."
        : "Export PDF Report (Ctrl+E)";

    public string OpenFolderTooltip => string.IsNullOrWhiteSpace(SessionFolder)
        ? "Complete a session first to open the session folder."
        : "Open Session Folder";

    // Status banner styling (using cached brushes)
    public Brush StatusBannerBackground => State == SessionState.Error ? ErrorBrush : SuccessBrush;
    public Brush StatusBannerForeground => Brushes.White;

    // Session state badge styling (using cached brushes)
    public Brush SessionStateBadgeBackground => State switch
    {
        SessionState.Running => SuccessBrush,
        SessionState.Error => ErrorBrush,
        SessionState.Completed => PrimaryBrush,
        _ => NeutralBrush
    };
    public Brush SessionStateBadgeForeground => Brushes.White;

    public string CurrentFps { get; private set; } = "0.0";
    public string AvgFps { get; private set; } = "0.0";
    public string OnePercentLowFps { get; private set; } = "0.0";
    public string PointOnePercentLowFps { get; private set; } = "0.0";
    public string AvgFrameTimeMs { get; private set; } = "0.0 ms";
    public string P99FrameTimeMs { get; private set; } = "0.0 ms";
    public string WorstFrameTimeMs { get; private set; } = "0.0 ms";
    public string AvgCpuFrameTimeMs { get; private set; } = "n/a";
    public string AvgGpuFrameTimeMs { get; private set; } = "n/a";
    public string Stutter50Count { get; private set; } = "0";
    public string Stutter100Count { get; private set; } = "0";
    public string VerdictSummary { get; private set; } = string.Empty;
    public string NextStepsSummary { get; private set; } = string.Empty;

    // Data Quality properties
    public string DataQualityEtwRisk { get; private set; } = "None";
    public string DataQualityOutlierRisk { get; private set; } = "Low";
    public string DataQualityWarnings { get; private set; } = "None";

    // Last Session Summary for Dashboard
    public string LastSessionGameName { get; private set; } = "N/A";
    public string LastSessionDate { get; private set; } = "N/A";
    public string LastSessionAvgFps { get; private set; } = "N/A";
    public string LastSessionP99 { get; private set; } = "N/A";

    // PresentMon Validation
    public bool ShowPresentMonValidation { get; private set; }
    public string PresentMonValidationMessage { get; private set; } = string.Empty;
    public Brush PresentMonValidationBrush { get; private set; } = SuccessBrush;
    public Wpf.Ui.Controls.SymbolRegular PresentMonValidationIcon { get; private set; } = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;

    // Process selection
    public ProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                if (value is not null)
                {
                    _selectedProcessId = value.ProcessId;
                    ProcessName = value.DisplayName;
                }
                else
                {
                    _selectedProcessId = null;
                }
                OnPropertyChanged(nameof(IsProcessValid));
                OnPropertyChanged(nameof(ProcessValidationMessage));
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProcessValid => _selectedProcessId.HasValue || !string.IsNullOrWhiteSpace(ProcessName);
    public string ProcessValidationMessage => IsProcessValid
        ? ""
        : "Select a running process or enter a process name/PID to start capture.";

    // Countdown
    public int CountdownSeconds
    {
        get => _countdownSeconds;
        private set
        {
            if (SetField(ref _countdownSeconds, value))
            {
                OnPropertyChanged(nameof(CountdownText));
                OnPropertyChanged(nameof(IsCountdownActive));
            }
        }
    }

    public string CountdownText => IsCountdownActive ? $"Starting in {CountdownSeconds}..." : "";
    public bool IsCountdownActive => State == SessionState.Countdown && CountdownSeconds > 0;
    public bool IsSessionPaused => State == SessionState.Paused;

    // Sort and filter
    public int SelectedSortIndex
    {
        get => _selectedSortIndex;
        set
        {
            if (SetField(ref _selectedSortIndex, value))
            {
                ApplySortAndFilter();
            }
        }
    }

    // Theme
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetField(ref _selectedTheme, value))
            {
                ApplyTheme();
            }
        }
    }

    public int SelectedThemeIndex
    {
        get => _selectedTheme switch { "Dark" => 0, "Light" => 1, "System" => 2, _ => 0 };
        set
        {
            var theme = value switch { 0 => "Dark", 1 => "Light", 2 => "System", _ => "Dark" };
            if (_selectedTheme != theme)
            {
                _selectedTheme = theme;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedTheme));
                ApplyTheme();
            }
        }
    }

    private SessionState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                UpdateStatus();
                StartCommand.RaiseCanExecuteChanged();
                EndCommand.RaiseCanExecuteChanged();
                PauseCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
                ToggleSessionCommand.RaiseCanExecuteChanged();
                ExportPdfCommand.RaiseCanExecuteChanged();
                OpenFolderCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsSessionRunning));
                OnPropertyChanged(nameof(IsSessionPaused));
                OnPropertyChanged(nameof(IsCountdownActive));
                OnPropertyChanged(nameof(CountdownText));
                OnPropertyChanged(nameof(SessionStateBadgeBackground));
                OnPropertyChanged(nameof(StatusBannerBackground));
                OnPropertyChanged(nameof(StartButtonTooltip));
                OnPropertyChanged(nameof(StopButtonTooltip));
                OnPropertyChanged(nameof(ExportPdfTooltip));
                OnPropertyChanged(nameof(OpenFolderTooltip));
            }
        }
    }

    private bool CanStartSession()
    {
        // Can only start when idle/completed/error AND have a valid process
        var stateOk = State is SessionState.Idle or SessionState.Completed or SessionState.Error;
        var processOk = _selectedProcessId.HasValue || !string.IsNullOrWhiteSpace(ProcessName);
        return stateOk && processOk;
    }

    private bool CanEndSession()
    {
        return State is SessionState.Running or SessionState.Paused or SessionState.Countdown;
    }

    private void ToggleSession()
    {
        if (State == SessionState.Running || State == SessionState.Countdown || State == SessionState.Paused)
        {
            EndSession();
        }
        else if (State is SessionState.Idle or SessionState.Completed or SessionState.Error)
        {
            StartSessionWithCountdown();
        }
    }

    private void StartSessionWithCountdown()
    {
        if (!CanStartSession())
        {
            StatusMessage = ProcessValidationMessage;
            return;
        }

        // Dispose previous CancellationTokenSource if any
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        _countdownCts = new CancellationTokenSource();
        CountdownSeconds = 5;
        State = SessionState.Countdown;
        StatusMessage = "Session starting in 5 seconds...";
        _countdownTimer.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_countdownCts?.IsCancellationRequested == true)
        {
            _countdownTimer.Stop();
            return;
        }

        CountdownSeconds--;

        if (CountdownSeconds <= 0)
        {
            _countdownTimer.Stop();
            StartSession();
        }
        else
        {
            StatusMessage = $"Session starting in {CountdownSeconds} seconds...";
        }
    }

    private void StartSession()
    {
        try
        {
            State = SessionState.Running;
            _samples.Clear();
            _pausedSamples.Clear();
            _metadata = _service.StartSession(ProcessName, BuildVersion, new RunConfig());
            _startUtc = DateTime.UtcNow;
            _durationTimer.Start();
            StatusMessage = "Session started.";
            OnPropertyChanged(nameof(SessionFolder));
        }
        catch (Exception ex)
        {
            HandleError("Failed to start session.", ex);
            State = SessionState.Error;
        }
    }

    private void PauseSession()
    {
        if (State != SessionState.Running)
        {
            return;
        }

        try
        {
            // Save current samples to pause buffer
            _pausedSamples.AddRange(_samples);
            _samples.Clear();

            _durationTimer.Stop();
            State = SessionState.Paused;
            StatusMessage = "Session paused. Click Resume to continue.";
        }
        catch (Exception ex)
        {
            HandleError("Failed to pause session.", ex);
        }
    }

    private void ResumeSession()
    {
        if (State != SessionState.Paused)
        {
            return;
        }

        try
        {
            // Restore paused samples
            _samples.AddRange(_pausedSamples);
            _pausedSamples.Clear();

            _durationTimer.Start();
            State = SessionState.Running;
            StatusMessage = "Session resumed.";
        }
        catch (Exception ex)
        {
            HandleError("Failed to resume session.", ex);
        }
    }

    private void EndSession()
    {
        // Cancel and dispose countdown if active
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        _countdownCts = null;
        _countdownTimer.Stop();

        if (State == SessionState.Countdown)
        {
            State = SessionState.Idle;
            CountdownSeconds = 0;
            StatusMessage = "Session cancelled.";
            return;
        }

        try
        {
            // Merge paused samples back if any
            if (_pausedSamples.Count > 0)
            {
                _samples.InsertRange(0, _pausedSamples);
                _pausedSamples.Clear();
            }

            State = SessionState.Ending;
            _service.StopSession();
            _durationTimer.Stop();

            // Reset timer display
            DurationText = "00:00:00";
            OnPropertyChanged(nameof(DurationText));

            State = SessionState.Completed;
            StatusMessage = "Session ended.";

            // Update data quality from the saved summary
            UpdateDataQualityFromSummary();

            // Refresh sessions list to include the new session
            RefreshSessions();
        }
        catch (Exception ex)
        {
            HandleError("Failed to stop session.", ex);
            State = SessionState.Error;
        }
    }

    private void UpdateDataQualityFromSummary()
    {
        if (string.IsNullOrWhiteSpace(SessionFolder))
        {
            return;
        }

        try
        {
            var summary = _discoveryService.LoadSessionSummary(SessionFolder);
            if (summary?.DataQuality is not null)
            {
                DataQualityEtwRisk = summary.DataQuality.EtwEventsLostRiskLevel ?? "None";
                DataQualityWarnings = summary.DataQuality.CaptureWarnings?.Count > 0
                    ? string.Join(", ", summary.DataQuality.CaptureWarnings)
                    : "None";

                // Calculate outlier risk based on P99 frame time from summary (not local samples)
                if (summary.Aggregates is not null)
                {
                    var p99FrameTime = summary.Aggregates.P99FrameTimeMs;
                    DataQualityOutlierRisk = p99FrameTime > 1000 ? "High" : p99FrameTime > 500 ? "Moderate" : "Low";
                }

                OnPropertyChanged(nameof(DataQualityEtwRisk));
                OnPropertyChanged(nameof(DataQualityOutlierRisk));
                OnPropertyChanged(nameof(DataQualityWarnings));
            }
        }
        catch
        {
            // Ignore errors reading summary
        }
    }

    private void ExportPdf()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PDF report|*.pdf",
                FileName = $"ZykeMark_Report_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf"
            };

            if (dialog.ShowDialog() == true)
            {
                _service.ExportPdf(dialog.FileName);
                StatusMessage = "PDF exported.";
            }
        }
        catch (Exception ex)
        {
            HandleError("PDF export failed.", ex);
        }
    }

    private void CopyErrorDetails()
    {
        if (string.IsNullOrWhiteSpace(LastError))
        {
            StatusMessage = "No error details to copy.";
            return;
        }

        Clipboard.SetText(LastError);
        StatusMessage = "Error details copied.";
    }

    private void RefreshSessions()
    {
        try
        {
            // Cache all sessions for efficient filtering
            _allSessions = _discoveryService.DiscoverSessions();

            // Apply current sort and filter
            ApplySortAndFilter();

            // Update last session summary for Dashboard (uses _allSessions which is sorted by date descending)
            UpdateLastSessionSummary();

            OpenLastSessionFolderCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            HandleError("Failed to refresh sessions.", ex);
        }
    }

    private void UpdateLastSessionSummary()
    {
        // Use cached _allSessions (already sorted by date descending from discovery service)
        if (_allSessions.Count == 0)
        {
            LastSessionGameName = "N/A";
            LastSessionDate = "N/A";
            LastSessionAvgFps = "N/A";
            LastSessionP99 = "N/A";
        }
        else
        {
            var lastSession = _allSessions.First();
            LastSessionGameName = lastSession.GameName;
            LastSessionDate = lastSession.StartDateDisplay;
            LastSessionAvgFps = lastSession.AvgFpsDisplay;
            LastSessionP99 = lastSession.P99Display;
        }

        OnPropertyChanged(nameof(LastSessionGameName));
        OnPropertyChanged(nameof(LastSessionDate));
        OnPropertyChanged(nameof(LastSessionAvgFps));
        OnPropertyChanged(nameof(LastSessionP99));
    }

    private void FilterSessions()
    {
        // Use the unified sort and filter method
        ApplySortAndFilter();
    }

    private void OpenSelectedSessionFolder()
    {
        if (SelectedSession is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedSession.SessionFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            HandleError("Failed to open session folder.", ex);
        }
    }

    private void OpenLastSessionFolder()
    {
        if (_allSessions.Count == 0)
        {
            return;
        }

        try
        {
            var lastSession = _allSessions.First();
            Process.Start(new ProcessStartInfo
            {
                FileName = lastSession.SessionFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            HandleError("Failed to open last session folder.", ex);
        }
    }

    private void OpenSelectedSessionReport()
    {
        if (SelectedSession is null || !SelectedSession.HasReport)
        {
            return;
        }

        try
        {
            var reportPath = System.IO.Path.Combine(SelectedSession.SessionFolder, "report.pdf");
            if (System.IO.File.Exists(reportPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            HandleError("Failed to open report.", ex);
        }
    }

    private void BrowsePresentMonPath()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable|*.exe",
            Title = "Select PresentMon executable"
        };

        if (dialog.ShowDialog() == true)
        {
            PresentMonPath = dialog.FileName;
            // Hide validation message when path changes
            ShowPresentMonValidation = false;
            OnPropertyChanged(nameof(ShowPresentMonValidation));
        }
    }

    private void ValidatePresentMonPath()
    {
        ShowPresentMonValidation = true;

        if (string.IsNullOrWhiteSpace(PresentMonPath))
        {
            PresentMonValidationMessage = "Path is empty. Auto-detect will be used.";
            PresentMonValidationBrush = NeutralBrush;
            PresentMonValidationIcon = Wpf.Ui.Controls.SymbolRegular.Info24;
        }
        else if (!System.IO.File.Exists(PresentMonPath))
        {
            PresentMonValidationMessage = "File not found. Please check the path.";
            PresentMonValidationBrush = ErrorBrush;
            PresentMonValidationIcon = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
        }
        else if (!PresentMonPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            PresentMonValidationMessage = "File is not an executable.";
            PresentMonValidationBrush = ErrorBrush;
            PresentMonValidationIcon = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
        }
        else if (!PresentMonPath.Contains("PresentMon", StringComparison.OrdinalIgnoreCase))
        {
            PresentMonValidationMessage = "Warning: File name doesn't contain 'PresentMon'. Make sure this is the correct executable.";
            PresentMonValidationBrush = NeutralBrush;
            PresentMonValidationIcon = Wpf.Ui.Controls.SymbolRegular.Warning24;
        }
        else
        {
            PresentMonValidationMessage = "Valid PresentMon executable found.";
            PresentMonValidationBrush = SuccessBrush;
            PresentMonValidationIcon = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
        }

        OnPropertyChanged(nameof(ShowPresentMonValidation));
        OnPropertyChanged(nameof(PresentMonValidationMessage));
        OnPropertyChanged(nameof(PresentMonValidationBrush));
        OnPropertyChanged(nameof(PresentMonValidationIcon));
    }

    private void OpenSessionFolderForItem(SessionListItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.SessionFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            HandleError("Failed to open session folder.", ex);
        }
    }

    private void OpenSessionReportForItem(SessionListItem? item)
    {
        if (item is null || !item.HasReport)
        {
            StatusMessage = "No PDF report available for this session.";
            return;
        }

        try
        {
            var reportPath = Path.Combine(item.SessionFolder, "report.pdf");
            if (File.Exists(reportPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = reportPath,
                    UseShellExecute = true
                });
            }
            else
            {
                StatusMessage = "PDF report file not found.";
            }
        }
        catch (Exception ex)
        {
            HandleError("Failed to open report.", ex);
        }
    }

    private void OnChunkReceived(object? sender, RawSampleChunk chunk)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _samples.AddRange(chunk.Samples);
            UpdateMetrics();
        });
    }

    private void OnServiceError(object? sender, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() => HandleError("Collector error.", ex));
    }

    private void UpdateMetrics()
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var aggregator = new ZykeMarkAggregator();
        var aggregates = aggregator.Aggregate(_samples.ToArray());
        var latest = _samples[^1];
        var worst = _samples.Max(sample => sample.FrameTimeMs);
        var stutter50 = _samples.Count(sample => sample.FrameTimeMs >= 50);
        var stutter100 = _samples.Count(sample => sample.FrameTimeMs >= 100);

        CurrentFps = (1000.0 / latest.FrameTimeMs).ToString("F1");
        AvgFps = aggregates.AvgFps.ToString("F1");
        OnePercentLowFps = aggregates.OnePercentLowFps.ToString("F1");
        PointOnePercentLowFps = aggregates.PointOnePercentLowFps.ToString("F1");
        AvgFrameTimeMs = $"{aggregates.AvgFrameTimeMs:F2} ms";
        P99FrameTimeMs = $"{aggregates.P99FrameTimeMs:F2} ms";
        WorstFrameTimeMs = $"{worst:F2} ms";
        AvgCpuFrameTimeMs = aggregates.AvgCpuFrameTimeMs.HasValue ? $"{aggregates.AvgCpuFrameTimeMs.Value:F2} ms" : "n/a";
        AvgGpuFrameTimeMs = aggregates.AvgGpuFrameTimeMs.HasValue ? $"{aggregates.AvgGpuFrameTimeMs.Value:F2} ms" : "n/a";
        Stutter50Count = stutter50.ToString();
        Stutter100Count = stutter100.ToString();

        VerdictSummary = BuildVerdictSummary(aggregates);
        NextStepsSummary = BuildNextStepsSummary(aggregates);

        OnPropertyChanged(nameof(CurrentFps));
        OnPropertyChanged(nameof(AvgFps));
        OnPropertyChanged(nameof(OnePercentLowFps));
        OnPropertyChanged(nameof(PointOnePercentLowFps));
        OnPropertyChanged(nameof(AvgFrameTimeMs));
        OnPropertyChanged(nameof(P99FrameTimeMs));
        OnPropertyChanged(nameof(WorstFrameTimeMs));
        OnPropertyChanged(nameof(AvgCpuFrameTimeMs));
        OnPropertyChanged(nameof(AvgGpuFrameTimeMs));
        OnPropertyChanged(nameof(Stutter50Count));
        OnPropertyChanged(nameof(Stutter100Count));
        OnPropertyChanged(nameof(VerdictSummary));
        OnPropertyChanged(nameof(NextStepsSummary));
    }

    private void UpdateDuration()
    {
        if (!_startUtc.HasValue)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _startUtc.Value;
        DurationText = elapsed.ToString("hh\\:mm\\:ss");
        OnPropertyChanged(nameof(DurationText));
    }

    private void UpdateStatus()
    {
        StatusText = State switch
        {
            SessionState.Idle => "Idle",
            SessionState.Countdown => "Starting...",
            SessionState.Running => "Running",
            SessionState.Paused => "Paused",
            SessionState.Ending => "Ending",
            SessionState.Completed => "Completed",
            SessionState.Error => "Error",
            _ => "Idle"
        };
        OnPropertyChanged(nameof(StatusText));
    }

    private string BuildVerdictSummary(SessionAggregates aggregates)
    {
        var hint = GetBalanceHint(aggregates.AvgCpuFrameTimeMs, aggregates.AvgGpuFrameTimeMs);
        var stability = aggregates.P99FrameTimeMs > 50 ? "Needs attention" : "OK";
        return $"Balance: {hint} | Stability: {stability}";
    }

    private string BuildNextStepsSummary(SessionAggregates aggregates)
    {
        var hint = GetBalanceHint(aggregates.AvgCpuFrameTimeMs, aggregates.AvgGpuFrameTimeMs);
        return hint switch
        {
            "Likely CPU-bound" => "Profile main thread, reduce simulation cost, improve batching.",
            "Likely GPU-bound" => "Review shaders, lower resolution scaling, optimize textures.",
            _ => "Capture longer runs and ensure consistent settings."
        };
    }

    private static string GetBalanceHint(double? cpuMs, double? gpuMs)
    {
        if (!cpuMs.HasValue || !gpuMs.HasValue)
        {
            return "Balanced/unclear";
        }

        if (cpuMs.Value > gpuMs.Value * 1.1)
        {
            return "Likely CPU-bound";
        }

        if (gpuMs.Value > cpuMs.Value * 1.1)
        {
            return "Likely GPU-bound";
        }

        return "Balanced";
    }

    private void RefreshProcessList()
    {
        try
        {
            RunningProcesses.Clear();
            var processes = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        // Filter to processes with main windows (top-level apps)
                        return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderBy(p => p.ProcessName)
                .Take(100); // Limit for performance

            foreach (var proc in processes)
            {
                try
                {
                    RunningProcesses.Add(new ProcessInfo(
                        proc.Id,
                        proc.ProcessName,
                        proc.MainWindowTitle));
                }
                catch
                {
                    // Skip inaccessible processes
                }
            }

            StatusMessage = $"Found {RunningProcesses.Count} running applications.";
        }
        catch (Exception ex)
        {
            HandleError("Failed to refresh process list.", ex);
        }
    }

    private void ResetFilters()
    {
        // Reset search query using backing field to avoid triggering filter during reset
        _sessionSearchQuery = "";
        OnPropertyChanged(nameof(SessionSearchQuery));

        // Reset sort to default (Date desc = index 0) using backing field
        _selectedSortIndex = 0;
        OnPropertyChanged(nameof(SelectedSortIndex));

        // Apply the default sort (Date desc) without triggering the filter chain
        var sorted = _allSessions.OrderByDescending(s => s.StartedAtUtc).ToList();
        Sessions.Clear();
        foreach (var session in sorted)
        {
            Sessions.Add(session);
        }

        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(HasSessions));
        StatusMessage = $"Filters reset. Showing {Sessions.Count} session(s).";
    }

    private void ApplySortAndFilter()
    {
        try
        {
            var query = SessionSearchQuery?.Trim().ToLowerInvariant();

            // Filter first
            var filtered = string.IsNullOrEmpty(query)
                ? _allSessions.ToList()
                : _allSessions.Where(s =>
                    (s.GameName?.ToLowerInvariant().Contains(query) == true) ||
                    (s.BuildVersion?.ToLowerInvariant().Contains(query) == true) ||
                    (s.SessionId?.ToLowerInvariant().Contains(query) == true)).ToList();

            // Then sort based on selected index
            IEnumerable<SessionListItem> sorted = SelectedSortIndex switch
            {
                0 => filtered.OrderByDescending(s => s.StartedAtUtc), // Date desc (default)
                1 => filtered.OrderBy(s => s.StartedAtUtc),           // Date asc
                2 => filtered.OrderByDescending(s => s.AvgFps ?? 0),  // Avg FPS desc
                3 => filtered.OrderBy(s => s.AvgFps ?? 0),            // Avg FPS asc
                4 => filtered.OrderByDescending(s => s.DurationMs ?? 0), // Duration desc
                5 => filtered.OrderBy(s => s.DurationMs ?? 0),        // Duration asc
                _ => filtered.OrderByDescending(s => s.StartedAtUtc)
            };

            Sessions.Clear();
            foreach (var session in sorted)
            {
                Sessions.Add(session);
            }

            OnPropertyChanged(nameof(HasNoSessions));
            OnPropertyChanged(nameof(HasSessions));
            StatusMessage = $"Found {Sessions.Count} session(s).";
        }
        catch
        {
            // Ignore sort/filter errors
        }
    }

    private void ApplyTheme()
    {
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                SelectedTheme switch
                {
                    "Light" => Wpf.Ui.Appearance.ApplicationTheme.Light,
                    "System" => Wpf.Ui.Appearance.ApplicationTheme.Unknown, // Let system decide
                    _ => Wpf.Ui.Appearance.ApplicationTheme.Dark
                });

            SaveSettings();
            StatusMessage = $"Theme changed to {SelectedTheme}.";
        }
        catch (Exception ex)
        {
            HandleError("Failed to apply theme.", ex);
        }
    }

    private void LoadSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    _selectedTheme = settings.Theme ?? "Dark";
                    _presentMonPath = settings.PresentMonPath ?? "";
                    OnPropertyChanged(nameof(SelectedTheme));
                    OnPropertyChanged(nameof(SelectedThemeIndex));
                    OnPropertyChanged(nameof(PresentMonPath));

                    // Apply theme on load
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                        _selectedTheme switch
                        {
                            "Light" => Wpf.Ui.Appearance.ApplicationTheme.Light,
                            "System" => Wpf.Ui.Appearance.ApplicationTheme.Unknown,
                            _ => Wpf.Ui.Appearance.ApplicationTheme.Dark
                        });
                }
            }
        }
        catch
        {
            // Ignore settings load errors
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsPath = GetSettingsPath();
            var settingsDir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
            }

            var settings = new AppSettings
            {
                Theme = _selectedTheme,
                PresentMonPath = _presentMonPath
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // Ignore settings save errors
        }
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "settings.json");
    }

    private void HandleError(string message, Exception ex)
    {
        LastError = ex.ToString();
        StatusMessage = message;
        CopyErrorCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum SessionState
{
    Idle,
    Countdown,
    Running,
    Paused,
    Ending,
    Completed,
    Error
}

/// <summary>
/// Represents a running process for the process selector.
/// </summary>
public sealed record ProcessInfo(int ProcessId, string ProcessName, string WindowTitle)
{
    public string DisplayName => $"{ProcessName} (PID {ProcessId})";
    public string FullDisplayName => string.IsNullOrWhiteSpace(WindowTitle)
        ? DisplayName
        : $"{ProcessName} - {WindowTitle} (PID {ProcessId})";
}

/// <summary>
/// App settings for persistence.
/// </summary>
public sealed class AppSettings
{
    public string? Theme { get; set; }
    public string? PresentMonPath { get; set; }
}
