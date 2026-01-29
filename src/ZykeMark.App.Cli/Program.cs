using System.Globalization;
using System.Text.Json;
using ZykeMark.App.Cli.Collectors;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Collectors;
using ZykeMark.Infrastructure.PresentMon;
using ZykeMark.Infrastructure.Reporting;

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

        var runOptions = new PresentMonRunOptions(presentMonPath, processName, processId, (int)Math.Ceiling(durationSeconds));
        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            new PresentMonRunner(Log),
            new PresentMonCsvParser(),
            runOptions,
            sessionFolder,
            Log);

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

        var summaryPath = sessionManager.StopSession(metadata.SessionId);
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
