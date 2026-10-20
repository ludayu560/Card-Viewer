[CmdletBinding()]
param([string]$SetupPath)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$setup = $SetupPath
if (-not $setup) {
    $release = Get-Content -LiteralPath (Join-Path $repo 'artifacts/installer/release.json') -Raw | ConvertFrom-Json
    $setup = Join-Path $repo "artifacts/installer/$($release.Installer)"
}
$destination = Join-Path $repo 'artifacts/installer-smoke'
if (-not (Test-Path -LiteralPath $setup)) { throw 'Build the installer first with ./build-installer.ps1.' }
if (Test-Path -LiteralPath $destination) { throw "Smoke-test destination already exists: $destination" }
# /D must be the last argument and must not be quoted (NSIS syntax).
$process = Start-Process -FilePath $setup -ArgumentList "/S /PORTABLE /D=$destination" -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Silent installation failed: $($process.ExitCode)" }
$sentinel = Join-Path $destination 'keep-user-file.txt'
[IO.File]::WriteAllText($sentinel, 'Uninstall must preserve unrelated files.')
try {
    $executable = Join-Path $destination 'CardViewer.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Installed executable is missing.' }
    if ((Get-Item -LiteralPath $executable).VersionInfo.ProductName -ne 'Card Viewer') { throw 'Incorrect product branding.' }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'coreclr.dll'))) { throw 'Self-contained runtime is missing.' }
    $reports = Join-Path $repo 'artifacts/installer-smoke-reports'
    New-Item -ItemType Directory -Force $reports | Out-Null
    $parameters = '--verify-build "' + (Join-Path $repo 'TestImages') + '" "' + $reports + '"'
    $process = Start-Process -FilePath $executable -ArgumentList $parameters -WorkingDirectory $repo -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installed application tests failed: $($process.ExitCode)" }
} finally {
    $uninstaller = Join-Path $destination 'Uninstall.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        Start-Process -FilePath $uninstaller -ArgumentList '/S' -WindowStyle Hidden -Wait | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (((Test-Path -LiteralPath (Join-Path $destination 'CardViewer.exe')) -or (Test-Path -LiteralPath $uninstaller)) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Test-Path -LiteralPath (Join-Path $destination 'CardViewer.exe')) { throw 'Uninstall did not remove the app.' }
        if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed an unrelated user file.' }
        Remove-Item -LiteralPath $sentinel
        # Deliberately nonrecursive: unexpected files cause a failure instead of deletion.
        Remove-Item -LiteralPath $destination
    }
}
Write-Host 'PASS: silent installation, installed app tests, branding, runtime, and safe uninstall.'
