using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    private readonly SessionOrchestrationService _service;
    private readonly SessionDiscoveryService _discoveryService;
    private readonly DispatcherTimer _durationTimer;
    private readonly List<FrameSample> _samples = new();
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel()
    {
        _service = new SessionOrchestrationService();
        _discoveryService = new SessionDiscoveryService();
        _service.ChunkReceived += OnChunkReceived;
        _service.Error += OnServiceError;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDuration();

        Sessions = new ObservableCollection<SessionListItem>();

        StartCommand = new RelayCommand(StartSession, () => State is SessionState.Idle or SessionState.Completed or SessionState.Error);
        EndCommand = new RelayCommand(EndSession, () => State == SessionState.Running);
        ToggleSessionCommand = new RelayCommand(ToggleSession, () => true);
        ExportPdfCommand = new RelayCommand(ExportPdf, () => !string.IsNullOrWhiteSpace(SessionFolder));
        OpenFolderCommand = new RelayCommand(() => _service.OpenSessionFolder(), () => !string.IsNullOrWhiteSpace(SessionFolder));
        CopyErrorCommand = new RelayCommand(CopyErrorDetails, () => !string.IsNullOrWhiteSpace(LastError));
        RefreshSessionsCommand = new RelayCommand(RefreshSessions, () => true);
        BrowsePresentMonPathCommand = new RelayCommand(BrowsePresentMonPath, () => true);
        ValidatePresentMonPathCommand = new RelayCommand(ValidatePresentMonPath, () => true);
        OpenSelectedSessionFolderCommand = new RelayCommand(OpenSelectedSessionFolder, () => SelectedSession is not null);
        OpenSelectedSessionReportCommand = new RelayCommand(OpenSelectedSessionReport, () => SelectedSession?.HasReport == true);

        StatusText = "Idle";
        DurationText = "00:00:00";
        VerdictSummary = "Awaiting session.";
        NextStepsSummary = "Start a session to see recommendations.";
        DataQualityEtwRisk = "None";
        DataQualityOutlierRisk = "Low";
        DataQualityWarnings = "None";

        // Initial session discovery
        RefreshSessions();
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand EndCommand { get; }
    public RelayCommand ToggleSessionCommand { get; }
    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyErrorCommand { get; }
    public RelayCommand RefreshSessionsCommand { get; }
    public RelayCommand BrowsePresentMonPathCommand { get; }
    public RelayCommand ValidatePresentMonPathCommand { get; }
    public RelayCommand OpenSelectedSessionFolderCommand { get; }
    public RelayCommand OpenSelectedSessionReportCommand { get; }

    public ObservableCollection<SessionListItem> Sessions { get; }

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
        set => SetField(ref _processName, value);
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
                ToggleSessionCommand.RaiseCanExecuteChanged();
                ExportPdfCommand.RaiseCanExecuteChanged();
                OpenFolderCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsSessionRunning));
                OnPropertyChanged(nameof(SessionStateBadgeBackground));
                OnPropertyChanged(nameof(StatusBannerBackground));
                OnPropertyChanged(nameof(StartButtonTooltip));
                OnPropertyChanged(nameof(StopButtonTooltip));
                OnPropertyChanged(nameof(ExportPdfTooltip));
                OnPropertyChanged(nameof(OpenFolderTooltip));
            }
        }
    }

    private void ToggleSession()
    {
        if (State == SessionState.Running)
        {
            EndSession();
        }
        else if (State is SessionState.Idle or SessionState.Completed or SessionState.Error)
        {
            StartSession();
        }
    }

    private void StartSession()
    {
        try
        {
            State = SessionState.Running;
            _samples.Clear();
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

    private void EndSession()
    {
        try
        {
            State = SessionState.Ending;
            _service.StopSession();
            _durationTimer.Stop();
            UpdateDuration();
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

                // Calculate outlier risk based on worst frame time
                if (summary.Aggregates is not null)
                {
                    var worstFrameTime = _samples.Count > 0 ? _samples.Max(s => s.FrameTimeMs) : 0;
                    DataQualityOutlierRisk = worstFrameTime > 1000 ? "High" : worstFrameTime > 500 ? "Moderate" : "Low";
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
            var discoveredSessions = _discoveryService.DiscoverSessions();
            Sessions.Clear();

            foreach (var session in discoveredSessions)
            {
                Sessions.Add(session);
            }

            // Update last session summary for Dashboard
            UpdateLastSessionSummary();

            OnPropertyChanged(nameof(HasNoSessions));
            OnPropertyChanged(nameof(HasSessions));
            StatusMessage = Sessions.Count > 0
                ? $"Found {Sessions.Count} session(s)."
                : "No sessions found.";
        }
        catch (Exception ex)
        {
            HandleError("Failed to refresh sessions.", ex);
        }
    }

    private void UpdateLastSessionSummary()
    {
        if (Sessions.Count == 0)
        {
            LastSessionGameName = "N/A";
            LastSessionDate = "N/A";
            LastSessionAvgFps = "N/A";
            LastSessionP99 = "N/A";
        }
        else
        {
            var lastSession = Sessions.First(); // Already sorted by date descending
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
        // Re-discover and filter by search query
        try
        {
            var discoveredSessions = _discoveryService.DiscoverSessions();
            Sessions.Clear();

            var query = SessionSearchQuery?.Trim().ToLowerInvariant();
            var filtered = string.IsNullOrEmpty(query)
                ? discoveredSessions
                : discoveredSessions.Where(s =>
                    (s.GameName?.ToLowerInvariant().Contains(query) == true) ||
                    (s.BuildVersion?.ToLowerInvariant().Contains(query) == true) ||
                    (s.SessionId?.ToLowerInvariant().Contains(query) == true));

            foreach (var session in filtered)
            {
                Sessions.Add(session);
            }

            OnPropertyChanged(nameof(HasNoSessions));
            OnPropertyChanged(nameof(HasSessions));
        }
        catch
        {
            // Ignore filter errors
        }
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
            SessionState.Running => "Running",
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
    Running,
    Ending,
    Completed,
    Error
}
