$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'backend\src\LifeOS.Desktop\LifeOS.Desktop.csproj'
$publishPath = Join-Path $PSScriptRoot 'publish'
$scriptPath = Join-Path $PSScriptRoot 'LifeOS.iss'

$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $compiler) {
    $knownPaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )
    $uninstallRoots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $uninstallRoots) {
        $locations = Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue |
            Get-ItemProperty -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup *' -and $_.InstallLocation }
        foreach ($entry in $locations) { $knownPaths += Join-Path $entry.InstallLocation 'ISCC.exe' }
    }
    $compilerPath = $knownPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compilerPath) { throw 'Inno Setup 6 or 7 is required to create the installer. Install it, reopen the terminal, and run this script again.' }
} else {
    $compilerPath = $compiler.Source
}

$installerRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$resolvedPublishPath = [System.IO.Path]::GetFullPath($publishPath)
if (-not $resolvedPublishPath.StartsWith(($installerRoot.TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish output path must remain inside the installer folder.'
}
if (Test-Path -LiteralPath $resolvedPublishPath) { Remove-Item -LiteralPath $resolvedPublishPath -Recurse -Force }

dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'The LifeOS desktop publish step failed.' }

& $compilerPath $scriptPath
if ($LASTEXITCODE -ne 0) { throw 'The Inno Setup installer build failed.' }

Write-Host "Installer created at $(Join-Path $PSScriptRoot 'output\LifeOS-Setup-x64.exe')"
