$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw 'Inno Setup 6 is required to build the installer.'
    }
    $compiler = $command.Source
}

$completed = $false
Push-Location $projectRoot
try {
    & dotnet publish .\MandelaVpnGuard.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o .\publish-installer
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    & $compiler .\installer\MandelaVpnGuard.iss
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    $completed = $true
}
finally {
    Pop-Location
    $publishDirectory = Join-Path $projectRoot 'publish-installer'
    if ($completed -and (Test-Path -LiteralPath $publishDirectory)) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
