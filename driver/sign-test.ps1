<#
.SYNOPSIS
    Test-sign the container-built AcerHelperLampArray driver package, using the signing tools that ship inside
    the WDK/SDK NuGet packages — so no Visual Studio and no WDK installation are needed on this machine.

.DESCRIPTION
    Steps 1-4 only produce files and touch nothing else:
      1. pull signtool.exe / inf2cat.exe out of the acerhelper-wdk container image into driver/out/tools
      2. create (or reuse) a self-signed code-signing certificate in the CURRENT USER store
      3. inf2cat -> AcerHelperLampArray.cat
      4. signtool -> sign the .cat and embed a signature in the .sys

    -Trust additionally installs that certificate into LocalMachine\Root and LocalMachine\TrustedPublisher,
    which is what makes Windows accept the package. That is a real change to the machine's trust configuration
    and needs an elevated shell, so it is opt-in.

    A test-signed driver ALSO requires test signing to be enabled, which requires Secure Boot to be off. This
    script never does that for you; it prints the command. On a machine you are not willing to take out of
    Secure Boot, the only alternative is an attestation-signed package (see README.md).

.EXAMPLE
    pwsh -File driver/sign-test.ps1
    pwsh -File driver/sign-test.ps1 -Trust     # elevated
#>
[CmdletBinding()]
param(
    [string] $Image   = 'acerhelper-wdk',
    [string] $OutDir  = (Join-Path $PSScriptRoot 'AcerHelperLampArray/out'),
    [string] $Subject = 'CN=Acer Helper LampArray (test)',
    [switch] $Trust
)

$ErrorActionPreference = 'Stop'

function Get-ToolFromImage {
    param([string] $Pattern, [string] $PathFilter, [string] $Destination)

    if (Test-Path $Destination) { return $Destination }
    $inside = docker run --rm --entrypoint sh $Image -c "find /opt/wdk -iname '$Pattern' -ipath '$PathFilter' | head -1"
    if (-not $inside) { throw "$Pattern not found in image $Image" }
    # `docker cp` needs a container, not an image: make a throwaway one, copy, drop it.
    $id = docker create $Image
    try   { docker cp "${id}:$($inside.Trim())" $Destination | Out-Null }
    finally { docker rm -f $id | Out-Null }
    return $Destination
}

if (-not (Test-Path (Join-Path $OutDir 'AcerHelperLampArray.sys'))) {
    throw "no driver in $OutDir — build it first: docker run --rm -v `"$PSScriptRoot/AcerHelperLampArray:/src`" $Image"
}

$tools = Join-Path $OutDir 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null

Write-Host '== extracting signing tools ==' -ForegroundColor Cyan
$signtool = Get-ToolFromImage -Pattern 'signtool.exe' -PathFilter '*x64*' -Destination (Join-Path $tools 'signtool.exe')
$inf2cat  = Get-ToolFromImage -Pattern 'inf2cat.exe'  -PathFilter '*'     -Destination (Join-Path $tools 'inf2cat.exe')
Write-Host "  $signtool"
Write-Host "  $inf2cat"

Write-Host '== certificate ==' -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
if (-not $cert) {
    # Three years is plenty for a dev certificate, and it never leaves this machine.
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
                                      -CertStoreLocation Cert:\CurrentUser\My `
                                      -NotAfter (Get-Date).AddYears(3)
    Write-Host "  created $($cert.Thumbprint)"
} else {
    Write-Host "  reusing $($cert.Thumbprint)"
}

Write-Host '== catalog ==' -ForegroundColor Cyan
# inf2cat hashes every file the INF references, so it must run against the folder holding both .inf and .sys.
& $inf2cat /driver:$OutDir /os:10_X64 /verbose
if ($LASTEXITCODE -ne 0) { throw "inf2cat failed ($LASTEXITCODE)" }

Write-Host '== signing ==' -ForegroundColor Cyan
foreach ($file in 'AcerHelperLampArray.cat', 'AcerHelperLampArray.sys') {
    & $signtool sign /v /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 `
                (Join-Path $OutDir $file)
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $file ($LASTEXITCODE)" }
}

if ($Trust) {
    Write-Host '== trusting the certificate (machine-wide) ==' -ForegroundColor Yellow
    $pfx = Join-Path $OutDir 'test-cert.pfx'
    $pwd = ConvertTo-SecureString -String ([guid]::NewGuid().ToString()) -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null
    try {
        foreach ($store in 'Root', 'TrustedPublisher') {
            Import-PfxCertificate -FilePath $pfx -Password $pwd `
                                  -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
            Write-Host "  installed into LocalMachine\$store"
        }
    } finally { Remove-Item $pfx -Force -ErrorAction SilentlyContinue }
} else {
    Write-Host 'skipped trusting the certificate (-Trust, elevated, installs it into Root + TrustedPublisher)' `
               -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Next, on a machine you are willing to take out of Secure Boot:' -ForegroundColor Cyan
Write-Host '  bcdedit /set testsigning on     (then reboot)'
Write-Host "  pnputil /add-driver `"$OutDir\AcerHelperLampArray.inf`" /install"
