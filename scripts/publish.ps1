param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\CodexMonitor\CodexMonitor.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$outputPath = Join-Path $artifactRoot "win-x64"

if (Test-Path -LiteralPath $outputPath) {
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $resolvedOutputPath = [IO.Path]::GetFullPath($outputPath)
    if (-not $resolvedOutputPath.StartsWith($resolvedArtifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an output path outside artifacts: $resolvedOutputPath"
    }
    Remove-Item -LiteralPath $resolvedOutputPath -Recurse -Force
}

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ("codexmonitor-smoke-" + [guid]::NewGuid().ToString("N"))
$diagnosticPath = Join-Path $smokeRoot "snapshot.json"
$previousCodexHome = $env:CODEX_HOME

try {
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    $env:CODEX_HOME = $smokeRoot

    $process = Start-Process `
        -FilePath (Join-Path $outputPath "CodexMonitor.exe") `
        -ArgumentList @("--diagnostic-snapshot", $diagnosticPath) `
        -WindowStyle Hidden `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Published executable failed its diagnostic smoke test with exit code $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $diagnosticPath)) {
        throw "Published executable did not create a diagnostic snapshot"
    }

    $snapshot = Get-Content -Raw -LiteralPath $diagnosticPath | ConvertFrom-Json
    if ($null -eq $snapshot.ActiveTasks -or $null -eq $snapshot.ProjectCount) {
        throw "Published executable created an invalid diagnostic snapshot"
    }
}
finally {
    $env:CODEX_HOME = $previousCodexHome
    if ([IO.Directory]::Exists($smokeRoot)) {
        [IO.Directory]::Delete($smokeRoot, $true)
    }
}

Write-Output "Published and verified: $outputPath"
