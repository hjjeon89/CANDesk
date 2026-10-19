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

function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

# Exercise installation without building the app or modifying the real Start menu.
function dotnet {
    if ($args[0] -eq '--list-sdks') {
        if ($global:CANDeskInstallerTest_hasSdk) { "$global:CANDeskInstallerTest_sdkVersion [test SDK]" }
        return
    }
    $global:CANDeskInstallerTest_publishCalls++
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

function Get-Command {
    param($Name, $ErrorAction)
    if ($Name -like '*\Microsoft\dotnet\dotnet.exe') {
        if ($global:CANDeskInstallerTest_hasSdk) {
            Microsoft.PowerShell.Core\Get-Command dotnet
        }
        return
    }
    Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
}

function Invoke-WebRequest {
    param($Uri, [switch] $UseBasicParsing, $OutFile)
    Assert ($Uri -eq 'https://dot.net/v1/dotnet-install.ps1') 'SDK must come from Microsoft.'
    $global:CANDeskInstallerTest_downloads++
    Set-Content -LiteralPath $OutFile -Value '# mocked SDK installer'
}

function powershell.exe {
    Assert ($args -contains '8.0') 'Expected .NET 8 SDK installation.'
    Assert ($args -contains 'x64') 'Expected x64 SDK installation.'
    $global:LASTEXITCODE = 1
    if ($global:CANDeskInstallerTest_sdkFailure) { return }
    $global:CANDeskInstallerTest_hasSdk = $true
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

$calls = $global:CANDeskInstallerTest_publishCalls
$global:CANDeskInstallerTest_hasSdk = $false
& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly -WhatIf
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls) 'WhatIf started a build.'
Assert ($global:CANDeskInstallerTest_downloads -eq 0) 'WhatIf downloaded an SDK.'

$global:CANDeskInstallerTest_publishFailure = $false
& $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly
Assert ($global:CANDeskInstallerTest_downloads -eq 1) 'Missing SDK was not downloaded.'
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls + 1) 'Build did not run after SDK installation.'

$global:CANDeskInstallerTest_hasSdk = $false
$global:CANDeskInstallerTest_sdkFailure = $true
$failure = $null
try { & $installer -InstallDirectory $global:CANDeskInstallerTest_installDirectory -BuildOnly:$BuildOnly } catch { $failure = $_ }
Assert ($null -ne $failure -and $failure.ToString() -like '*SDK installation failed*') 'SDK installation failure was not reported.'
Assert ($global:CANDeskInstallerTest_publishCalls -eq $calls + 1) 'A build started after SDK installation failed.'
Write-Host 'PASS: install, shortcut, native files, cleanup, failed publish, WhatIf, SDK bootstrap and failure.'
