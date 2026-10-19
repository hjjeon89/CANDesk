#Requires -Version 5.1
param([switch] $BuildOnly)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\install.ps1'
$global:CANDeskInstallerTest_installDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.artifacts\test-check\install-script\installed app'))
$global:CANDeskInstallerTest_programs = [Environment]::GetFolderPath('Programs')
$global:CANDeskInstallerTest_publishFailure = $false
$global:CANDeskInstallerTest_hasSdk = $true
$global:CANDeskInstallerTest_publishCalls = 0
$global:CANDeskInstallerTest_savedShortcut = $null
$global:CANDeskInstallerTest_downloads = 0
$global:CANDeskInstallerTest_sdkFailure = $false
$global:CANDeskInstallerTest_sdkVersion = '10.0.301'
$global:CANDeskInstallerTest_downloadFailure = $false
$global:CANDeskInstallerTest_primaryDownloadFailure = $false
$global:CANDeskInstallerTest_brokenHost = $false
$global:CANDeskInstallerTest_sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

# Exercise installation without building the app or modifying the real Start menu.
function dotnet {
    if ($args[0] -eq '--list-sdks') {
        if ($global:CANDeskInstallerTest_brokenHost) { throw 'Broken dotnet host' }
        $global:LASTEXITCODE = 0
        if ($global:CANDeskInstallerTest_hasSdk) { "$global:CANDeskInstallerTest_sdkVersion [test SDK]" }
        return
    }
    $global:CANDeskInstallerTest_publishCalls++
    Assert ((Get-Location).Path -eq $global:CANDeskInstallerTest_sourceDirectory) 'Publish used the caller folder instead of the source folder.'
    $global:LASTEXITCODE = 1
    if ($global:CANDeskInstallerTest_publishFailure) { return }
    Assert ($args -contains 'win-x64') 'Expected a Windows x64 publish.'
    Assert ($args -contains '--self-contained') 'Expected a bundled runtime.'
    $output = $args[[Array]::IndexOf($args, '--output') + 1]
    New-Item -ItemType Directory -Path (Join-Path $output 'native') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'CANDesk.App.exe') -Value 'new app'
    Set-Content -LiteralPath (Join-Path $output 'native\test.dll') -Value 'native dependency'
    $global:CANDeskInstallerTest_lastPublishDirectory = $output
    $global:LASTEXITCODE = 0
}

function Get-Process { param($Name, $ErrorAction) }

function Start-Transcript {
    param($Path, [switch] $Force)
    $global:CANDeskInstallerTest_lastLog = $Path
    Microsoft.PowerShell.Host\Start-Transcript @PSBoundParameters
}

function Get-Command {
    param($Name, $ErrorAction)
    if ($Name -eq 'dotnet' -and -not $global:CANDeskInstallerTest_hasSdk) { return }
    if ($Name -like '*\Microsoft\dotnet\dotnet.exe') {
        if ($global:CANDeskInstallerTest_hasSdk) {
            Microsoft.PowerShell.Core\Get-Command dotnet
        }
        return
    }
    Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}

function Invoke-WebRequest {
    param($Uri, [switch] $UseBasicParsing, $TimeoutSec, $OutFile)
    Assert ($Uri -in @('https://dot.net/v1/dotnet-install.ps1', 'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1')) 'SDK must come from Microsoft.'
    Assert ($TimeoutSec -eq 60) 'SDK download needs a finite timeout.'
    $global:CANDeskInstallerTest_downloads++
    if ($global:CANDeskInstallerTest_downloadFailure -or
        ($global:CANDeskInstallerTest_primaryDownloadFailure -and $Uri -eq 'https://dot.net/v1/dotnet-install.ps1')) {
        throw 'Simulated blocked download endpoint'
    }
    Set-Content -LiteralPath $OutFile -Value '# mocked SDK installer'
}

function powershell.exe {
    Assert ($args -contains '8.0') 'Expected .NET 8 SDK installation.'
    Assert ($args -contains 'x64') 'Expected x64 SDK installation.'
    Assert (-not ($args -contains '-Quality')) 'Quality prevents the official installer fallback.'
    $global:LASTEXITCODE = 1
    if ($global:CANDeskInstallerTest_sdkFailure) { return }
    $global:CANDeskInstallerTest_hasSdk = $true
    $global:CANDeskInstallerTest_brokenHost = $false
    $global:CANDeskInstallerTest_sdkVersion = '8.0.400'
    $global:LASTEXITCODE = 0
}

function New-Item {
    param($ItemType, $Path, [switch] $Force)
    if ($Path -ne $global:CANDeskInstallerTest_programs) {
        Microsoft.PowerShell.Management\New-Item @PSBoundParameters
    }
}

function New-Object {
    param($ComObject)
    Assert (-not $BuildOnly) 'Build-only mode must not create a Start menu shortcut.'
    Assert ($ComObject -eq 'WScript.Shell') 'Unexpected COM object.'
    $shell = [pscustomobject]@{}
    $shell | Add-Member ScriptMethod CreateShortcut {
        param($Path)
        $shortcut = [pscustomobject]@{ Path = $Path; TargetPath = ''; WorkingDirectory = ''; IconLocation = '' }
        $shortcut | Add-Member ScriptMethod Save { $global:CANDeskInstallerTest_savedShortcut = $this }
        return $shortcut
    }
    return $shell
}

& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly
$executable = Join-Path $global:CANDeskInstallerTest_installDirectory 'CANDesk.App.exe'
Assert ((Get-Content -LiteralPath $executable) -eq 'new app') 'Application was not installed.'
Assert (Test-Path -LiteralPath (Join-Path $global:CANDeskInstallerTest_installDirectory 'native\test.dll')) 'Native dependency was not copied.'
if ($BuildOnly) {
    Assert ($null -eq $global:CANDeskInstallerTest_savedShortcut) 'Build-only mode changed the shortcut.'
}
else {
    Assert ($global:CANDeskInstallerTest_savedShortcut.TargetPath -eq $executable) 'Shortcut target is incorrect.'
    Assert ($global:CANDeskInstallerTest_savedShortcut.Path -eq (Join-Path $global:CANDeskInstallerTest_programs 'CANDesk.lnk')) 'Shortcut location is incorrect.'
}
Assert (-not (Test-Path -LiteralPath $global:CANDeskInstallerTest_lastPublishDirectory)) 'Temporary publish files were not cleaned up.'
Assert ($global:CANDeskInstallerTest_downloads -eq 0) 'An installed SDK was not reused.'

Set-Content -LiteralPath $executable -Value 'existing app'
$global:CANDeskInstallerTest_savedShortcut = $null
$global:CANDeskInstallerTest_publishFailure = $true
$failure = $null
try { & $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly } catch { $failure = $_ }
Assert ($null -ne $failure -and $failure.ToString() -like '*publish failed*') 'Publish failure was not reported.'
Assert ((Get-Content -LiteralPath $executable) -eq 'existing app') 'Failed publish changed the existing installation.'
Assert ($null -eq $global:CANDeskInstallerTest_savedShortcut) 'Failed publish changed the shortcut.'
Assert (Test-Path -LiteralPath $global:CANDeskInstallerTest_lastLog) 'Failure log was not retained.'
$log = Get-Content -LiteralPath $global:CANDeskInstallerTest_lastLog -Raw
Assert ($log -like '*Restore NuGet packages and publish CANDesk*' -and $log -like '*publish failed*') 'Failure log omitted the step or error.'

$calls = $global:CANDeskInstallerTest_publishCalls
$lastLog = $global:CANDeskInstallerTest_lastLog
$global:CANDeskInstallerTest_hasSdk = $false
& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly -WhatIf
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls) 'WhatIf started a build.'
Assert ($global:CANDeskInstallerTest_downloads -eq 0) 'WhatIf downloaded an SDK.'
Assert ($global:CANDeskInstallerTest_lastLog -eq $lastLog) 'WhatIf created a log.'

$global:CANDeskInstallerTest_publishFailure = $false
$global:CANDeskInstallerTest_primaryDownloadFailure = $true
& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly
Assert ($global:CANDeskInstallerTest_downloads -eq 2) 'Blocked primary SDK endpoint did not fall back.'
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls + 1) 'Build did not run after SDK installation.'

$global:CANDeskInstallerTest_hasSdk = $false
$global:CANDeskInstallerTest_sdkFailure = $true
$failure = $null
try { & $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly } catch { $failure = $_ }
Assert ($null -ne $failure -and $failure.ToString() -like '*SDK installation failed*') 'SDK installation failure was not reported.'
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls + 1) 'A build started after SDK installation failed.'

$global:CANDeskInstallerTest_sdkFailure = $false
$global:CANDeskInstallerTest_hasSdk = $true
$global:CANDeskInstallerTest_brokenHost = $true
& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly
Assert (-not $global:CANDeskInstallerTest_brokenHost) 'A broken SDK host was not replaced.'

$global:CANDeskInstallerTest_hasSdk = $false
$global:CANDeskInstallerTest_downloadFailure = $true
$calls = $global:CANDeskInstallerTest_publishCalls
$failure = $null
try { & $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly } catch { $failure = $_ }
Assert ($null -ne $failure -and $failure.ToString() -like '*blocked download*') 'Download failure was not reported.'
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls) 'A build started after downloads failed.'

$incompleteSource = Join-Path $global:CANDeskInstallerTest_installDirectory '..\incomplete source'
New-Item -ItemType Directory -Path $incompleteSource -Force | Out-Null
Copy-Item -LiteralPath $installer -Destination (Join-Path $incompleteSource 'install.ps1') -Force
$downloads = $global:CANDeskInstallerTest_downloads
$failure = $null
try { & (Join-Path $incompleteSource 'install.ps1') -BuildOnly } catch { $failure = $_ }
Assert ($null -ne $failure -and $failure.ToString() -like '*source files are missing*') 'Missing source was not reported.'
Assert ($global:CANDeskInstallerTest_downloads -eq $downloads) 'SDK was downloaded before checking the source.'
Write-Host 'PASS: install/build, SDK detection/bootstrap/fallback/failure, source validation, caller folder, WhatIf, output preservation.'
