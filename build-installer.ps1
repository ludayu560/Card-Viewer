[CmdletBinding()]
param(
    [switch]$NoRestore,
    [switch]$UsePublishedOutput,
    [switch]$PublishOnly,
    [switch]$PrepareOnly,
    [switch]$SmokeTest,
    [string]$CertificateThumbprint,
    [uri]$TimestampServer
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $repo 'src/CardViewer/CardViewer.csproj'
[xml]$projectXml = [IO.File]::ReadAllText($project)
$version = ([string]$projectXml.Project.PropertyGroup.Version).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installer requires a three-part numeric project Version.' }
if ($PublishOnly -and ($PrepareOnly -or $UsePublishedOutput -or $SmokeTest)) { throw '-PublishOnly cannot be combined with prepare, reuse, or smoke-test options.' }
if ($PrepareOnly -and $SmokeTest) { throw '-PrepareOnly does not create an installer to smoke-test.' }
if ($TimestampServer -and (-not $CertificateThumbprint -or $TimestampServer.Scheme -ne 'https')) { throw 'TimestampServer requires a signing certificate and an HTTPS URL.' }
$publish = Join-Path $repo 'artifacts/publish/win-x64'
$output = Join-Path $repo 'artifacts/installer'
$nsisRoot = Join-Path $repo '.tools/nsis-3.13'
$compiler = Join-Path $nsisRoot 'Bin/makensis.exe'
$expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'

function Initialize-Compiler {
if (-not (Test-Path -LiteralPath $compiler)) {
    $archive = Join-Path $repo '.tools/nsis-3.13.zip'
    New-Item -ItemType Directory -Force (Join-Path $repo '.tools') | Out-Null
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $expectedHash) {
        $urls = @(
            'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip',
            'https://github.com/tauri-apps/binary-releases/releases/download/nsis-3.13/nsis-3.13.zip'
        )
        foreach ($url in $urls) {
            try {
                Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
                if ((Get-FileHash -LiteralPath $archive).Hash -eq $expectedHash) { break }
            } catch { Write-Warning "Download failed: $url" }
        }
    }
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $expectedHash) {
        throw 'NSIS download did not match the pinned SHA-256; refusing to execute it.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $repo '.tools') -Force
}
}

function Sign-ReleaseFile([string]$Path) {
    if (-not $CertificateThumbprint) { return }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'Signing certificate has no private key.' }
    $signing = @{ FilePath = $Path; Certificate = $certificate; HashAlgorithm = 'SHA256' }
    if ($TimestampServer) { $signing.TimestampServer = $TimestampServer.AbsoluteUri }
    $signature = Set-AuthenticodeSignature @signing
    if ($signature.Status -ne 'Valid') { throw "Signing failed for ${Path}: $($signature.StatusMessage)" }
}

$saved = @{}
try {
    $localSdk = Join-Path $repo '.tools/dotnet/dotnet.exe'
    $dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet).Source }
    $environment = @{ NSISDIR = $nsisRoot }
    if ($dotnet -eq $localSdk) {
        $environment.DOTNET_ROOT = Join-Path $repo '.tools/dotnet'
        $environment.DOTNET_CLI_HOME = Join-Path $repo '.tools/home'
        $environment.NUGET_PACKAGES = Join-Path $repo '.tools/packages'
        $environment.APPDATA = Join-Path $repo '.tools/appdata'
    }
    foreach ($name in $environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    if (-not $UsePublishedOutput) {
        # A fresh staging tree prevents removed files from leaking into later installers.
        if (Test-Path -LiteralPath $publish) {
            if ([IO.Path]::GetFullPath($publish) -ne (Join-Path $repo 'artifacts/publish/win-x64')) { throw 'Invalid publish directory.' }
            $entries = @(Get-Item -LiteralPath $publish) + @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
            if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Publish directory contains a link.' }
            Remove-Item -LiteralPath $publish -Recurse -Force
        }
        Write-Host '[1/4] Publishing application; automatic tests and cold/warm benchmark run during build.'
        $arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', $publish, '--disable-build-servers', '-maxcpucount:1')
        if ($NoRestore) { $arguments += '--no-restore' }
        & $dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Publish, tests, or cold-load benchmark failed.' }
    }
    foreach ($required in @('CardViewer.exe', 'CardViewer.dll', 'CardViewer.runtimeconfig.json', 'coreclr.dll', 'PresentationFramework.dll', 'ThirdParty/Magick.NET-Notice.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish is incomplete; missing $required. Run without -UsePublishedOutput." }
    }
    if ($PublishOnly) { Write-Host "Published application: $publish"; return }
    Write-Host '[2/4] Preparing exact install/uninstall manifests.'
    Sign-ReleaseFile (Join-Path $publish 'CardViewer.exe')
    New-Item -ItemType Directory -Force $output | Out-Null
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName)
    $install = [Collections.Generic.List[string]]::new()
    $uninstall = [Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($publish.Length + 1)
        if ($relative -match '[\$\"\r\n]') { throw "Unsupported package filename: $relative" }
        $parent = [IO.Path]::GetDirectoryName($relative)
        $install.Add('SetOutPath "$INSTDIR' + $(if ($parent) { '\' + $parent }) + '"')
        $install.Add('File "' + $file.FullName + '"')
        $uninstall.Add('Delete "$INSTDIR\' + $relative + '"')
    }
    foreach ($directory in Get-ChildItem -LiteralPath $publish -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending) {
        $uninstall.Add('RMDir "$INSTDIR\' + $directory.FullName.Substring($publish.Length + 1) + '"')
    }
    $installManifest = Join-Path $output 'install-files.nsh'
    $uninstallManifest = Join-Path $output 'uninstall-files.nsh'
    [IO.File]::WriteAllLines($installManifest, $install)
    [IO.File]::WriteAllLines($uninstallManifest, $uninstall)
    if ($PrepareOnly) { Write-Host "Manifests prepared: $output. Native compiler was not executed."; return }
    Write-Host '[3/4] Compiling Windows setup with NSIS.'
    Initialize-Compiler
    $setup = Join-Path $output "CardViewer-Setup-$version-win-x64.exe"
    & $compiler /V3 /WX "/DAPP_VERSION=$version" "/DSETUP_FILE=$setup" "/DAPP_ICON=$(Join-Path $repo 'src/CardViewer/Assets/CardViewer.ico')" "/DINSTALL_MANIFEST=$installManifest" "/DUNINSTALL_MANIFEST=$uninstallManifest" (Join-Path $repo 'installer/CardViewer.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed. Run this script from your own Windows PowerShell session with permission to execute native tools.' }
    Sign-ReleaseFile $setup
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$setup.sha256", "$hash  $([IO.Path]::GetFileName($setup))`r`n")
    $release = [ordered]@{ Product = 'Card Viewer'; Version = $version; Runtime = 'win-x64'; SelfContained = $true; Installer = [IO.Path]::GetFileName($setup); Sha256 = $hash; Bytes = (Get-Item -LiteralPath $setup).Length; CreatedUtc = [DateTime]::UtcNow.ToString('o'); Signed = [bool]$CertificateThumbprint }
    [IO.File]::WriteAllText((Join-Path $output 'release.json'), ($release | ConvertTo-Json))
    Write-Host '[4/4] Release manifest and checksum created.'
    if ($SmokeTest) {
        & (Join-Path $repo 'installer/Test-Installer.ps1') -SetupPath $setup
    }
    Write-Host "Installer: $setup"
    Write-Host "SHA-256: $hash"
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
