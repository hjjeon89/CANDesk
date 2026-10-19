#Requires -Version 5.1
<#
.SYNOPSIS
Builds and installs CANDesk for the current Windows user.
.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CANDesk'),
    [switch] $BuildOnly
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'CANDesk requires Windows.'
}

if (Get-Process -Name 'CANDesk.App' -ErrorAction SilentlyContinue) {
    throw 'Close CANDesk before installing or updating it.'
}

$InstallDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDirectory)
$action = if ($BuildOnly) { 'Install missing .NET SDK and publish CANDesk build files' } else { 'Install missing .NET SDK, build and install CANDesk, and create a Start menu shortcut' }
if (-not $PSCmdlet.ShouldProcess($InstallDirectory, $action)) {
    return
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workDirectory = Join-Path $temporaryRoot ('CANDesk-install-' + [guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $workDirectory 'publish'
try {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $sdkDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    $sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
    if (-not $dotnet -or -not (@(& $dotnet --list-sdks) -match '^(8|9|[1-9][0-9]+)\.')) {
        $dotnet = Get-Command $sdkExecutable -ErrorAction SilentlyContinue
    }
    if (-not $dotnet -or -not (@(& $dotnet --list-sdks) -match '^(8|9|[1-9][0-9]+)\.')) {
        Write-Host 'Installing .NET SDK 8 for the current user...'
        New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
        $sdkInstaller = Join-Path $workDirectory 'dotnet-install.ps1'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing -OutFile $sdkInstaller
        # The official installer can call exit, so run it in a child process.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sdkInstaller -Channel 8.0 -Quality GA -Architecture x64 -InstallDir $sdkDirectory -NoPath
        if ($LASTEXITCODE -ne 0) {
            throw ".NET SDK installation failed (exit code $LASTEXITCODE)."
        }
        $dotnet = Get-Command $sdkExecutable -ErrorAction Stop
        if (-not (@(& $dotnet --list-sdks) -match '^8\.')) {
            throw '.NET SDK installation did not produce a usable .NET 8 SDK.'
        }
    }

    $project = Join-Path $PSScriptRoot 'src\CANDesk.App\CANDesk.App.csproj'
    & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "CANDesk publish failed (exit code $LASTEXITCODE)."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'CANDesk.App.exe') -PathType Leaf)) {
        throw 'Publish did not produce CANDesk.App.exe. Destination was not changed.'
    }

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $publishDirectory -Force |
        Copy-Item -Destination $InstallDirectory -Recurse -Force

    if ($BuildOnly) {
        Write-Host "CANDesk build files: $InstallDirectory"
        Write-Host 'Run CANDesk.App.exe from this folder. Copy the entire folder when distributing the app.'
    }
    else {
        $programs = [Environment]::GetFolderPath('Programs')
        New-Item -ItemType Directory -Path $programs -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $programs 'CANDesk.lnk'))
        $shortcut.TargetPath = Join-Path $InstallDirectory 'CANDesk.App.exe'
        $shortcut.WorkingDirectory = $InstallDirectory
        $shortcut.IconLocation = $shortcut.TargetPath + ',0'
        $shortcut.Save()

        Write-Host "CANDesk installed: $InstallDirectory"
        Write-Host 'Launch CANDesk from the Start menu. Install CAN hardware vendor drivers separately.'
    }
}
finally {
    if (Test-Path -LiteralPath $workDirectory) {
        $cleanupPath = (Resolve-Path -LiteralPath $workDirectory).ProviderPath
        if (-not $cleanupPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unexpected temporary directory: $cleanupPath"
        }
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}
