# Engineering Notes: Making Real Capture Reliable

This document records the debugging path behind several of ZykeMark's more interesting engineering decisions. The goal is not to present the project as finished product infrastructure, but to show why the current reliability code exists.

## 1. PresentMon reported success but produced no data

### Symptom

A capture could show PresentMon starting and stopping normally and return exit code `0`, while no CSV was written.

### Root cause

`.NET`'s `Process.ProcessName` returns a value such as `chrome`, while PresentMon process-name matching expects the executable name such as `chrome.exe`.

That mismatch did not surface as a conventional process failure, which made the problem easy to misdiagnose.

### Fix

Process names passed to PresentMon are normalized before argument construction. Tests cover names with and without an existing `.exe` suffix as well as the argument-building paths that use process-name targeting.

### Lesson

An external tool exiting successfully is not enough to declare the operation successful. The application also validates the expected output artifact and records diagnostics when it is missing.

---

## 2. A single PID was not enough for browsers and Electron apps

### Symptom

Some applications were detected correctly but frame capture returned no useful samples.

### Root cause

Browsers and Electron applications commonly split work across multiple processes. The process selected by the user may not be the process presenting the frames.

### Fix

`CaptureTarget` identifies selected multi-process application families. Those targets can prefer process-name capture so PresentMon sees relevant child processes rather than only one PID.

GPU telemetry PID matching also builds a set of matching process IDs instead of assuming a single static PID.

### Lesson

Process identity is part of the domain model. Treating every application as `one app = one PID` made the capture abstraction too weak.

---

## 3. ETW state survived failed runs

### Symptom

PresentMon could fail with ETW-related error `1450`, and subsequent captures could remain unreliable after a failed or interrupted run.

### Root cause

Stale ETW sessions could survive longer than the capture process that created them.

### Fix

ZykeMark added an ETW session manager that can discover and stop stale ZykeMark-related sessions before capture. Selected failures trigger cleanup and one controlled retry. A watchdog also prevents a capture process from hanging indefinitely.

Cleanup activity is written into capture diagnostics so a retry is observable rather than silent.

### Lesson

Retrying without resetting external state only repeats the same failure. Recovery needed to explicitly clean the resource that survived the previous attempt.

---

## 4. Telemetry existed, but was attached to the wrong frames

### Symptom

CPU/GPU telemetry could be collected successfully while session output still showed misleading values because multiple frames were associated with the same late telemetry sample.

### Root cause

The earlier workflow effectively asked for the latest telemetry value after frame capture instead of correlating samples by time.

### Fix

Telemetry samples gained relative timestamps. The sampler keeps a history and resolves the sample closest to each frame timestamp. Lookup is implemented with an ordered sample set rather than applying one final value to the entire run.

Tests use fake telemetry samplers to verify the attachment behavior independently from Windows counters.

### Lesson

Collecting two correct streams does not mean their combined result is correct. Correlation has to be part of the data model and test strategy.

---

## 5. Diagnostics became a feature, not just logging

A repeated problem during development was that runtime capture bugs were machine-specific. Reproducing the exact state was slower than inspecting the failed run.

ZykeMark therefore writes structured diagnostic data for the capture pipeline, including relevant executable/argument information, process output, CSV discovery and parsing details, ETW recovery actions and telemetry matching context.

The design goal is simple: a failed session should leave enough evidence to understand what failed and where.

## How coding agents were used

Coding agents were used throughout development for implementation, test generation, code review and focused debugging tasks. The workflow was iterative rather than one-shot:

1. reproduce or isolate the failure
2. write down the concrete runtime symptom
3. ask the agent to inspect the narrow subsystem involved
4. review the proposed change and its assumptions
5. add or update tests around the failure
6. run the relevant path again on Windows
7. keep the change only when the observed failure was actually resolved

Several fixes in the repository originated from agent-assisted branches, but the important artifact is the debugging trail: symptoms were converted into specific hypotheses, instrumentation and regression tests rather than accepted on the basis of generated code alone.
