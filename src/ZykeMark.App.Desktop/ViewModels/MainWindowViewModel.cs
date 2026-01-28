using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ZykeMark.App.Desktop.Commands;
using ZykeMark.App.Desktop.Services;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;

namespace ZykeMark.App.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SessionOrchestrationService _service;
    private readonly DispatcherTimer _durationTimer;
    private readonly List<FrameSample> _samples = new();
    private SessionMetadata? _metadata;
    private DateTime? _startUtc;
    private SessionState _state = SessionState.Idle;
    private string _statusMessage = string.Empty;
    private string _lastError = string.Empty;
    private string _processName = string.Empty;
    private string _buildVersion = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel()
    {
        _service = new SessionOrchestrationService();
        _service.ChunkReceived += OnChunkReceived;
        _service.Error += OnServiceError;

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDuration();

        StartCommand = new RelayCommand(StartSession, () => State is SessionState.Idle or SessionState.Completed or SessionState.Error);
        EndCommand = new RelayCommand(EndSession, () => State == SessionState.Running);
        ExportPdfCommand = new RelayCommand(ExportPdf, () => !string.IsNullOrWhiteSpace(SessionFolder));
        OpenFolderCommand = new RelayCommand(() => _service.OpenSessionFolder(), () => !string.IsNullOrWhiteSpace(SessionFolder));
        CopyErrorCommand = new RelayCommand(CopyErrorDetails, () => !string.IsNullOrWhiteSpace(LastError));

        StatusText = "Idle";
        DurationText = "00:00:00";
        VerdictSummary = "Awaiting session.";
        NextStepsSummary = "Start a session to see recommendations.";
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand EndCommand { get; }
    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyErrorCommand { get; }

    public string StatusText { get; private set; } = string.Empty;
    public string DurationText { get; private set; } = string.Empty;
    public string SessionFolder => _service.CurrentSessionFolder ?? "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetField(ref _lastError, value);
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
                ExportPdfCommand.RaiseCanExecuteChanged();
                OpenFolderCommand.RaiseCanExecuteChanged();
            }
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
        }
        catch (Exception ex)
        {
            HandleError("Failed to stop session.", ex);
            State = SessionState.Error;
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
