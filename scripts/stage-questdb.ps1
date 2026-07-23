<#
.SYNOPSIS
    Downloads, verifies, and stages the pinned native QuestDB Windows runtime for release artifacts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Destination
)

$ErrorActionPreference = 'Stop'
$version = '9.4.3'
$expectedSha256 = 'ff8e0cade90559720c78660e1391b9dc95ec0d365dac496c00212ff3095d40f5'
$expectedSizeBytes = 84814552
$assetName = "questdb-$version-rt-windows-x86-64.tar.gz"
$downloadUrl = "https://github.com/questdb/questdb/releases/download/$version/$assetName"
$archiveRootName = "questdb-$version-rt-windows-x86-64"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("daxalgo-questdb-" + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryRoot $assetName
$extractPath = Join-Path $temporaryRoot 'extract'

New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
try {
    Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $archivePath
    $actualSizeBytes = (Get-Item -LiteralPath $archivePath).Length
    if ($actualSizeBytes -ne $expectedSizeBytes) {
        throw "QuestDB runtime size mismatch. Expected $expectedSizeBytes bytes; received $actualSizeBytes."
    }

    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "QuestDB runtime checksum mismatch. Expected $expectedSha256; received $actualSha256."
    }

    & tar -xzf $archivePath -C $extractPath
    if ($LASTEXITCODE -ne 0) { throw "QuestDB runtime extraction failed ($LASTEXITCODE)." }

    $runtimeRoot = Join-Path $extractPath $archiveRootName
    $runtimeExecutable = Join-Path $runtimeRoot 'bin\questdb.exe'
    $mainLicense = Join-Path $runtimeRoot 'legal\LICENSE.txt'
    $thirdPartyNotices = Join-Path $runtimeRoot 'legal\THIRD_PARTY_LICENSES.txt'
    if (-not (Test-Path -LiteralPath $runtimeExecutable -PathType Leaf)) {
        throw "Verified QuestDB archive did not contain bin\questdb.exe."
    }
    if (-not (Test-Path -LiteralPath $mainLicense -PathType Leaf)) {
        throw "Verified QuestDB archive did not contain legal\LICENSE.txt."
    }
    if (-not (Test-Path -LiteralPath $thirdPartyNotices -PathType Leaf)) {
        throw "Verified QuestDB archive did not contain its third-party legal notices."
    }

    $componentManifest = [ordered]@{
        schemaVersion = 1
        component = 'QuestDB'
        version = $version
        sourceUrl = $downloadUrl
        archive = $assetName
        archiveSha256 = $expectedSha256
        archiveSizeBytes = $expectedSizeBytes
        executable = 'bin/questdb.exe'
        licenseFiles = @('legal/LICENSE.txt', 'legal/THIRD_PARTY_LICENSES.txt')
    }
    $componentManifestJson = $componentManifest | ConvertTo-Json -Depth 4

    foreach ($target in $Destination) {
        if ([string]::IsNullOrWhiteSpace($target)) { throw 'A QuestDB staging destination was empty.' }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $runtimeRoot '*') -Destination $target -Recurse -Force
        $manifestPath = Join-Path $target 'component-manifest.json'
        [System.IO.File]::WriteAllText(
            $manifestPath,
            $componentManifestJson + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $temporaryLeaf = [System.IO.Path]::GetFileName($resolvedTemporaryRoot)
        $isUnderSystemTemp = $resolvedTemporaryRoot.StartsWith(
            $resolvedSystemTemp,
            [System.StringComparison]::OrdinalIgnoreCase)
        $hasExpectedPrefix = $temporaryLeaf.StartsWith(
            'daxalgo-questdb-',
            [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $isUnderSystemTemp -or -not $hasExpectedPrefix) {
            throw "Refusing to clean an unexpected temporary path: $resolvedTemporaryRoot"
        }

        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
