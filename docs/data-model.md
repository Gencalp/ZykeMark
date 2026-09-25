# ZykeMark Data Model

## Session

A benchmark is represented by `SessionMetadata` and identified by a `SessionId`.

Key fields include:

- `StartedAtUtc`
- `EndedAtUtc`
- `DurationMs`
- `GameName`
- `BuildVersion`
- `RunConfig`
- `CaptureTarget`

`RunConfig` stores optional run context such as rendering API, resolution and preset.

`CaptureTarget` stores the process-oriented information used by the capture pipeline, including process name/PID selection and whether the application should be treated as multi-process.

## Frame samples

Frame data is collected from PresentMon and stored as frame samples/chunks. The important output is frame timing rather than a generic FPS counter, which allows the aggregator to compute distribution-sensitive metrics.

## Telemetry samples

Windows telemetry samples can contain:

- CPU process usage
- top-thread CPU usage
- working-set and private memory
- disk read throughput
- GPU utilization when counters are available
- dedicated/shared VRAM when counters are available
- a relative timestamp used to correlate telemetry with frame samples

Telemetry is intentionally nullable because counter availability varies by process, hardware and Windows environment.

## Aggregates

`SessionAggregates` currently includes:

- frame count
- duration
- average FPS
- average frame time
- P99 frame time
- 1% low FPS
- 0.1% low FPS
- optional CPU/GPU frame-time averages
- optional CPU, GPU, RAM, VRAM and disk telemetry averages

## Data quality

Capture quality is tracked separately from the numeric aggregates. A session can therefore preserve partial/diagnostic information instead of pretending missing telemetry is a valid zero value.

## Local persistence

Session artifacts are written under:

```text
%LOCALAPPDATA%\ZykeMark\sessions\<session-id>
```

Depending on the workflow, a session can contain metadata, frame chunks, a summary, diagnostics and generated reports.

Fixture CSV files under `tests/fixtures` cover multiple PresentMon header/time variants so parser behavior is reproducible in automated tests.
