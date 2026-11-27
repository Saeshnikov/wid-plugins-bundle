[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'Personal.GitHub'), [string]$TemplateDirectory)
$ErrorActionPreference = 'Stop'
function Check-Exit([string]$Step) { if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)" } }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output already exists: $OutputDirectory. Choose a new directory." }
if (-not $TemplateDirectory) {
    $templateCommit = 'edd1e46fcdb1c2edac8707d37b093cc271bdd126'
    $TemplateDirectory = Join-Path $PSScriptRoot 'widbar-widget-template'
    if (-not (Test-Path -LiteralPath $TemplateDirectory)) {
        git clone https://github.com/andelby/widbar-widget-template.git $TemplateDirectory
        Check-Exit 'Clone template'
        git -C $TemplateDirectory checkout --detach $templateCommit
        Check-Exit 'Check out pinned template'
    }
    $actualCommit = git -C $TemplateDirectory rev-parse HEAD
    Check-Exit 'Read template revision'
    if ($actualCommit -ne $templateCommit) {
        throw "Expected template revision $templateCommit in $TemplateDirectory. Use a clean checkout of that revision or pass -TemplateDirectory explicitly."
    }
} else {
    $TemplateDirectory = (Resolve-Path -LiteralPath $TemplateDirectory).Path
}
Push-Location (Join-Path $PSScriptRoot '..\..')
try {
    dotnet new install $TemplateDirectory
    Check-Exit 'Install template'
    dotnet new widbar-widget -n Personal.GitHub --PluginId com.personal.github --DisplayName 'GitHub Activity' -o $OutputDirectory
    Check-Exit 'Generate project'
} finally {
    Pop-Location
}
$plugin = Join-Path $OutputDirectory 'Personal.GitHub.ExtensionApp\MainPlugin.cs'
if (-not (Test-Path -LiteralPath $plugin)) { throw 'Template layout changed. Inspect the generated project before replacing MainPlugin.cs.' }
$original = Get-Content -LiteralPath $plugin -Raw
if ($original -notmatch 'class\s+MainPlugin' -or $original -notmatch 'namespace\s+Personal\.GitHub\.ExtensionApp\s*[;{]') { throw 'Unexpected plugin class or namespace. Inspect the template.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MainPlugin.cs') -Destination $plugin -Force
Write-Host "Project generated: $OutputDirectory"
Write-Host 'See the repository README for build instructions. This command does not register the package.'
