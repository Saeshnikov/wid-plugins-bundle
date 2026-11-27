[CmdletBinding()]
param(
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot 'Personal.GitHub'),
    [string]$MSBuildPath = 'A:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProjectDirectory -PathType Container)) {
    throw "Generated project not found: $ProjectDirectory. Run New-Widget.ps1 or pass -ProjectDirectory pointing to a generated Personal.GitHub project."
}
$ProjectDirectory = (Resolve-Path -LiteralPath $ProjectDirectory).Path
if (-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSBuild not found. Pass -MSBuildPath explicitly.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) { throw 'MSBuild not found.' }
}
$projects = @(Get-ChildItem -LiteralPath $ProjectDirectory -Recurse -Filter '*.wapproj' -File)
if ($projects.Count -ne 1) { throw "Expected one packaging project; found $($projects.Count)." }
$project = $projects[0].FullName
$output = Join-Path (Split-Path -Parent $project) 'bin\x64\Release'
$destination = Join-Path $env:LOCALAPPDATA 'WidBarWidgets\GitHub'
foreach ($package in @(Get-AppxPackage -Name 'Personal.GitHub.Widget')) {
    if (-not $package.InstallLocation -or
        [IO.Path]::GetFullPath($package.InstallLocation).TrimEnd('\') -ine $destination) {
        throw "GitHub Activity is registered at '$($package.InstallLocation)', not '$destination'. Migrate the development package before updating. No files changed."
    }
}
$extension = Join-Path $ProjectDirectory 'Personal.GitHub.ExtensionApp'
$target = Join-Path $extension 'MainPlugin.cs'
$source = Join-Path $PSScriptRoot 'MainPlugin.cs'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Existing plugin source not found: $target" }
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Update source not found: $source" }
$notices = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'
$templateLicense = Join-Path $PSScriptRoot '..\..\licenses\widbar-widget-template.MIT.txt'
foreach ($file in @($notices, $templateLicense)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required attribution file missing: $file" }
}
# Stop only our extension process before replacing the deployed executable.
$running = @(Get-Process -Name 'Personal.GitHub.ExtensionApp' -ErrorAction SilentlyContinue)
foreach ($proc in $running) {
    if (-not $proc.HasExited) {
        Write-Host "Stopping widget process $($proc.Id)..."
        Stop-Process -InputObject $proc -Force -ErrorAction Stop
        if (-not $proc.WaitForExit(10000)) { throw 'Widget process did not exit.' }
    }
}
Start-Sleep -Seconds 2
if (Get-Process -Name 'Personal.GitHub.ExtensionApp' -ErrorAction SilentlyContinue) {
    throw 'WidBar restarted the widget. Exit WidBar from its tray menu and rerun this script.'
}
$backup = "$target.previous.$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').bak"
Copy-Item -LiteralPath $target -Destination $backup
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Source backup: $backup"
Push-Location $ProjectDirectory
try {
    & $MSBuildPath $project /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). The source backup is $backup. No registration attempted." }
} finally { Pop-Location }
$manifest = Join-Path $output 'AppxManifest.xml'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Expected build manifest not found: $manifest" }
[xml]$xml = Get-Content -LiteralPath $manifest -Raw
$identity = $xml.SelectSingleNode("//*[local-name()='Identity']")
$ext = $xml.SelectSingleNode("//*[local-name()='AppExtension' and @Name='com.widbar.widget']")
if (-not $identity -or $identity.GetAttribute('Name') -ne 'Personal.GitHub.Widget' -or -not $ext) { throw 'Unexpected package identity or extension. No registration attempted.' }
$pluginId = $ext.SelectSingleNode(".//*[local-name()='WidBarPluginId']")
if (-not $pluginId -or $pluginId.InnerText -ne 'com.personal.github') { throw 'Unexpected plugin ID. No registration attempted.' }
$public = Join-Path $output 'Public'
$generated = Join-Path $extension 'obj\widbar\plugin.json'
if (-not (Test-Path -LiteralPath $generated -PathType Leaf)) { throw "Generated plugin.json not found: $generated. No registration attempted." }
$null = Get-Content -LiteralPath $generated -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $public -Force | Out-Null
Copy-Item -LiteralPath $generated -Destination (Join-Path $public 'plugin.json') -Force
Copy-Item -LiteralPath @($notices, $templateLicense) -Destination $output -Force
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $output -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
}
Add-AppxPackage -Register (Join-Path $destination 'AppxManifest.xml') -ErrorAction Stop
$registered = Get-AppxPackage -Name 'Personal.GitHub.Widget'
if (-not $registered -or $registered.Status -ne 'Ok' -or $registered.InstallLocation -ine $destination) {
    throw 'Registration verification failed.'
}
$registered | Select-Object Name, Version, Status, InstallLocation
Write-Host 'Done. Restart WidBar and refresh Layout.'
