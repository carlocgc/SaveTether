# Installs the Windows installer silently for this user, checks what it put in place, uninstalls it, and checks that
# everything is gone and the user's Path is as it was. Then again with the folder already on Path by hand, which must
# stay after uninstalling. Used by CI on its throwaway runner:
#
#   pwsh build/check-installer.ps1 -Installer dist/<name>-setup.exe -Version <version> [-Dir <folder>]
#
# -Dir installs into another folder than the default, to test on a machine that has SaveTether in the default one.
# Refuses to run where SaveTether is already installed, so it never tests over a real install. The user's Path is put
# back exactly as it was, value and type, whatever happens.
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Dir = (Join-Path $env:LOCALAPPDATA 'Programs\SaveTether')
)
$ErrorActionPreference = 'Stop'

$app = [IO.Path]::GetFullPath($Dir)
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SaveTether.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E3C9B52-8F1D-4A7E-9C41-2B5D7E0A9F13}_is1'
$files = 'savetether-gui.exe', 'savetether.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll', 'LICENSE', 'README.md'

function Read-UserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try {
        if ($null -eq $key.GetValue('Path')) { return $null }
        [pscustomobject]@{
            Value = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind  = $key.GetValueKind('Path')
        }
    }
    finally { $key.Dispose() }
}

function Write-UserPath($saved) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        if ($null -eq $saved) { $key.DeleteValue('Path', $false) } else { $key.SetValue('Path', $saved.Value, $saved.Kind) }
    }
    finally { $key.Dispose() }
}

function Assert-That([bool] $condition, [string] $what) {
    if (-not $condition) { throw "check failed: $what" }
    Write-Host "ok: $what"
}

function Install-AndCheck {
    Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=addtopath', "/DIR=`"$app`"" -Wait
    foreach ($file in $files) { Assert-That (Test-Path (Join-Path $app $file)) "installed $file" }
    Assert-That (Test-Path $shortcut) 'Start menu entry'
    Assert-That (Test-Path $uninstallKey) 'Settings > Apps entry'
    Assert-That ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $Version) "registered as version $Version"
    Assert-That ((Read-UserPath).Value.Split(';') -contains $app) 'install folder on the user Path'
    $reported = & (Join-Path $app 'savetether.exe') --version
    Assert-That ($reported -eq "savetether $Version") "installed CLI reports '$reported'"
}

function Uninstall-AndCheck {
    # The uninstaller copies itself to a temporary folder and runs from there, so wait for the folder to go.
    Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    $until = (Get-Date).AddSeconds(60)
    while ((Test-Path $app) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
    Assert-That (-not (Test-Path $app)) 'install folder removed'
    Assert-That (-not (Test-Path $shortcut)) 'Start menu entry removed'
    Assert-That (-not (Test-Path $uninstallKey)) 'Settings > Apps entry removed'
}

if (Test-Path $app) { throw "$app already exists; not testing over it." }
if (Test-Path $uninstallKey) { throw 'SaveTether is already installed by its installer; not testing over it.' }
$pathBefore = Read-UserPath
try {
    Write-Host '--- install and uninstall'
    Install-AndCheck
    Uninstall-AndCheck
    Assert-That (((Read-UserPath).Value) -eq $pathBefore.Value) 'user Path as before'
    Assert-That (((Read-UserPath).Kind) -eq $pathBefore.Kind) "user Path's type as before ($($pathBefore.Kind))"

    Write-Host '--- the folder already on Path by hand'
    $byHand = [pscustomobject]@{ Value = (($pathBefore.Value.TrimEnd(';'), $app) -join ';').TrimStart(';'); Kind = $pathBefore.Kind }
    Write-UserPath $byHand
    Install-AndCheck
    Uninstall-AndCheck
    Assert-That (((Read-UserPath).Value) -eq $byHand.Value) 'the entry put there by hand stays'
}
finally {
    Write-UserPath $pathBefore
    Write-Host 'user Path restored'
}
