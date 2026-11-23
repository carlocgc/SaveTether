# Builds the Windows installer from a checked release package (build/package.sh), with Inno Setup 6:
#
#   pwsh build/build-installer.ps1 -Package dist/<name>.zip -Version <version> -OutDir dist
#
# Writes <OutDir>/<name>-setup.exe, where <name> is the package's, and prints its path. The installer holds exactly
# the files the package holds; build/windows/savetether.iss says where they go. docs/releasing.md has the rest.
param(
    [Parameter(Mandatory)] [string] $Package,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'

$onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = @(
    $(if ($onPath) { $onPath.Source } else { $null }),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed (ISCC.exe not found).' }

$name = [IO.Path]::GetFileNameWithoutExtension($Package)
$stage = Join-Path ([IO.Path]::GetTempPath()) ("savetether-installer-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $Package -DestinationPath $stage
    $source = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $out = (Resolve-Path $OutDir).Path
    $script = Join-Path $PSScriptRoot 'windows\savetether.iss'
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$source" "/DOutputDir=$out" "/DOutputName=$name-setup" $script
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    Join-Path $out "$name-setup.exe"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
