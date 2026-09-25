#Requires -Version 5.1
<#
.SYNOPSIS
    Fetch the LATEST RyzenSMU PawnIO module and stage it for the AcerHelper build.

.DESCRIPTION
    AcerHelper's CPU-undervolt loads the PawnIO module "RyzenSMU.bin" into the kernel driver
    (Infrastructure/Vendors/Generic/RyzenCurveOptimizer.Windows.cs). The blob is LGPL-2.1-or-later and
    upstream (namazso/PawnIO.Modules) publishes it inside a per-release zip asset, with GitHub recording
    a SHA-256 "digest" for that asset. This script resolves the latest release, picks the release zip,
    verifies it against the published digest, extracts RyzenSMU.bin, checks that the two functions the
    app drives are still exported, and copies it where the csproj embeds it.

    It is invoked from the FetchRyzenSmu MSBuild target in AcerHelper.csproj, so a normal `dotnet build`
    / `dotnet publish` (Windows TFM) performs the fetch; the Docker/Linux build never does, because the
    module is Windows-only.

    Exit codes -- the target maps these onto MSBuild warnings/errors:
        0  the module was staged (or -SelfTest passed)
        1  upstream was unavailable and nothing usable was cached (degradable: the feature hides)
        2  an integrity check failed (never degradable: mismatched digest, missing exports, pinned-hash
           mismatch)
        3  an unexpected internal failure

    Caching: the resolved release and its extracted module live under -CacheDir (obj/ryzensmu by default),
    keyed by the release tag. The GitHub release API is queried on every run so "latest" really is latest,
    but the zip is only downloaded when the resolved tag has no verified cached copy, so repeat builds and
    CI runs with a warm cache do not re-download.

    Integrity: the downloaded zip is checked against the release asset's published SHA-256 digest when
    GitHub provides one (it does for current releases). The extracted module's SHA-256 is always recorded
    in <CacheDir>/<tag>/resolved.json and printed to the build log. -PinnedSha256 enforces an exact module
    hash and -PinnedVersion pins a release; neither is required.

    Offline: if the API or the download fails, a previously cached module for the last known tag is used;
    if there is none, the script exits 1 and the build continues with the feature hidden.
#>
[CmdletBinding()]
param(
    # Where to write the staged module (the csproj passes third-party\RyzenSMU.bin).
    [string]$OutputPath = '',

    # Cache root. Defaults to <cwd>/obj/ryzensmu.
    [string]$CacheDir = '',

    [string]$Repo = 'namazso/PawnIO.Modules',
    [string]$AssetPattern = 'release_*.zip',
    [string]$ModuleName = 'RyzenSMU.bin',

    # Optional hard pins. Empty means "whatever is latest".
    [string]$PinnedVersion = '',
    [string]$PinnedSha256 = '',

    # The module must still name these exports; a release that drops one is treated as an integrity failure.
    [string]$ExpectedExports = 'ioctl_read_smu_register,ioctl_write_smu_register',

    [int]$TimeoutSec = 20,

    # Ignore a verified cache and re-download the asset.
    [switch]$Force,

    # Run the offline parsing/selection assertions and exit (no network).
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# ---------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------

function Get-Sha256([string]$Path) {
    # Deliberately .NET, not Get-FileHash: when MSBuild launches Windows PowerShell 5.1 it inherits a
    # PSModulePath seeded with PowerShell 7 module paths, and on that mixture Get-FileHash is not resolvable
    # (measured). SHA256 + FileStream depend on no PowerShell module at all.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $bytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
        }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
}

# GitHub asset JSON carries digest as "sha256:<hex>"; accept a bare hex too. Anything else is not a digest.
function Get-DigestHex([string]$Digest) {
    if ([string]::IsNullOrWhiteSpace($Digest)) { return $null }
    $value = $Digest.Trim()
    if ($value -match '^sha256:(?<h>[0-9a-fA-F]{64})$') { return $Matches['h'].ToLowerInvariant() }
    if ($value -match '^[0-9a-fA-F]{64}$') { return $value.ToLowerInvariant() }
    return $null
}

function Test-BytePattern([byte[]]$Haystack, [byte[]]$Needle) {
    if ($null -eq $Needle -or $Needle.Length -eq 0 -or $null -eq $Haystack -or $Haystack.Length -lt $Needle.Length) {
        return $false
    }
    $limit = $Haystack.Length - $Needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        $ok = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}

# Returns $null when every export name is present, otherwise a reason string.
function Test-ModuleExports([string]$Path, [string]$Exports) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    foreach ($name in ($Exports -split ',')) {
        $name = $name.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if (-not (Test-BytePattern -Haystack $bytes -Needle ([System.Text.Encoding]::ASCII.GetBytes($name)))) {
            return "the module does not name the export '$name'"
        }
    }
    return $null
}

function Write-JsonFile([string]$Path, $Object) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $Object | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

function Get-Headers {
    $headers = @{
        'User-Agent' = 'AcerHelper-build'
        'Accept'     = 'application/vnd.github+json'
    }
    $token = $env:GH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GITHUB_TOKEN }
    if (-not [string]::IsNullOrWhiteSpace($token)) { $headers['Authorization'] = "Bearer $token" }
    return $headers
}

function Select-ReleaseAsset($Release, [string]$Pattern) {
    $assets = @($Release.assets)
    if ($assets.Count -eq 0) { throw "release '$($Release.tag_name)' has no assets" }
    $match = $assets | Where-Object { $_.name -like $Pattern } | Select-Object -First 1
    if ($null -eq $match) {
        $names = ($assets | ForEach-Object { $_.name }) -join ', '
        throw "no asset matching '$Pattern' in release '$($Release.tag_name)' (assets: $names)"
    }
    return $match
}

function Extract-ZipEntry([string]$ZipPath, [string]$EntryName, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq $EntryName } | Select-Object -First 1
        if ($null -eq $entry) { throw "'$EntryName' not found in $ZipPath" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    finally { $zip.Dispose() }
}

# ---------------------------------------------------------------------------------------------------
# Verification / staging
# ---------------------------------------------------------------------------------------------------

# Full checks for a module we are about to publish: the optional pin and the export names.
function Assert-Module([string]$ModulePath, [string]$Tag) {
    $hash = Get-Sha256 $ModulePath
    if (-not [string]::IsNullOrWhiteSpace($PinnedSha256)) {
        $want = Get-DigestHex $PinnedSha256
        if ([string]::IsNullOrWhiteSpace($want)) {
            throw "INTEGRITY: -PinnedSha256 '$PinnedSha256' is not a SHA-256 digest"
        }
        if ($hash -ne $want) {
            throw "INTEGRITY: module sha256 $hash does not match the pinned $want (tag $Tag)"
        }
    }
    $exportError = Test-ModuleExports -Path $ModulePath -Exports $ExpectedExports
    if ($exportError) { throw "INTEGRITY: $exportError (tag $Tag)" }
    return $hash
}

function Publish-Module([string]$ModulePath, [string]$Tag) {
    $hash = Assert-Module -ModulePath $ModulePath -Tag $Tag
    $stageDir = Split-Path -Parent $OutputPath
    if ($stageDir -and -not (Test-Path -LiteralPath $stageDir)) {
        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    }
    $needCopy = $true
    if (Test-Path -LiteralPath $OutputPath) {
        if ((Get-Sha256 $OutputPath) -eq $hash) { $needCopy = $false }
    }
    if ($needCopy) { Copy-Item -LiteralPath $ModulePath -Destination $OutputPath -Force }
    $size = (Get-Item -LiteralPath $ModulePath).Length
    Write-Host ("RYZENSMU: staged {0} ({1} bytes, sha256 {2})" -f $Tag, $size, $hash)
    return $hash
}

# A cached module is trustworthy when its bytes still match the SHA-256 recorded when it was fetched.
function Test-CachedModule([string]$ModulePath, [string]$TagDir) {
    if (-not (Test-Path -LiteralPath $ModulePath)) { return $false }
    $recorded = Read-JsonFile (Join-Path $TagDir 'resolved.json')
    if ($null -eq $recorded) { return $false }
    if (-not ($recorded.PSObject.Properties.Name -contains 'moduleSha256')) { return $false }
    $recordedHash = [string]$recorded.moduleSha256
    if ([string]::IsNullOrWhiteSpace($recordedHash)) { return $false }
    return ((Get-Sha256 $ModulePath) -eq $recordedHash)
}

function Write-ResolvedMetadata([string]$Tag, $Asset, [string]$AssetDigestHex, [string]$ModuleHash, [long]$ModuleSize) {
    $tagDir = Join-Path $CacheDir $Tag
    if (-not (Test-Path -LiteralPath $tagDir)) { New-Item -ItemType Directory -Force -Path $tagDir | Out-Null }
    $resolved = [ordered]@{
        repo         = $Repo
        tag          = $Tag
        asset        = [string]$Asset.name
        assetUrl     = [string]$Asset.browser_download_url
        assetDigest  = $AssetDigestHex
        module       = $ModuleName
        moduleSha256 = $ModuleHash
        moduleSize   = $ModuleSize
        fetchedUtc   = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-JsonFile -Path (Join-Path $tagDir 'resolved.json') -Object $resolved
    Write-JsonFile -Path (Join-Path $CacheDir 'latest.json') -Object ([ordered]@{
        tag         = $Tag
        asset       = [string]$Asset.name
        resolvedUtc = $resolved.fetchedUtc
    })
}

function Fetch-ModuleFromRelease($Release, $Headers) {
    $tag = [string]$Release.tag_name
    if ([string]::IsNullOrWhiteSpace($tag)) { throw 'the upstream release has no tag' }
    $asset = Select-ReleaseAsset -Release $Release -Pattern $AssetPattern
    $assetDigestHex = $null
    if ($asset.PSObject.Properties.Name -contains 'digest') { $assetDigestHex = Get-DigestHex ([string]$asset.digest) }

    $tagDir = Join-Path $CacheDir $tag
    $modulePath = Join-Path $tagDir $ModuleName

    if (-not $Force -and (Test-CachedModule -ModulePath $modulePath -TagDir $tagDir)) {
        $hash = Publish-Module -ModulePath $modulePath -Tag $tag
        $size = (Get-Item -LiteralPath $modulePath).Length
        Write-ResolvedMetadata -Tag $tag -Asset $asset -AssetDigestHex $assetDigestHex -ModuleHash $hash -ModuleSize $size
        if (-not $assetDigestHex) {
            Write-Host "RYZENSMU: upstream published no digest for $($asset.name); recorded module sha256 $hash"
        }
        return
    }

    if (-not (Test-Path -LiteralPath $tagDir)) { New-Item -ItemType Directory -Force -Path $tagDir | Out-Null }
    $zipPath = Join-Path $tagDir ([string]$asset.name)
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $Headers -OutFile $zipPath `
                      -UseBasicParsing -TimeoutSec $TimeoutSec

    if ($assetDigestHex) {
        $zipHash = Get-Sha256 $zipPath
        if ($zipHash -ne $assetDigestHex) {
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
            throw "INTEGRITY: $($asset.name) sha256 mismatch: release says $assetDigestHex, downloaded $zipHash"
        }
    }

    Extract-ZipEntry -ZipPath $zipPath -EntryName $ModuleName -Destination $modulePath
    $moduleHash = Publish-Module -ModulePath $modulePath -Tag $tag
    $size = (Get-Item -LiteralPath $modulePath).Length
    Write-ResolvedMetadata -Tag $tag -Asset $asset -AssetDigestHex $assetDigestHex -ModuleHash $moduleHash -ModuleSize $size
    if (-not $assetDigestHex) {
        Write-Host "RYZENSMU: upstream published no digest for $($asset.name); recorded module sha256 $moduleHash"
    }
}

function Use-CachedModule {
    $marker = Read-JsonFile (Join-Path $CacheDir 'latest.json')
    if ($null -eq $marker) { return $false }
    if (-not ($marker.PSObject.Properties.Name -contains 'tag')) { return $false }
    $tag = [string]$marker.tag
    if ([string]::IsNullOrWhiteSpace($tag)) { return $false }

    $tagDir = Join-Path $CacheDir $tag
    $modulePath = Join-Path $tagDir $ModuleName
    if (-not (Test-CachedModule -ModulePath $modulePath -TagDir $tagDir)) { return $false }

    try {
        Publish-Module -ModulePath $modulePath -Tag $tag | Out-Null
        Write-Host "RYZENSMU: using the cached $tag module (upstream was unavailable)"
        return $true
    }
    catch {
        Write-Host "RYZENSMU: the cached module failed verification: $($_.Exception.Message)"
        return $false
    }
}

# ---------------------------------------------------------------------------------------------------
# Offline self-test: the parsing/selection logic, exercised without touching the network.
# ---------------------------------------------------------------------------------------------------

function Invoke-SelfTest {
    $checks = 0

    $sampleJson = @'
{
  "tag_name": "0.2.11",
  "assets": [
    { "name": "release_0_2_11.zip",
      "browser_download_url": "https://example.invalid/release_0_2_11.zip",
      "digest": "sha256:43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4" }
  ]
}
'@
    $release = $sampleJson | ConvertFrom-Json

    $asset = Select-ReleaseAsset -Release $release -Pattern 'release_*.zip'
    if ($asset.name -ne 'release_0_2_11.zip') { throw "asset selection picked '$($asset.name)'" }
    $checks++

    $digest = Get-DigestHex 'sha256:43608CB89BC84247FEF1368A139013F7D043E17DB6D6C8DFC9B46BF0905A81F4'
    if ($digest -ne '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4') { throw 'prefixed digest not parsed' }
    $checks++

    if ($null -ne (Get-DigestHex 'not-a-digest')) { throw 'a non-digest was accepted' }
    $checks++

    if ((Get-DigestHex '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4') -ne '43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4') {
        throw 'bare digest not parsed'
    }
    $checks++

    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllBytes($tmp, [System.Text.Encoding]::ASCII.GetBytes(
            'xx ioctl_read_smu_register yy ioctl_write_smu_register zz'))
        if (Test-ModuleExports -Path $tmp -Exports 'ioctl_read_smu_register,ioctl_write_smu_register') {
            throw 'a present export was reported missing'
        }
        $checks++
        if (-not (Test-ModuleExports -Path $tmp -Exports 'ioctl_read_smu_register,ioctl_missing')) {
            throw 'a missing export was not detected'
        }
        $checks++
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

    Write-Host "SELFTEST-PASS $checks"
}

# ---------------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------------

if ($SelfTest) {
    try { Invoke-SelfTest; exit 0 }
    catch { Write-Host "SELFTEST-FAIL: $($_.Exception.Message)"; exit 1 }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Host 'RYZENSMU-UNAVAILABLE: no -OutputPath was given'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($CacheDir)) {
    $CacheDir = Join-Path -Path (Get-Location).Path -ChildPath 'obj/ryzensmu'
}
if (-not (Test-Path -LiteralPath $CacheDir)) { New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null }

$exitCode = 1
try {
    $headers = Get-Headers
    $apiBase = 'https://api.github.com'
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_API_URL)) { $apiBase = $env:GITHUB_API_URL.TrimEnd('/') }

    if (-not [string]::IsNullOrWhiteSpace($PinnedVersion)) {
        $url = "$apiBase/repos/$Repo/releases/tags/$PinnedVersion"
    }
    else {
        $url = "$apiBase/repos/$Repo/releases/latest"
    }

    $release = Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec $TimeoutSec
    Fetch-ModuleFromRelease -Release $release -Headers $headers
    $exitCode = 0
}
catch {
    $message = "$($_.Exception.Message)"
    if ($message -like 'INTEGRITY:*') {
        Write-Host "RYZENSMU-INTEGRITY-FAILURE: $($message.Substring(10).Trim())"
        $exitCode = 2
    }
    else {
        Write-Host "RYZENSMU-UNAVAILABLE: $message"
        $usedCache = $false
        try { $usedCache = Use-CachedModule } catch { $usedCache = $false }
        if ($usedCache) {
            $exitCode = 0
        }
        else {
            Write-Host 'RYZENSMU: no cached module to fall back to; the CPU-undervolt feature will be hidden in this build'
            $exitCode = 1
        }
    }
}
exit $exitCode
