# Capture Reliability Notes

These are the main runtime issues I hit while making real Windows capture usable.

## PresentMon exited successfully but produced no CSV

### Symptom

PresentMon printed normal start/stop output and returned exit code `0`, but no CSV file was written.

### Cause

`.NET Process.ProcessName` returns names such as `chrome`, while PresentMon process-name matching expects the executable name, such as `chrome.exe`.

### Fix

Process names are normalized before PresentMon arguments are built. The capture path also checks that the expected CSV was actually created instead of trusting the process exit code alone.

Regression tests cover names with and without `.exe` and the argument-building paths that use process-name targeting.

## One PID was not enough for browsers and Electron apps

### Symptom

The selected process was valid, but frame capture returned no useful samples for some applications.

### Cause

Browsers and Electron apps split work across multiple processes. The selected PID may not be the process that presents frames.

### Fix

Known multi-process targets can use process-name capture so PresentMon sees the related child processes. GPU telemetry matching also builds a set of matching PIDs instead of assuming one static PID.

## ETW state survived failed runs

### Symptom

PresentMon sometimes failed with ETW error `1450`, and later captures could continue failing after an interrupted run.

### Cause

A stale ETW session could outlive the PresentMon process that created it.

### Fix

ZykeMark can discover and stop stale ZykeMark-related ETW sessions before capture. Error `1450` triggers cleanup and one retry. A watchdog also stops a capture process that hangs past the expected duration.

The cleanup/retry information is written to `capture_diagnostics.json`.

## Telemetry was attached to the wrong frames

### Symptom

CPU/GPU telemetry was collected, but multiple frames ended up with the same late telemetry sample.

### Cause

The earlier code asked for the latest telemetry value after frame capture instead of matching samples by time.

### Fix

Telemetry samples now carry relative timestamps. Samples are kept in order and the collector resolves the closest sample for each frame timestamp.

This path is tested with fake telemetry samplers, so the correlation logic can be verified without Windows performance counters.

## Diagnostics

Machine-specific failures were difficult to reproduce from the UI alone, so capture diagnostics are written into the session folder.

Depending on the failure, the diagnostic files can include:

- resolved PresentMon path
- generated command-line arguments
- exit code, stdout and stderr
- CSV path, size and parser information
- ETW cleanup/retry activity
- PID matching details for telemetry

The idea is simple: when a capture fails, the session should contain enough information to debug it later.

## Agent-assisted development

Some implementation and test work was done with coding agents, mainly Copilot/Codex-style workflows. I used them for narrow changes, test generation and code review, then reproduced the relevant runtime path on Windows before keeping the change.

The useful pattern for this project was:

1. reproduce the failure
2. narrow it to one subsystem
3. inspect the relevant code and diagnostics
4. make one focused change
5. add a regression test when possible
6. run the same path again on Windows
