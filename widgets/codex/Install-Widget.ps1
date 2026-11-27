[CmdletBinding()]
param(
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot 'Personal.Codex'),
    [string]$MSBuildPath,
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$ProjectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
if (-not $MSBuildPath) {
    $msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($msbuild) { $MSBuildPath = $msbuild.Path }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        }
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'MSBuild not found. Pass -MSBuildPath explicitly.'
}
$MSBuildPath = [IO.Path]::GetFullPath($MSBuildPath)
if (-not $BuildOnly -and (Get-Process -Name 'WidBar','Personal.Codex.ExtensionApp' -ErrorAction SilentlyContinue)) {
    throw 'Exit WidBar from its tray menu before registering the widget.'
}

if (-not (Test-Path -LiteralPath $ProjectDirectory)) {
    & dotnet new widbar-widget -n Personal.Codex --PluginId com.personal.codex --DisplayName 'Codex Limits' -o $ProjectDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Template generation failed. See the root README for template setup.' }
}
$extension = Join-Path $ProjectDirectory 'Personal.Codex.ExtensionApp'
$main = Join-Path $extension 'MainPlugin.cs'
if (-not (Test-Path -LiteralPath $main -PathType Leaf) -or
    (Get-Content -LiteralPath $main -Raw) -notmatch 'namespace\s+Personal\.Codex\.ExtensionApp\s*[;{]') {
    throw 'Unexpected template layout or namespace. No source files replaced.'
}
$projects = @(Get-ChildItem -LiteralPath $ProjectDirectory -Recurse -Filter '*.wapproj' -File)
if ($projects.Count -ne 1) { throw 'Expected one packaging project.' }
$project = $projects[0].FullName
$sources = 'MainPlugin.cs', 'CodexUsage.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
Copy-Item -LiteralPath $sources -Destination $extension -Force
& $MSBuildPath $project /restore /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Never
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE). No registration attempted." }

$output = Join-Path (Split-Path -Parent $project) 'bin\x64\Release'
$manifest = Join-Path $output 'AppxManifest.xml'
[xml]$xml = Get-Content -LiteralPath $manifest -Raw
$identity = $xml.SelectSingleNode("//*[local-name()='Identity']")
$ext = $xml.SelectSingleNode("//*[local-name()='AppExtension' and @Name='com.widbar.widget']")
$pluginId = if ($ext) { $ext.SelectSingleNode(".//*[local-name()='WidBarPluginId']") }
if (-not $identity -or $identity.GetAttribute('Name') -ne 'Personal.Codex.Widget' -or
    -not $pluginId -or $pluginId.InnerText -ne 'com.personal.codex') {
    throw 'Unexpected package identity or plugin ID. No registration attempted.'
}
$images = Join-Path (Split-Path -Parent $project) 'Images'
Copy-Item -LiteralPath $images -Destination $output -Recurse -Force
$generated = Join-Path $extension 'obj\widbar\plugin.json'
$null = Get-Content -LiteralPath $generated -Raw | ConvertFrom-Json
$public = Join-Path $output 'Public'
New-Item -ItemType Directory -Path $public -Force | Out-Null
Copy-Item -LiteralPath $generated -Destination (Join-Path $public 'plugin.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\licenses\widbar-widget-template.MIT.txt') -Destination $output -Force
$recipes = @(Get-ChildItem -LiteralPath $output -Filter '*.build.appxrecipe' -File)
if ($recipes.Count -ne 1) { throw 'Expected one MSBuild package recipe. No registration attempted.' }
[xml]$recipe = Get-Content -LiteralPath $recipes[0].FullName -Raw
$packageFiles = @($recipe.SelectNodes("//*[local-name()='AppxPackagedFile']/*[local-name()='PackagePath']"))
if (-not $packageFiles.Count) { throw 'Empty MSBuild package recipe. No registration attempted.' }
foreach ($file in $packageFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $file.InnerText) -PathType Leaf)) {
        throw "Loose package is missing '$($file.InnerText)'. No registration attempted."
    }
}
if ($BuildOnly) {
    Write-Host "Built Personal.Codex.Widget / com.personal.codex (not registered): $output"
    return
}

$destination = Join-Path $env:LOCALAPPDATA 'WidBarWidgets\Codex'
foreach ($package in @(Get-AppxPackage -Name 'Personal.Codex.Widget')) {
    if (-not $package.InstallLocation -or $package.InstallLocation -ine $destination) {
        throw 'Codex Limits is registered from another directory. Remove only Personal.Codex.Widget before moving it.'
    }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $output -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
}
Add-AppxPackage -Register (Join-Path $destination 'AppxManifest.xml') -ErrorAction Stop
$registered = Get-AppxPackage -Name 'Personal.Codex.Widget'
if (-not $registered -or $registered.Status -ne 'Ok' -or $registered.InstallLocation -ine $destination) {
    throw 'Registration verification failed.'
}
Write-Host 'Restart WidBar and add Codex Limits in Layout.'
