[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).ProviderPath
$artifactsPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$solutionPath = Join-Path $repoRoot 'CardViewer.slnx'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Cannot find CardViewer.slnx beside this script; artifacts were not touched."
}

# Resolve and constrain the deletion target before any recursive operation.
if (-not [string]::Equals([IO.Path]::GetDirectoryName($artifactsPath), $repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($artifactsPath) -ne 'artifacts') {
    throw "Refusing to clean a path outside this repository: $artifactsPath"
}
if (-not $PSCmdlet.ShouldProcess($repoRoot, "Delete artifacts and build $Configuration (including tests and benchmark)")) { return }

$localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$usingLocalSdk = Test-Path -LiteralPath $localSdk -PathType Leaf
$dotnetPath = if ($usingLocalSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$originalEnvironment = @{}
try {
    if ($usingLocalSdk) {
        $values = @{
            DOTNET_ROOT = (Join-Path $repoRoot '.tools/dotnet')
            DOTNET_CLI_HOME = (Join-Path $repoRoot '.tools/home')
            NUGET_PACKAGES = (Join-Path $repoRoot '.tools/packages')
        }
        foreach ($name in $values.Keys) {
            $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process')
        }
    }
    $sdks = & $dotnetPath --list-sdks
    if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) { throw 'A .NET 10 SDK is required to build this project.' }

    if (Test-Path -LiteralPath $artifactsPath) {
        # Reject links anywhere in the tree instead of traversing a junction into
        # another directory. Walk one level at a time so no links are followed.
        $pendingDirectories = New-Object 'System.Collections.Generic.Stack[string]'
        $pendingDirectories.Push($artifactsPath)
        while ($pendingDirectories.Count -gt 0) {
            $directoryPath = $pendingDirectories.Pop()
            $directory = Get-Item -LiteralPath $directoryPath -Force
            if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing to clean a non-directory or linked path: $directoryPath"
            }
            foreach ($entry in Get-ChildItem -LiteralPath $directoryPath -Force) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to clean a linked artifact: $($entry.FullName)" }
                if ($entry.PSIsContainer) { $pendingDirectories.Push($entry.FullName) }
            }
        }
        Write-Host "Clearing $artifactsPath"
        Remove-Item -LiteralPath $artifactsPath -Recurse -Force
    }

    $buildArguments = @('build', $solutionPath, '--configuration', $Configuration, '--disable-build-servers', '-maxcpucount:1')
    if ($NoRestore) { $buildArguments += '--no-restore' }
    & $dotnetPath @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Build or automatic verification failed (exit $LASTEXITCODE)." }

    Write-Host "Application: $(Join-Path $repoRoot "src/CardViewer/bin/$Configuration/net10.0-windows/CardViewer.exe")"
    Write-Host "Benchmark: $(Join-Path $artifactsPath "build-verification/$Configuration/net10.0-windows/latest.txt")"
}
finally {
    foreach ($name in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}

