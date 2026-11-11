<#
.SYNOPSIS
  Reproducible Microsoft Store release pipeline for Swiftwave Tweaks (MSI/EXE program path).

.DESCRIPTION
  1. Verifies the current v3.1 project sources and toolchain.
  2. Clean-builds the standalone self-contained EXE (existing build-release.ps1 flow).
  3. Authenticode-signs the EXE (required by Microsoft Store for MSI/EXE submissions).
  4. Builds the per-machine x64 MSI with WiX v5 and signs it.
  5. Validates package identity, version, architecture, icon and signatures.
  6. Publishes the immutable, versioned installer at a DIRECT (no-redirect) GitHub
     Pages URL: https://<owner>.github.io/<repo>/<version>/<msi>. The signed MSI is
     staged on a GitHub Release and the deploy-msi-pages.yml Actions workflow mirrors
     it to the gh-pages branch. Existing versioned URLs are never overwritten.
  7. Updates the Partner Center draft submission via the official MSStore CLI.
  8. Submits, polls certification and reports the real Store status.

  The script NEVER publishes an unsigned or validation-failing package.
  Credentials are read only from environment variables and are never written to logs.

.PARAMETER ProductId
  Partner Center product ID. Defaults to $env:MSSTORE_PRODUCT_ID.

.PARAMETER PackageUrl
  An existing immutable, DIRECT (no HTTP redirect) HTTPS URL to the already-signed MSI.
  Redirecting URLs (e.g. plain GitHub Releases asset links) are rejected because
  Partner Center refuses them. When omitted and -PublishGitHubRelease is set, the
  MSI is staged on a GitHub Release and mirrored to GitHub Pages automatically.

.PARAMETER PublishGitHubRelease
  Stage the signed MSI on the GitHub Release for -GitHubTag and trigger the
  deploy-msi-pages.yml workflow that mirrors it to the immutable GitHub Pages URL
  https://<owner>.github.io/<repo>/<version>/<msi> (requires $env:GITHUB_TOKEN).
  Fails if the versioned Pages URL or the release asset already exists.

.PARAMETER GitHubTag
  Release tag to stage the installer under and derive the Pages version from.
  Defaults to v3.1. (Tag 'v3.1.1' -> Pages path /3.1.1/.)

.PARAMETER SkipSigning
  Build/package only, without signatures. Forces -SkipSubmit (unsigned MSI/EXE
  packages are rejected by Microsoft Store certification).

.PARAMETER SkipSubmit
  Build (and optionally upload) but do not touch Partner Center.

.PARAMETER PollMinutes
  How long to poll certification after submitting. Certification can take up to
  three business days; re-run "msstore submission status <id>" afterwards. Default 30.

.EXAMPLE
  $env:SWT_CODESIGN_THUMBPRINT='AB12...'; $env:GITHUB_TOKEN='ghp_...'
  $env:MSSTORE_TENANT_ID='...'; $env:MSSTORE_SELLER_ID='...'
  $env:MSSTORE_CLIENT_ID='...'; $env:MSSTORE_CLIENT_SECRET='...'
  $env:MSSTORE_PRODUCT_ID='08fbc7df-0942-4416-aecb-4f486a3145dc'  # EXE/MSI apps use the Partner Center GUID
  .\release-store.ps1 -PublishGitHubRelease
#
# Future versions: bump <Version> in the csproj, set -GitHubTag v<version>,
# and the MSI is mirrored to https://swiftwave.github.io/swiftwave-tweaks/<version>/...
# Existing versioned URLs are never overwritten.
#
.NOTES
  Manual one-time prerequisites (the script cannot and must not do these):

  CODE SIGNING (required for every EXE/MSI Store submission):
    An OV or EV code-signing certificate chaining to a CA in the Microsoft
    Trusted Root Program. Microsoft does not re-sign EXE/MSI. Provide it either
    as a cert in CurrentUser\My / LocalMachine\My (set SWT_CODESIGN_THUMBPRINT)
    or as a PFX (SWT_CODESIGN_PFX + SWT_CODESIGN_PFX_PASSWORD). Never paste the
    certificate or password into chat or commit them.

  MICROSOFT STORE SUBMISSION API (Entra ID) - one time, manual:
    1. Partner Center -> Account settings -> Tenants: associate (or create) the
       Entra ID directory used by the developer account.
    2. Account settings -> Users -> add/create a Microsoft Entra application and
       assign it the Manager role (required by the Submission API).
    3. Open the application record, copy Tenant ID and Client ID.
    4. Entra admin center -> App registrations -> <app> -> Certificates &
       secrets -> New client secret; copy the secret value immediately.
    5. Copy the Seller ID from Account settings -> Organization profile ->
       Legal / Seller info, and use the app's Partner Center ID as Product ID
       (EXE/MSI apps: the GUID, e.g. 08fbc7df-0942-4416-aecb-4f486a3145dc).
    Then set these environment variables yourself - never in the repo or logs:
      MSSTORE_TENANT_ID, MSSTORE_SELLER_ID, MSSTORE_CLIENT_ID,
      MSSTORE_CLIENT_SECRET, MSSTORE_PRODUCT_ID

  GITHUB:
    A fine-grained/classic PAT scoped to Swiftwave/swiftwave-tweaks with
    Contents:Read/Write (and Actions:Read) in GITHUB_TOKEN, used to stage the
    release asset and poll the Pages sync workflow. The workflow itself uses the
    ephemeral GITHUB_TOKEN Actions provides automatically.
#>
[CmdletBinding()]
param(
    [string]$ProductId = $env:MSSTORE_PRODUCT_ID,
    [string]$PackageUrl,
    [switch]$PublishGitHubRelease,
    [string]$GitHubTag = 'v3.1.0',
    [switch]$SkipSigning,
    [switch]$SkipSubmit,
    [int]$PollMinutes = 30
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$releaseDir = Join-Path $root 'release'
$artifactsDir = Join-Path $root 'store-artifacts'
$version = '3.1.0'
$msiName = 'SwiftwaveTweaks-3.1.0-x64.msi'
$msiPath = Join-Path $artifactsDir $msiName
$repoOwner = 'Swiftwave'
$repoName = 'swiftwave-tweaks'

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Fail($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red; throw $m }
function Need($path, $label = $path) { if (-not (Test-Path $path)) { Fail "missing $label" } }

# ---------------------------------------------------------------- 1. preconditions
Step 'Verify project and toolchain'
Need (Join-Path $root 'GameReadyOptimizer.csproj')
Need (Join-Path $root 'app.manifest')
Need (Join-Path $root 'installer\SwiftwaveTweaks.wxs')
$csprojText = Get-Content (Join-Path $root 'GameReadyOptimizer.csproj') -Raw
if ($csprojText -notmatch '<Version>3\.1\.0</Version>') { Fail 'csproj is not the v3.1 project (<Version>3.1.0</Version> expected)' }
if ($csprojText -notmatch '<SelfContained>true</SelfContained>' -or $csprojText -notmatch '<PublishSingleFile>true</PublishSingleFile>') { Fail 'csproj no longer self-contained single-file publish' }
Ok 'project is Swiftwave Tweaks v3.1'

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) { Fail 'dotnet SDK not found' }
Ok "dotnet: $(& dotnet --version)"

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    $toolWix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path $toolWix) { $env:Path = "$env:USERPROFILE\.dotnet\tools;$env:Path" } else { Fail 'WiX v5 not found. Install: dotnet tool install --global wix --version 5.0.2' }
}
$wixVer = (& wix --version | Out-String).Trim()
if ($wixVer -notlike '5.*') { Fail "WiX v5 required, found $wixVer" }
Ok "wix: $wixVer"

# ---------------------------------------------------------------- 2. clean build
Step 'Clean build standalone EXE (existing canonical process)'
& (Join-Path $root 'build-release.ps1')
$exePath = Join-Path $releaseDir 'SwiftwaveTweaks.exe'
Need $exePath 'release\SwiftwaveTweaks.exe'
Ok "standalone EXE built: $exePath"

# ---------------------------------------------------------------- 3. verify EXE
Step 'Verify EXE identity, version and architecture'
$vi = (Get-Item $exePath).VersionInfo
if ($vi.ProductName -ne 'Swiftwave Tweaks') { Fail "ProductName '$($vi.ProductName)'" }
if ($vi.FileVersion -ne '3.1.0.0' -or $vi.ProductVersion -ne '3.1.0') { Fail "version $($vi.FileVersion)/$($vi.ProductVersion)" }
# PE machine type: bytes at 0x3C -> PE offset; machine must be 0x8664 (AMD64)
$fs = [IO.File]::OpenRead($exePath); $br = New-Object IO.BinaryReader($fs)
$fs.Position = 0x3C; $peOff = $br.ReadInt32(); $fs.Position = $peOff + 4; $machine = $br.ReadUInt16(); $br.Close(); $fs.Close()
if ($machine -ne 0x8664) { Fail ('EXE architecture is not x64 (machine=0x{0:X4})' -f $machine) }
Ok 'Swiftwave Tweaks 3.1.0, x64, self-contained single-file'

# ---------------------------------------------------------------- 4. sign EXE
$signExe = $false
if (-not $SkipSigning) {
    Step 'Authenticode sign SwiftwaveTweaks.exe'
    $thumb = $env:SWT_CODESIGN_THUMBPRINT
    $pfx = $env:SWT_CODESIGN_PFX
    if (-not $thumb -and -not $pfx) {
        Fail 'no code-signing certificate configured. Set SWT_CODESIGN_THUMBPRINT (cert in CurrentUser\My) or SWT_CODESIGN_PFX (+ SWT_CODESIGN_PFX_PASSWORD). Microsoft Store rejects unsigned EXE/MSI.'
    }
    if ($thumb) {
        $cert = Get-ChildItem Cert:\CurrentUser\My,Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
            Where-Object Thumbprint -eq $thumb | Select-Object -First 1
        if (-not $cert) { Fail "certificate $thumb not found in CurrentUser\My/LocalMachine\My" }
        $s = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert `
             -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.sectigo.com'
    } else {
        Need $pfx 'SWT_CODESIGN_PFX'
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfx, $env:SWT_CODESIGN_PFX_PASSWORD,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet)
        $s = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert `
             -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.sectigo.com'
    }
    if ($s.Status -ne 'Valid') { Fail "EXE signature: $($s.Status) $($s.StatusMessage)" }
    Ok "EXE signed ($($s.SignerCertificate.Subject))"
    $signExe = $true
} else {
    Write-Warning 'SkipSigning: package will be UNSIGNED. Submit is disabled (Store certification requires Authenticode).'
    $SkipSubmit = $true
}

# ---------------------------------------------------------------- 5. build MSI
Step 'Build x64 MSI'
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
Push-Location $root
try {
    & wix build .\installer\SwiftwaveTweaks.wxs -arch x64 -o $msiPath
    if ($LASTEXITCODE -ne 0) { Fail 'wix build failed' }
} finally { Pop-Location }
Remove-Item (Join-Path $artifactsDir '*.wixpdb') -Force -ErrorAction SilentlyContinue
Need $msiPath
Ok "MSI built: $msiPath ($([math]::Round((Get-Item $msiPath).Length/1MB,1)) MB)"

# ---------------------------------------------------------------- 6. verify MSI
Step 'Verify MSI tables, platform and embedded payload'
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msiPath, 0)
function MsiProp($name) {
    $view = $db.OpenView("SELECT Value FROM Property WHERE Property = '$name'")
    $view.Execute()
    $rec = $view.Fetch()
    if ($rec) { $rec.StringData(1) }
}
if ((MsiProp 'ProductName') -ne 'Swiftwave Tweaks') { Fail 'MSI ProductName mismatch' }
if ((MsiProp 'ProductVersion') -ne $version) { Fail 'MSI ProductVersion mismatch' }
if ((MsiProp 'Manufacturer') -ne 'Swiftwave') { Fail 'MSI Manufacturer mismatch' }
if ((MsiProp 'ALLUSERS') -ne '1') { Fail 'MSI must be per-machine (ALLUSERS=1) for /qn Store install' }
$si = $db.SummaryInformation(0)
$template = $si.Property(7)
if ($template -notmatch 'x64') { Fail "MSI template '$template' is not x64" }
Ok 'MSI: Swiftwave Tweaks 3.1.0, x64, per-machine, en-US, StartMenu shortcut + ARP metadata'

if ($signExe) {
    Step 'Authenticode sign MSI'
    if ($thumb) {
        $s = Set-AuthenticodeSignature -FilePath $msiPath -Certificate $cert `
             -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.sectigo.com'
    } else {
        $s = Set-AuthenticodeSignature -FilePath $msiPath -Certificate $cert `
             -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.sectigo.com'
    }
    if ($s.Status -ne 'Valid') { Fail "MSI signature: $($s.Status) $($s.StatusMessage)" }
    Ok 'MSI signed and timestamped'
}
foreach ($f in $exePath,$msiPath) {
    $sig = Get-AuthenticodeSignature $f
    Write-Host ("  signature {0}: {1}" -f (Split-Path $f -Leaf), $sig.Status)
}

# ------------------------------------------------- 7. immutable installer URL
if (-not $SkipSubmit -or $PublishGitHubRelease -or $PackageUrl) {
    Step 'Prepare immutable versioned installer URL (GitHub Pages, direct/no redirect)'

    # HARD GATE: an unsigned package must never be staged on a release, mirrored to
    # the public immutable Pages URL, or handed to Partner Center.
    if (-not $signExe) {
        Fail 'refusing to host/submit an unsigned package. Configure signing via SWT_CODESIGN_THUMBPRINT or SWT_CODESIGN_PFX (+ password).'
    }

    $pagesUrl = "https://$repoOwner.github.io/$repoName/$version/$msiName"

    # HEAD without following redirects. Partner Center rejects URLs that redirect,
    # so a 3xx Location is a failure here, not something to follow.
    function Test-DirectPackageUrl([string]$u, [long]$expectLength) {
        try {
            $rq = [System.Net.HttpWebRequest]::Create($u)
            $rq.AllowAutoRedirect = $false
            $rq.Method = 'HEAD'
            $rp = $rq.GetResponse()
            $code = [int]$rp.StatusCode
            $loc = $rp.Headers['Location']
            $len = $rp.ContentLength
            $rp.Close()
        } catch { Fail "package URL not reachable: $u ($($_.Exception.Message))" }
        if ($code -ne 200 -or $loc) { Fail "package URL must be a direct HTTP 200 link with no redirect (got $code, Location=${loc}): $u" }
        if ($expectLength -gt 0 -and $len -ne $expectLength) { Fail "package URL size $len does not match signed MSI ${expectLength}: $u" }
    }

    if ($PackageUrl) {
        Test-DirectPackageUrl $PackageUrl 0
        Ok "using provided direct package URL: $PackageUrl"
    } else {
        if (-not $PublishGitHubRelease) { Fail 'provide -PackageUrl or -PublishGitHubRelease' }
        if ($GitHubTag -ne "v$version") { Fail "tag/version mismatch: -GitHubTag '$GitHubTag' must equal 'v$version' so the Pages path matches the package version" }
        $token = $env:GITHUB_TOKEN
        if (-not $token) { Fail 'GITHUB_TOKEN required to stage the release asset and trigger the Pages sync workflow' }
        $auth = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        $api = "https://api.github.com/repos/$repoOwner/$repoName"
        $localSize = (Get-Item $msiPath).Length

        # Immutability pre-check: a versioned Pages URL that is already live must never change.
        $pagesExists = $false
        try {
            $rq = [System.Net.HttpWebRequest]::Create($pagesUrl)
            $rq.AllowAutoRedirect = $false; $rq.Method = 'HEAD'
            $rp = $rq.GetResponse(); $pagesExists = ([int]$rp.StatusCode -eq 200); $rp.Close()
        } catch { $pagesExists = $false }
        if ($pagesExists) { Fail "immutable package URL already exists: $pagesUrl - publish a NEW version instead of replacing a published package" }

        # Create the release as a DRAFT so the release->Pages workflow only fires
        # after the MSI asset has finished uploading.
        try {
            $rel = Invoke-RestMethod "$api/releases/tags/$GitHubTag" -Headers $auth
        } catch {
            $body = @{ tag_name = $GitHubTag; name = "Swiftwave Tweaks $version"; draft = $true; prerelease = $false } | ConvertTo-Json
            $rel = Invoke-RestMethod "$api/releases" -Method Post -Headers $auth -ContentType 'application/json' -Body $body
            Ok "created draft GitHub release $GitHubTag"
        }
        $existing = $rel.assets | Where-Object name -eq $msiName
        if ($existing) { Fail "release $GitHubTag already contains asset $msiName - release assets are immutable; use a new version/tag" }

        $up = "https://uploads.github.com/repos/$repoOwner/$repoName/releases/$($rel.id)/assets?name=$msiName"
        $bytes = [IO.File]::ReadAllBytes($msiPath)
        Invoke-RestMethod $up -Method Post -Headers ($auth + @{ 'Content-Type' = 'application/octet-stream' }) -Body $bytes | Out-Null
        Ok "staged signed installer on release $GitHubTag"

        $triggeredAt = (Get-Date).ToUniversalTime()
        if ($rel.draft) {
            # Publishing fires deploy-msi-pages.yml via the release:published event.
            Invoke-RestMethod "$api/releases/$($rel.id)" -Method Patch -Headers $auth -ContentType 'application/json' `
                -Body (@{ draft = $false } | ConvertTo-Json) | Out-Null
            Ok "published release $GitHubTag - Pages sync workflow triggered"
        } else {
            $wfBody = @{ ref = 'main'; inputs = @{ tag = $GitHubTag; asset = $msiName; version = $version } } | ConvertTo-Json -Depth 5
            Invoke-RestMethod "$api/actions/workflows/deploy-msi-pages.yml/dispatches" -Method Post -Headers $auth -ContentType 'application/json' -Body $wfBody | Out-Null
            Ok "dispatched Pages sync workflow for existing release $GitHubTag"
        }

        # Wait for the workflow run that mirrors the asset onto gh-pages.
        $deadline = (Get-Date).AddMinutes(10)
        $runId = $null
        while ((Get-Date) -lt $deadline -and -not $runId) {
            Start-Sleep -Seconds 5
            try {
                $runs = Invoke-RestMethod "$api/actions/workflows/deploy-msi-pages.yml/runs?per_page=10" -Headers $auth
                $runId = ($runs.workflow_runs | Where-Object {
                    $_.status -in @('queued','in_progress','waiting') -and
                    ([datetime]$_.created_at).ToUniversalTime() -gt $triggeredAt.AddSeconds(-30) -and
                    $_.head_branch -eq 'main'
                } | Select-Object -First 1).id
            } catch { }
        }
        if (-not $runId) { Fail 'Pages sync workflow did not start within 10 minutes' }
        while ((Get-Date) -lt $deadline) {
            $run = Invoke-RestMethod "$api/actions/runs/$runId" -Headers $auth
            if ($run.status -eq 'completed') {
                if ($run.conclusion -ne 'success') { Fail "Pages sync workflow run $runId ended with conclusion '$($run.conclusion)' (an immutable URL may already exist)" }
                break
            }
            Start-Sleep -Seconds 10
        }
        $run = Invoke-RestMethod "$api/actions/runs/$runId" -Headers $auth
        if ($run.status -ne 'completed') { Fail 'Pages sync workflow timed out after 10 minutes' }

        Test-DirectPackageUrl $pagesUrl $localSize
        $PackageUrl = $pagesUrl
        Ok "immutable direct package URL: $PackageUrl"
    }
}

# ------------------------------------------------- 8. Partner Center submission
if ($SkipSubmit) {
    Step 'Done (submission skipped)'
    Write-Host "  Artifact: $msiPath"
    return
}

Step 'Configure MSStore CLI credentials'
$msstore = Get-Command msstore -ErrorAction SilentlyContinue
if (-not $msstore) { Fail 'MSStore CLI not found. Install: winget install "Microsoft Store Developer CLI"' }
$tenant = $env:MSSTORE_TENANT_ID; $seller = $env:MSSTORE_SELLER_ID
$client = $env:MSSTORE_CLIENT_ID; $secret = $env:MSSTORE_CLIENT_SECRET
if (-not $tenant -or -not $seller -or -not $client) {
    Fail 'set MSSTORE_TENANT_ID, MSSTORE_SELLER_ID, MSSTORE_CLIENT_ID (+ MSSTORE_CLIENT_SECRET), or run "msstore reconfigure" yourself once.'
}
if (-not $ProductId) { Fail 'set MSSTORE_PRODUCT_ID (the Partner Center ID of the reserved app)' }
if ($secret) {
    & msstore reconfigure --tenantId $tenant --sellerId $seller --clientId $client --clientSecret $secret | Out-Null
} else {
    & msstore reconfigure --tenantId $tenant --sellerId $seller --clientId $client | Out-Null
}
if ($LASTEXITCODE -ne 0) { Fail 'msstore reconfigure failed (check Entra application has Manager role)' }
Ok 'MSStore CLI configured (secret not logged)'

Step 'Fetch current draft submission'
$pkgJsonPath = Join-Path $artifactsDir 'submission-package.json'
& msstore submission get $ProductId | Out-File -Encoding utf8 $pkgJsonPath
if ($LASTEXITCODE -ne 0) { Fail "cannot read draft submission for $ProductId. The FIRST submission (incl. age ratings) must be created in Partner Center before API updates are allowed." }
$pkg = Get-Content $pkgJsonPath -Raw | ConvertFrom-Json
if (-not $pkg.packages) { $pkg | Add-Member -NotePropertyName packages -NotePropertyValue @() -Force }
if ($pkg.packages.Count -eq 0) { $pkg.packages = @([pscustomobject]@{}); $pkg = [pscustomobject]@{ packages = $pkg.packages } }
$p0 = $pkg.packages[0]
$fields = @{
    packageUrl          = $PackageUrl
    languages           = @('en-us')
    architectures       = @('X64')
    appType             = 'MSI'
    isSilentInstall     = $true
    installerParameters = '/qn /norestart'
}
foreach ($k in $fields.Keys) {
    if ($p0.PSObject.Properties.Name -contains $k) { $p0.$k = $fields[$k] }
    else { $p0 | Add-Member -NotePropertyName $k -NotePropertyValue $fields[$k] -Force }
}
$pkg | ConvertTo-Json -Depth 20 | Out-File -Encoding utf8 $pkgJsonPath
Ok "draft package updated: MSI, X64, silent '/qn /norestart'"

Step 'Update listing metadata (if store/listing.json exists)'
$listingPath = Join-Path $root 'store\listing.json'
if (Test-Path $listingPath) {
    & msstore submission updateMetadata $ProductId (Get-Content $listingPath -Raw) | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'updateMetadata rejected (schema differs in this draft); update listing fields in Partner Center' }
    else { Ok 'listing metadata updated' }
} else { Write-Host '  store/listing.json not present - leaving listing untouched' }

Step 'Push package into submission'
& msstore submission update $ProductId (Get-Content $pkgJsonPath -Raw)
if ($LASTEXITCODE -ne 0) { Fail 'submission update failed' }
Ok 'package uploaded into draft submission'

Step 'Submit to Microsoft Store'
& msstore submission publish $ProductId
if ($LASTEXITCODE -ne 0) { Fail 'publish/submit failed' }
Ok 'submission submitted - certification can take up to three business days'

Step "Poll certification (up to $PollMinutes min)"
$deadline = (Get-Date).AddMinutes($PollMinutes)
$last = ''
while ((Get-Date) -lt $deadline) {
    $status = (& msstore submission status $ProductId | Out-String).Trim()
    if ($status -ne $last) { Write-Host "  status: $status"; $last = $status }
    if ($status -match 'Publish|In the Store') { Write-Host 'PUBLICATION CONFIRMED' -ForegroundColor Green; break }
    if ($status -match 'Fail|Error|Reject') { Fail "submission failed certification: $status" }
    Start-Sleep -Seconds 60
}
Write-Host "`nFinal status: $last"
Write-Host 'Re-run later with: msstore submission status' $ProductId
