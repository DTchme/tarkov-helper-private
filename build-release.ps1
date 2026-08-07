[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'TarkovHelper\TarkovHelper.csproj'
$LocalDotnet = Join-Path $Root '.dotnet'
$PublishDir = Join-Path $Root 'publish\win-x64'
$ReleaseDir = Join-Path $Root 'release'
$ReleaseZip = Join-Path $ReleaseDir 'TarkovHelper_v1.5.10_1.1_windows_v6.zip'
$LogPath = Join-Path $Root 'build-release.log'

Start-Transcript -Path $LogPath -Force | Out-Null
try {
    Write-Host '=== Tarkov Helper 1.5.10 Windows build v2 ==='

    if (-not (Test-Path $Project)) {
        throw "Project file not found: $Project"
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $useLocalDotnet = $true

    if ($dotnetCommand) {
        $sdkLines = @(& dotnet --list-sdks 2>$null)
        $sdk8 = $sdkLines | Where-Object { $_ -match '^8\.' } | Select-Object -First 1
        if ($sdk8) {
            $useLocalDotnet = $false
        }
        else {
            Write-Host 'dotnet is installed, but a .NET 8 SDK was not found. A local .NET 8 SDK will be installed.'
        }
    }

    if ($useLocalDotnet) {
        New-Item -ItemType Directory -Force -Path $LocalDotnet | Out-Null
        $installScript = Join-Path $Root 'dotnet-install.ps1'
        Write-Host 'Downloading official .NET 8 SDK installer...'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $installScript `
            -Channel '8.0' `
            -Architecture 'x64' `
            -InstallDir $LocalDotnet `
            -NoPath
        if ($LASTEXITCODE -ne 0) {
            throw ".NET SDK installation failed with exit code $LASTEXITCODE"
        }
        $env:PATH = "$LocalDotnet;$env:PATH"
    }

    Write-Host "Using .NET SDK: $(& dotnet --version)"

    if (Test-Path $PublishDir) {
        Remove-Item -Recurse -Force $PublishDir
    }
    if (Test-Path $ReleaseDir) {
        Remove-Item -Recurse -Force $ReleaseDir
    }
    New-Item -ItemType Directory -Force -Path $PublishDir, $ReleaseDir | Out-Null

    Write-Host 'Restoring NuGet packages...'
    & dotnet restore $Project --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }

    Write-Host 'Publishing self-contained Windows x64 build...'
    & dotnet publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $PublishDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $exe = Join-Path $PublishDir 'TarkovHelper.exe'
    if (-not (Test-Path $exe)) {
        throw "Published executable not found: $exe"
    }

    @"
Tarkov Helper 1.5.10 / Escape from Tarkov 1.1 quest and coordinate refresh

Recommended first run:
1. Back up the existing Tarkov Helper folder.
2. Run TarkovHelper.exe.
3. Select the PvE profile.
4. Refresh the current profile quest database and map coordinates.
5. Select the Tarkov log folder.
6. Run quest progress rebuild and review the preview before applying.

The application backs up quest databases before replacement/rebuild, but keeping a copy of the old folder is still recommended.
"@ | Set-Content -Encoding UTF8 (Join-Path $PublishDir 'README_FIRST.txt')

    Write-Host 'Creating release ZIP...'
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $ReleaseZip -CompressionLevel Optimal -Force

    $hash = Get-FileHash -Algorithm SHA256 $ReleaseZip
    "$($hash.Hash)  $([IO.Path]::GetFileName($ReleaseZip))" | Set-Content -Encoding ASCII (Join-Path $ReleaseDir 'SHA256SUMS.txt')

    Write-Host ''
    Write-Host 'Build completed successfully.'
    Write-Host "EXE: $exe"
    Write-Host "ZIP: $ReleaseZip"
    exit 0
}
catch {
    $message = if ($_.Exception -and $_.Exception.Message) { $_.Exception.Message } else { [string]$_ }
    Write-Host ''
    Write-Host "[FAILED] $message" -ForegroundColor Red
    exit 1
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
}
