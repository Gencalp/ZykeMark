using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ZykeMark.App.Cli.Collectors;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Collectors;
using ZykeMark.Infrastructure.PresentMon;
using ZykeMark.Infrastructure.Reporting;
using ZykeMark.Infrastructure.Telemetry;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "start":
    {
        var gameName = GetOptionValue(args, "--game");
        var buildVersion = GetOptionValue(args, "--build");

        var store = new FileSystemLocalStore();
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession(gameName, buildVersion, new RunConfig());

        Console.WriteLine($"SessionId: {metadata.SessionId}");
        Console.WriteLine($"SessionFolder: {Path.Combine(GetRootPath(), metadata.SessionId)}");
        return;
    }
    case "end":
    {
        var store = new FileSystemLocalStore();
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var latestSessionId = TryGetLatestSessionId();

        if (string.IsNullOrWhiteSpace(latestSessionId))
        {
            Console.WriteLine("No session found to end.");
            return;
        }

        var summaryPath = sessionManager.StopSession(latestSessionId);
        if (SummaryHasNoSamples(summaryPath))
        {
            Console.WriteLine("No samples collected for this session.");
        }
        Console.WriteLine($"Summary: {summaryPath}");
        return;
    }
    case "status":
    {
        var metadata = TryGetLatestMetadata();

        if (metadata is null)
        {
            Console.WriteLine("No sessions found.");
            return;
        }

        Console.WriteLine($"SessionId: {metadata.SessionId}");
        Console.WriteLine($"StartedAtUtc: {metadata.StartedAtUtc:O}");
        Console.WriteLine($"EndedAtUtc: {(metadata.EndedAtUtc.HasValue ? metadata.EndedAtUtc.Value.ToString("O") : "(running)")}");
        return;
    }
    case "demo-run":
    {
        var secondsValue = GetOptionValue(args, "--seconds");
        var seedValue = GetOptionValue(args, "--seed");
        var gameName = GetOptionValue(args, "--game");
        var buildVersion = GetOptionValue(args, "--build");

        var durationSeconds = 15.0;
        if (!string.IsNullOrWhiteSpace(secondsValue) && !double.TryParse(secondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
        {
            Console.WriteLine("Invalid value for --seconds.");
            return;
        }

        var seed = 123;
        if (!string.IsNullOrWhiteSpace(seedValue) && !int.TryParse(seedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
        {
            Console.WriteLine("Invalid value for --seed.");
            return;
        }

        var store = new FileSystemLocalStore();
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession(gameName, buildVersion, new RunConfig());

        var framePattern = new[] { 16.67, 16.67, 16.67, 33.3, 16.67, 20.0 };
        var collector = new SimulatedCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            seed,
            framePattern,
            cpuFrameTimeChance: 0.7,
            gpuFrameTimeChance: 0.7);

        collector.Collect(TimeSpan.FromSeconds(durationSeconds));
        var summaryPath = sessionManager.StopSession(metadata.SessionId);
        if (SummaryHasNoSamples(summaryPath))
        {
            Console.WriteLine("No samples collected for this session.");
        }

        Console.WriteLine($"SessionFolder: {Path.Combine(GetRootPath(), metadata.SessionId)}");
        Console.WriteLine($"Summary: {summaryPath}");
        return;
    }
    case "real-run":
    {
        var secondsValue = GetOptionValue(args, "--seconds");
        var gameName = GetOptionValue(args, "--game");
        var buildVersion = GetOptionValue(args, "--build");
        var processName = GetOptionValue(args, "--process_name");
        var processIdValue = GetOptionValue(args, "--process_id");
        var presentMonPath = GetOptionValue(args, "--presentmon-path");

        var durationSeconds = 15.0;
        if (!string.IsNullOrWhiteSpace(secondsValue) && !double.TryParse(secondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
        {
            Console.WriteLine("Invalid value for --seconds.");
            return;
        }

        int? processId = null;
        if (!string.IsNullOrWhiteSpace(processIdValue))
        {
            if (!int.TryParse(processIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedProcessId))
            {
                Console.WriteLine("Invalid value for --process_id.");
                return;
            }

            processId = parsedProcessId;
        }

        if (string.IsNullOrWhiteSpace(processName) && processId is null)
        {
            Console.WriteLine("--process_name or --process_id is required.");
            return;
        }

        var store = new FileSystemLocalStore();
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession(gameName, buildVersion, new RunConfig());

        // Get session folder path
        var sessionFolder = Path.Combine(GetRootPath(), metadata.SessionId);

        // Create logger that outputs to console for PresentMon diagnostics
        void Log(string message) => Console.WriteLine(message);

        // Resolve process ID for telemetry (required for Windows telemetry sampling)
        var telemetryPid = processId ?? ResolveProcessId(processName);
        
        // Start telemetry sampler if we have a valid PID (Windows only)
        WindowsTelemetrySampler? telemetrySampler = null;
        if (OperatingSystem.IsWindows() && telemetryPid.HasValue)
        {
            try
            {
                telemetrySampler = new WindowsTelemetrySampler(Log);
                telemetrySampler.Start(telemetryPid.Value);
                Log($"Telemetry sampler started for PID {telemetryPid.Value}");
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not start telemetry sampler: {ex.Message}");
                telemetrySampler?.Dispose();
                telemetrySampler = null;
            }
        }
        else if (telemetryPid is null)
        {
            Log("Warning: Could not resolve process ID for telemetry sampling. Telemetry will be unavailable.");
        }

        var runOptions = new PresentMonRunOptions(presentMonPath, processName, processId, (int)Math.Ceiling(durationSeconds));
        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            new PresentMonRunner(Log),
            new PresentMonCsvParser(),
            runOptions,
            sessionFolder,
            Log,
            telemetrySampler);

        try
        {
            collector.Collect(TimeSpan.FromSeconds(durationSeconds));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException is System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("PresentMon may require administrator privileges.");
            }
            return;
        }
        finally
        {
            // Stop and dispose telemetry sampler
            if (OperatingSystem.IsWindows() && telemetrySampler != null)
            {
                telemetrySampler.Stop();
                var telemetrySampleCount = telemetrySampler.GetSamples().Count;
                Log($"Telemetry sampler stopped. Collected {telemetrySampleCount} samples.");
                telemetrySampler.Dispose();
            }
        }

        var summaryPath = sessionManager.StopSession(metadata.SessionId, collector.LastCollectionDataQuality);
        if (SummaryHasNoSamples(summaryPath))
        {
            Console.WriteLine("No samples collected for this session.");
        }

        Console.WriteLine($"SessionFolder: {sessionFolder}");
        Console.WriteLine($"Summary: {summaryPath}");
        return;
    }
    case "export-pdf":
    {
        var sessionId = GetOptionValue(args, "--sessionId");
        var sessionPath = GetOptionValue(args, "--sessionPath");
        var outputPath = GetOptionValue(args, "--out");

        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(sessionPath))
        {
            Console.WriteLine("--sessionId or --sessionPath is required.");
            return;
        }

        var resolvedSessionPath = !string.IsNullOrWhiteSpace(sessionPath)
            ? sessionPath
            : Path.Combine(GetRootPath(), sessionId!);

        try
        {
            var tokensPath = ReportGenerator.FindBrandTokensPath();
            var theme = BrandTheme.LoadFromTokens(tokensPath);
            var generator = new ReportGenerator(theme);
            var pdfPath = generator.Generate(resolvedSessionPath, outputPath);
            Console.WriteLine($"PDF: {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }

        return;
    }
    case "test-telemetry":
    {
        // CLI command to test WindowsTelemetrySampler independently
        var processIdValue = GetOptionValue(args, "--process_id");
        var processName = GetOptionValue(args, "--process_name");
        var secondsValue = GetOptionValue(args, "--seconds");

        var durationSeconds = 3.0;
        if (!string.IsNullOrWhiteSpace(secondsValue) && !double.TryParse(secondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds))
        {
            Console.WriteLine("Invalid value for --seconds.");
            return;
        }

        int? processId = null;
        if (!string.IsNullOrWhiteSpace(processIdValue))
        {
            if (!int.TryParse(processIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedProcessId))
            {
                Console.WriteLine("Invalid value for --process_id.");
                return;
            }
            processId = parsedProcessId;
        }

        // If only process name is provided, resolve to PID
        if (processId is null && !string.IsNullOrWhiteSpace(processName))
        {
            processId = ResolveProcessId(processName);
            if (processId is null)
            {
                Console.WriteLine($"Could not find process with name: {processName}");
                return;
            }
            Console.WriteLine($"Resolved process '{processName}' to PID {processId}");
        }

        if (processId is null)
        {
            Console.WriteLine("--process_id or --process_name is required.");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("test-telemetry is only supported on Windows.");
            return;
        }

        Console.WriteLine($"Testing telemetry sampler for PID {processId} for {durationSeconds} seconds...");
        
        try
        {
            using var sampler = new WindowsTelemetrySampler(msg => Console.WriteLine(msg));
            sampler.Start(processId.Value);
            
            // Wait for samples
            Thread.Sleep(TimeSpan.FromSeconds(durationSeconds));
            
            sampler.Stop();
            
            var samples = sampler.GetSamples();
            Console.WriteLine($"\nCollected {samples.Count} telemetry samples:");
            
            foreach (var sample in samples.Take(5))
            {
                Console.WriteLine($"  CPU={sample.CpuProcessPercent:F1}%, TopThread={sample.TopThreadCpuPercent:F1}%, WS={sample.RamWorkingSetMB:F1}MB, Private={sample.RamPrivateBytesMB:F1}MB, DiskRead={sample.DiskReadMBps:F2}MB/s, GPU={sample.GpuUtilizationPercent?.ToString("F1") ?? "n/a"}%");
            }
            
            if (samples.Count > 5)
            {
                Console.WriteLine($"  ... and {samples.Count - 5} more samples");
            }
            
            // Check if core metrics are non-null
            var hasWorkingSet = samples.Any(s => s.RamWorkingSetMB.HasValue);
            var hasPrivate = samples.Any(s => s.RamPrivateBytesMB.HasValue);
            var hasCpu = samples.Any(s => s.CpuProcessPercent.HasValue);
            var hasDisk = samples.Any(s => s.DiskReadMBps.HasValue);
            
            Console.WriteLine($"\nMetric availability:");
            Console.WriteLine($"  RamWorkingSetMB: {(hasWorkingSet ? "OK" : "MISSING")}");
            Console.WriteLine($"  RamPrivateBytesMB: {(hasPrivate ? "OK" : "MISSING")}");
            Console.WriteLine($"  CpuProcessPercent: {(hasCpu ? "OK" : "MISSING")}");
            Console.WriteLine($"  DiskReadMBps: {(hasDisk ? "OK" : "MISSING")}");
            Console.WriteLine($"  GPU telemetry available: {sampler.IsGpuTelemetryAvailable}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        
        return;
    }
    default:
        Console.WriteLine("Unknown command.");
        PrintUsage();
        return;
}

static void PrintUsage()
{
    Console.WriteLine("ZykeMark CLI");
    Console.WriteLine("Commands:");
    Console.WriteLine("  zykemark start --game \"MyGame\" --build \"1.0.0\"");
    Console.WriteLine("  zykemark end");
    Console.WriteLine("  zykemark status");
    Console.WriteLine("  zykemark demo-run --seconds 15 --seed 123");
    Console.WriteLine("  zykemark real-run --process_name \"MyGame.exe\" --seconds 15 --presentmon-path \"C:\\\\tools\\\\PresentMon.exe\"");
    Console.WriteLine("  zykemark export-pdf --sessionId <id> [--out \"C:\\\\path\\\\report.pdf\"]");
    Console.WriteLine("  zykemark test-telemetry --process_id <pid> [--seconds 3]");
}

static string? GetOptionValue(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Length)
        {
            return arguments[i + 1];
        }
    }

    return null;
}

static string GetRootPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ZykeMark",
    "sessions");

static bool SummaryHasNoSamples(string summaryPath)
{
    try
    {
        using var stream = File.OpenRead(summaryPath);
        using var document = JsonDocument.Parse(stream);
        var aggregates = document.RootElement.GetProperty("aggregates");
        return aggregates.GetProperty("FrameCount").GetInt32() == 0;
    }
    catch (Exception)
    {
        return false;
    }
}

static string? TryGetLatestSessionId()
{
    var metadata = TryGetLatestMetadata();
    return metadata?.SessionId;
}

static SessionMetadata? TryGetLatestMetadata()
{
    var rootPath = GetRootPath();

    if (!Directory.Exists(rootPath))
    {
        return null;
    }

    var serializerOptions = new JsonSerializerOptions();
    SessionMetadata? latest = null;

    foreach (var directory in Directory.GetDirectories(rootPath))
    {
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            continue;
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            var metadata = JsonSerializer.Deserialize<SessionMetadata>(stream, serializerOptions);
            if (metadata is null)
            {
                continue;
            }

            if (latest is null || metadata.StartedAtUtc > latest.StartedAtUtc)
            {
                latest = metadata;
            }
        }
        catch (JsonException)
        {
            continue;
        }
    }

    return latest;
}

static int? ResolveProcessId(string? processName)
{
    if (string.IsNullOrWhiteSpace(processName))
    {
        return null;
    }

    try
    {
        // Remove .exe extension if present for Process.GetProcessesByName
        var searchName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var processes = System.Diagnostics.Process.GetProcessesByName(searchName);
        if (processes.Length > 0)
        {
            return processes[0].Id;
        }
    }
    catch (ArgumentException)
    {
        // Invalid process name
    }
    catch (InvalidOperationException)
    {
        // Process query failed
    }

    return null;
}
