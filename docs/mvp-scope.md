# ZykeMark Project Scope

## Current goal

ZykeMark is a Windows-first benchmarking tool for capturing frame-time and process telemetry, storing benchmark sessions locally, computing useful aggregates and generating human-readable reports.

The project is currently best described as a working engineering build rather than a packaged end-user product.

## Implemented

- WPF desktop application
- CLI workflows for simulated and real captures
- PresentMon integration
- process-name and PID targeting
- handling for selected multi-process applications
- Windows CPU, memory, disk and available GPU/VRAM telemetry
- timestamp-based frame/telemetry correlation
- local session persistence
- aggregate benchmark metrics
- capture and telemetry diagnostics
- ETW cleanup/retry handling
- PDF report generation
- automated tests with PresentMon fixture data

## Deliberate constraints

- Windows is the primary supported platform.
- Real frame capture depends on PresentMon.
- Hardware/counter availability varies by machine, so some telemetry fields may be unavailable.
- The repository does not currently ship a signed installer or automatic dependency bootstrapper.
- Broad compatibility validation across game engines, anti-cheat systems and hardware configurations is not complete.

## Next productization steps

The highest-value next steps are:

1. package a repeatable Windows release/installer
2. make first-run PresentMon setup more guided
3. validate capture behavior across a wider set of games and hardware
4. add explicit compatibility notes for applications that restrict telemetry/capture
5. add richer visual summaries and comparison workflows between sessions

## Non-goals for the current repository

- kernel-level instrumentation
- bypassing anti-cheat or process protections
- cross-platform capture parity
- cloud account or multi-user synchronization
- replacing specialist profiling tools

The focus is a transparent local benchmarking workflow with debuggable failure modes, not hidden instrumentation or game modification.
