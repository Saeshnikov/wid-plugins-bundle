[CmdletBinding()]
param(
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot 'Personal.Happ'),
    [string]$MSBuildPath = 'A:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe'
)
$ErrorActionPreference = 'Stop'
# Requires the already installed widbar-widget template and .NET 10 SDK.
$ProjectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
if (-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSBuild not found. Pass -MSBuildPath explicitly.' }
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) { throw 'MSBuild not found.' }
}
$source = Join-Path $PSScriptRoot 'MainPlugin.cs'
$notices = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'
$templateLicense = Join-Path $PSScriptRoot '..\..\licenses\widbar-widget-template.MIT.txt'
foreach ($file in @($source, $notices, $templateLicense)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required repository file missing: $file" }
}
if (-not (Test-Path -LiteralPath $ProjectDirectory)) {
    $null = New-Item -ItemType Directory -Path $ProjectDirectory
    $sdks = @(& dotnet --list-sdks | ForEach-Object { if ($_ -match '^(10\.\d+\.\d+)\s') { [version]$Matches[1] } })
    if ($LASTEXITCODE -ne 0 -or $sdks.Count -eq 0) { throw 'Install .NET 10 SDK first.' }
    $sdk = ($sdks | Sort-Object -Descending | Select-Object -First 1).ToString()
    @{ sdk = @{ version = $sdk; rollForward = 'latestPatch' } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ProjectDirectory 'global.json') -Encoding ASCII
    Push-Location $ProjectDirectory
    try {
        & dotnet new widbar-widget -n Personal.Happ --PluginId com.personal.happ --DisplayName 'Happ Control' -o .
        if ($LASTEXITCODE -ne 0) { throw 'Template generation failed. Check dotnet new list widbar-widget. Remove the incomplete project directory before retrying.' }
    } finally { Pop-Location }
}
$extension = Join-Path $ProjectDirectory 'Personal.Happ.ExtensionApp'
$target = Join-Path $extension 'MainPlugin.cs'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Unexpected template layout. No files replaced.' }
if ((Get-Content -LiteralPath $target -Raw) -notmatch 'namespace\s+Personal\.Happ\.ExtensionApp\s*[;{]') { throw 'Unexpected namespace. No files replaced.' }
$projects = @(Get-ChildItem -LiteralPath $ProjectDirectory -Recurse -Filter '*.wapproj' -File)
if ($projects.Count -ne 1) { throw "Expected one packaging project; found $($projects.Count)." }
$project = $projects[0].FullName
foreach ($proc in @(Get-Process -Name 'Personal.Happ.ExtensionApp' -ErrorAction SilentlyContinue)) {
    if (-not $proc.HasExited) { Stop-Process -InputObject $proc -Force; if (-not $proc.WaitForExit(10000)) { throw 'Widget process did not exit.' } }
}
Start-Sleep -Seconds 1
if (Get-Process -Name 'Personal.Happ.ExtensionApp' -ErrorAction SilentlyContinue) { throw 'Exit WidBar from its tray menu before reinstalling.' }
$backup = "$target.previous.$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').bak"
Copy-Item -LiteralPath $target -Destination $backup
Copy-Item -LiteralPath $source -Destination $target -Force
Push-Location $ProjectDirectory
try {
    & $MSBuildPath $project /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). Backup: $backup. No registration attempted." }
} finally { Pop-Location }
$output = Join-Path (Split-Path -Parent $project) 'bin\x64\Release'
$manifest = Join-Path $output 'AppxManifest.xml'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Manifest not found: $manifest" }
[xml]$xml = Get-Content -LiteralPath $manifest -Raw
$identity = $xml.SelectSingleNode("//*[local-name()='Identity']")
$ext = $xml.SelectSingleNode("//*[local-name()='AppExtension' and @Name='com.widbar.widget']")
if (-not $identity -or $identity.GetAttribute('Name') -ne 'Personal.Happ.Widget' -or -not $ext) { throw 'Unexpected package identity or extension. No registration attempted.' }
$pluginId = $ext.SelectSingleNode(".//*[local-name()='WidBarPluginId']")
if (-not $pluginId -or $pluginId.InnerText -ne 'com.personal.happ') { throw 'Unexpected plugin ID. No registration attempted.' }
$generated = Join-Path $extension 'obj\widbar\plugin.json'
if (-not (Test-Path -LiteralPath $generated -PathType Leaf)) { throw 'Generated plugin.json missing.' }
$null = Get-Content -LiteralPath $generated -Raw | ConvertFrom-Json
$public = Join-Path $output 'Public'
New-Item -ItemType Directory -Path $public -Force | Out-Null
Copy-Item -LiteralPath $generated -Destination (Join-Path $public 'plugin.json') -Force
Copy-Item -LiteralPath @($notices, $templateLicense) -Destination $output -Force
$exe = @(Get-ChildItem -LiteralPath $output -Recurse -File -Filter 'Personal.Happ.ExtensionApp.exe')
if ($exe.Count -ne 1) { throw "Expected one extension executable in package; found $($exe.Count)." }
$helper = Join-Path $exe[0].DirectoryName 'HappProbe.ps1'
# Remove the obsolete helper from previous builds only.
if (Test-Path -LiteralPath $helper -PathType Leaf) { Remove-Item -LiteralPath $helper -Force }
# Register from the stable per-user directory that worked on this Windows installation.
$destination = Join-Path $env:LOCALAPPDATA 'WidBarWidgets\Happ'
$existing = @(Get-AppxPackage -Name 'Personal.Happ.Widget')
foreach ($package in $existing) {
    if (-not $package.InstallLocation) {
        throw 'Previous Happ Control registration has no InstallLocation. Remove only Personal.Happ.Widget and retry.'
    }
    if ([IO.Path]::GetFullPath($package.InstallLocation).TrimEnd('\') -ine [IO.Path]::GetFullPath($destination).TrimEnd('\')) {
        throw 'Happ Control is registered from another directory. Remove only Personal.Happ.Widget before moving it.'
    }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $output -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
}
$installedManifest = Join-Path $destination 'AppxManifest.xml'
$installedExe = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter 'Personal.Happ.ExtensionApp.exe')
if ($installedExe.Count -ne 1) { throw 'Unexpected extension executable count in installation directory.' }
$oldHelper = Join-Path $installedExe[0].DirectoryName 'HappProbe.ps1'
if (Test-Path -LiteralPath $oldHelper -PathType Leaf) { Remove-Item -LiteralPath $oldHelper -Force }
if ((Get-FileHash -LiteralPath $exe[0].FullName).Hash -ne (Get-FileHash -LiteralPath $installedExe[0].FullName).Hash) { throw 'Installed executable hash mismatch.' }
Add-AppxPackage -Register $installedManifest -ErrorAction Stop
$registered = Get-AppxPackage -Name 'Personal.Happ.Widget'
if (-not $registered -or $registered.Status -ne 'Ok' -or $registered.InstallLocation -ine $destination) { throw 'Registration verification failed.' }
$registered | Select-Object Name, Version, Status, InstallLocation
Write-Host 'Done. Restart WidBar and add Happ Control in Layout.'
