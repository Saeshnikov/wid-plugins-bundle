# Codex Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Windows WidBar widget that shows remaining Codex usage as a percentage and time to reset as a draining ring.

**Architecture:** A UI-independent reader obtains a snapshot from Codex App Server over stdio. The WinUI widget polls it on a background loop and renders reset progress on the UI thread from the last confirmed snapshot.

**Tech Stack:** C#, .NET SDK 10.0.102, WinUI, PowerShell, pinned WidBar template, Codex App Server JSON-RPC.

**Spec:** [Codex Limits design](../specs/2026-11-27-codex-limits-design.md)

This is the original implementation checklist. For current behavior and deployment commands, see the [widget README](../../../widgets/codex/README.md).

## Global Constraints

- Main Codex quota only. No inference calls, cookie or token-file reads, or changes to the Happ and GitHub widgets.
- One indicator per available window, shorter first. Usage refresh every minute; local ring updates; no textual countdown.
- Maintain the existing generated-project convention; don't register the package as part of build verification.

## Review Focus

- A single valid window must render without inventing a second; two windows must be ordered by duration.
- A missing reset or duration must not turn a valid percentage into an unknown percentage.
- An elapsed reset must not produce a fake `100%` allowance.
- An unrelated JSON-RPC notification must not displace the requested result.
- An unavailable source or a child process that never replies must not block WidBar or leave a process behind.

## Task 1: Read and calculate the Codex quota

**Files:** Create `widgets/codex/CodexUsage.cs`, `widgets/codex/tests/CodexUsage.Tests.csproj`, `widgets/codex/tests/Program.cs`.

**Interfaces:** `QuotaWindow.RemainingPercent`, `QuotaWindow.WindowMinutes`, `QuotaWindow.ResetsAt`, `QuotaWindow.RemainingTimeFraction(DateTimeOffset now)`, `CodexUsage.ParseQuota(JsonElement result)`, `CodexUsage.ReadAsync(string executablePath, CancellationToken token)`.

- [ ] Add dependency-free console tests using fixture JSON and a fake stdio child: assert `usedPercent:25` gives `75`; 5-hour sorts before weekly; null secondary yields one window; null duration/reset yields a null fraction; a five-hour window with 2.5 hours left gives `0.5`, and at expiry gives `0` without changing the usage percentage. Assert a notification before the matching response is ignored, an RPC error fails, and a non-responding child is terminated on cancellation.
- [ ] Run `dotnet run --project widgets/codex/tests/CodexUsage.Tests.csproj`; confirm the initial failure is from the missing implementation.
- [ ] Implement the reader via `initialize` → `initialized` → `account/rateLimits/read`. Match response IDs; prefer `rateLimitsByLimitId.codex`, with `rateLimits` as fallback only when it represents the main Codex quota. Treat malformed or unavailable usage as unknown. Redirect stdio, hide the window, bound requests, and terminate the owned child on cancellation or timeout. Do not log raw protocol messages.
- [ ] Rerun the console tests; require exit 0. Smoke-test the installed executable with the real account, recording only success/failure and the number of usable windows, not raw responses.

## Task 2: Render the preview and package the widget

**Files:** Create `widgets/codex/MainPlugin.cs`, `widgets/codex/Install-Widget.ps1`, `widgets/codex/README.md`; update `.gitignore` and root `README.md`.

**Interfaces:** `MainPlugin : WidgetPluginBase`, ID `com.personal.codex`, name `Codex Limits`, package `Personal.Codex.Widget`.

- [ ] Follow `widgets/happ/MainPlugin.cs` for WinUI preview lifecycle and `widgets/happ/Install-Widget.ps1` for the verified loose-package layout. Resolve the desktop executable from the registered `OpenAI.Codex` package, with documented fallback for a standalone `codex.exe`. Show a status tooltip for an unavailable source.
- [ ] Render percentage labels over determinate rings. Update rings on a UI timer without querying Codex on every tick. Refresh in the background every 60 seconds and once when a displayed reset expires; no concurrent polls. Show `—` on source failure, and cancel timers/requests when disposed.
- [ ] Copy `MainPlugin.cs` and `CodexUsage.cs` into the generated extension project. Deployment registers only the new widget under `%LOCALAPPDATA%\WidBarWidgets\Codex` and preserves the template license. Document build-only and installation commands and keep generated artifacts ignored.
- [ ] Run the console tests again. Build the generated `.wapproj` with MSBuild `/restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never`; require exit 0 and inspect package/plugin identities. Run `git diff --check`.
- [ ] Document in-host acceptance: check one and two windows, circle readability, disabled/unavailable and missing-reset states, refresh at reset, and existing widgets unchanged. Report any in-host checks not run.
