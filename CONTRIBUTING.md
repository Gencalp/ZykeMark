# Contributing

ZykeMark is primarily a personal engineering project and portfolio repository, but focused issues and pull requests are welcome.

## Before opening a change

For capture or telemetry bugs, please include:

- Windows version
- target application/process
- whether the target is single-process or multi-process
- PresentMon version/path if relevant
- the generated `capture_diagnostics.json` or `telemetry_diagnostics.json` with any sensitive local paths removed
- expected behavior and observed behavior

## Local checks

From the repository root:

```powershell
dotnet restore
dotnet build ZykeMark.sln -c Release
dotnet test ZykeMark.sln -c Release --no-build
```

For Windows-specific capture changes, also exercise the narrow path you changed. The CLI includes `test-telemetry` and `diagnostic-capture` commands to isolate runtime behavior without depending on the full desktop flow.

## Pull requests

Keep changes small enough that the failure mode and fix are easy to review. When fixing a bug, prefer adding a regression test or fixture that reproduces the behavior before the fix.

Please avoid changes intended to bypass anti-cheat, process protection or security controls. That is outside this project's scope.
