param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$archive = Join-Path $root 'downloads\scrcpy-win64-v4.1.zip'
$expectedHash = '5B12172B3264B2889F4583EE64752CE832E29BC8B1089DCA81093459697165DB'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'ConnectPad-win-x64-v0.2.0'
$zip = Join-Path $artifacts 'ConnectPad-win-x64-v0.2.0.zip'
$project = Join-Path $root 'src\ConnectPad\ConnectPad.csproj'

if (-not (Test-Path -LiteralPath $archive)) {
    throw "Missing official archive: $archive"
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
if ($actualHash -ne $expectedHash) {
    throw "scrcpy archive SHA-256 mismatch: $actualHash"
}

if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed: $LASTEXITCODE"
}

$requiredFiles = @(
    'ConnectPad.exe',
    'tools\scrcpy\adb.exe',
    'tools\scrcpy\scrcpy.exe',
    'tools\scrcpy\scrcpy-server',
    'tools\scrcpy\LICENSE.txt'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $relativePath))) {
        throw "Publish output is missing: $relativePath"
    }
}

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $publish
Copy-Item -LiteralPath 'C:\Program Files\dotnet\LICENSE.txt' -Destination (Join-Path $publish 'DOTNET_LICENSE.txt')
Copy-Item -LiteralPath 'C:\Program Files\dotnet\ThirdPartyNotices.txt' -Destination (Join-Path $publish 'DOTNET_THIRD_PARTY_NOTICES.txt')

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output $zip
