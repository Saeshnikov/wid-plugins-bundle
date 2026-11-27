# WidBar Plugins Bundle

- [Happ Control](widgets/happ/README.md) — shows the local Happ core status and lets you connect or disconnect.
- [GitHub Activity](widgets/github/README.md) — shows pull requests, review requests, and failed commit statuses.
- [Codex Limits](widgets/codex/README.md) — shows remaining Codex quota and a countdown ring for each reset window.

Widget source lives in `widgets/*/`. The `Personal.*` projects generated beside the scripts, their build output, and the local template checkout are not tracked.

## Build from a clean checkout

Use Windows with Git, .NET SDK 10.0.102 (or a newer patch in the same feature band), and Visual Studio or Build Tools with MSBuild, Windows application packaging support, and the Windows SDK 10.0.26100.0. Run from the repository root in Developer PowerShell for Visual Studio.

```powershell
# Cache and install the pinned WidBar template, then generate the GitHub project.
.\widgets\github\New-Widget.ps1

# Generate the Happ project from the same installed template.
dotnet new widbar-widget -n Personal.Happ --PluginId com.personal.happ --DisplayName 'Happ Control' -o .\widgets\happ\Personal.Happ
Copy-Item .\widgets\happ\MainPlugin.cs .\widgets\happ\Personal.Happ\Personal.Happ.ExtensionApp\MainPlugin.cs

msbuild '.\widgets\github\Personal.GitHub\Personal.GitHub (Package)\Personal.GitHub (Package).wapproj' /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never
msbuild '.\widgets\happ\Personal.Happ\Personal.Happ (Package)\Personal.Happ (Package).wapproj' /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never

.\widgets\codex\Install-Widget.ps1 -BuildOnly
```

These commands build but do not register the packages. No GitHub credentials, running Happ instance, or Codex sign-in are needed for compilation. Use the widget scripts for deployment: they put the generated `plugin.json` and attribution files in the loose package layout before registering it. The widgets deploy under `%LOCALAPPDATA%\WidBarWidgets`, independently of the repository location.

If PowerShell blocks a script extracted from a downloaded archive, review the script and unblock that specific file with `Unblock-File -LiteralPath <script-path>` if you trust it.

## Licensing

The standalone project scaffold comes from [WidBar Widget Templates at `edd1e46`](https://github.com/andelby/widbar-widget-template/tree/edd1e46fcdb1c2edac8707d37b093cc271bdd126). Its [MIT license](licenses/widbar-widget-template.MIT.txt) is preserved. The third-party icon notices are kept separately for [Happ Control](widgets/happ/THIRD-PARTY-NOTICES.txt) and [GitHub Activity](widgets/github/THIRD-PARTY-NOTICES.txt). Keep the relevant notices and template license with any distributed copy that includes those components. These notices do not license the original widget code; no license for that code is specified here.
