# Codex Limits

A compact WidBar widget for the main Codex quota on a ChatGPT account. Each available window has one gauge: the number is the **percentage of quota remaining**; the ring is the **fraction of the current window left until reset**. Shorter windows appear first. Hover over a gauge to see its window and the percentage with the `%` sign.

Usage updates every minute, and again when a displayed reset is reached. The rings update locally each second. A percentage without a ring means reset progress is unavailable; `—` means no quota data. At reset, the widget waits for reported usage rather than assuming the quota has renewed.

The widget uses the local [Codex App Server](https://developers.openai.com/codex/app-server#6-rate-limits-chatgpt) over stdio. It first looks for the registered Codex Desktop package on Windows, then for a standalone `codex.exe` on `PATH`. Sign in to Codex with ChatGPT. The widget does not request inference, read credentials, or show limits for ordinary ChatGPT messages.

## Build or install

Follow the [root README](../../README.md#build-from-a-clean-checkout) to set up the tools and install the pinned template. From the repository root:

```powershell
# Build and stage, without registering:
.\widgets\codex\Install-Widget.ps1 -BuildOnly

# Install or update after exiting WidBar from its tray menu:
.\widgets\codex\Install-Widget.ps1
```

If needed, pass `-MSBuildPath` with the full path to `MSBuild.exe`. The installer registers only `Personal.Codex.Widget` from `%LOCALAPPDATA%\WidBarWidgets\Codex`. Restart WidBar and add **Codex Limits** in **Layout**.

## Checks

Run `dotnet run --project widgets/codex/tests/CodexUsage.Tests.csproj` to check quota parsing, countdown math and local protocol behavior without a Codex account.

In WidBar, verify that one or two windows fit without clipping and agree with the Codex usage dashboard; an unavailable source shows `—`; a missing reset leaves the percentage without a ring; and a real reset refreshes usage without jumping to an assumed `100%`. Verify Happ and GitHub widgets still behave the same. Building the package does not verify these in-host checks.

The Blossom icon is an OpenAI mark, sourced from the [OpenAI brand kit](https://cdn.openai.com/brand/OpenAI-Logos-2025.zip). This is an unofficial widget.
