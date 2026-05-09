<#
.SYNOPSIS
    Builds, packages, and publishes Backlog Beater to GitHub Releases.

.PARAMETER Notes
    Release notes text. Defaults to "See plans.md for changes."

.PARAMETER DryRun
    Build and package the .pext without publishing to GitHub.

.PARAMETER ManifestOnly
    Skip build and release creation; only push update.json. Use this to recover
    from a partial run where the release was created but the manifest push failed.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Notes "Fixed modpack tag derivation for Prism instances."
    .\publish.ps1 -DryRun
    .\publish.ps1 -ManifestOnly
#>
[CmdletBinding()]
param(
    [string]$Notes = "See plans.md for changes.",
    [switch]$DryRun,
    [switch]$ManifestOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot     = $PSScriptRoot
$ProjectFile  = Join-Path $RepoRoot "GameRecommender.csproj"
$YamlPath     = Join-Path $RepoRoot "extension.yaml"
$BuildOutput  = Join-Path $RepoRoot "bin\Release\net462"
$DistDir      = Join-Path $RepoRoot "dist"
$GitHubRepo   = "Gab-ai/BacklogBeater"
$ManifestFile = "update.json"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Step([string]$msg) { Write-Host "`n$msg" -ForegroundColor Cyan }
function Ok([string]$msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Warn([string]$msg) { Write-Host "  $msg" -ForegroundColor Yellow }
function Fail([string]$msg) { Write-Error $msg }

# ---------------------------------------------------------------------------
# 1. Read version from extension.yaml
# ---------------------------------------------------------------------------

Step "Reading version..."
if (-not (Test-Path $YamlPath)) { Fail "extension.yaml not found at $YamlPath" }
$yamlText = Get-Content $YamlPath -Raw
if ($yamlText -notmatch '(?im)^\s*Version\s*:\s*(.+?)\s*$') {
    Fail "Could not parse Version from extension.yaml"
}
$Version     = $Matches[1].Trim()
$Tag         = "v$Version"
$PackageName = "BacklogBeater_$Version"
$StagingDir  = Join-Path $DistDir $PackageName
$PextPath    = Join-Path $DistDir "$PackageName.pext"

Ok "Version : $Version"
Ok "Tag     : $Tag"
Ok "Package : $PackageName.pext"
if ($DryRun) { Warn "[DRY RUN — no GitHub calls will be made]" }

# ---------------------------------------------------------------------------
# 2. Check prerequisites
# ---------------------------------------------------------------------------

Step "Checking prerequisites..."
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "gh CLI not found. Install from https://cli.github.com/ and run 'gh auth login'."
}
Ok "gh CLI found"

# ---------------------------------------------------------------------------
# 3. Build Release
# ---------------------------------------------------------------------------

Step "Building Release configuration..."
dotnet build $ProjectFile -c Release
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)." }
Ok "Build succeeded"

# ---------------------------------------------------------------------------
# 4. Stage package contents
# ---------------------------------------------------------------------------

Step "Staging $PackageName..."
if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir | Out-Null

# extension.yaml must be at the root of the .pext
Copy-Item $YamlPath (Join-Path $StagingDir "extension.yaml")

# DLL and all non-pdb files from build output, excluding DLLs Playnite provides at runtime
$playniteOwnedDlls = @("Playnite.SDK.dll", "Playnite.dll", "Playnite.Common.dll")
if (-not (Test-Path $BuildOutput)) {
    Fail "Build output not found at $BuildOutput — did the Release build succeed?"
}
Get-ChildItem $BuildOutput -File | Where-Object {
    $_.Extension -ne ".pdb" -and $playniteOwnedDlls -notcontains $_.Name
} | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $StagingDir $_.Name)
}

# Subdirectories except ref\ (ref\ holds facade assemblies not needed at runtime)
Get-ChildItem $BuildOutput -Directory | Where-Object { $_.Name -ne "ref" } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $StagingDir $_.Name) -Recurse
}
Ok "Staged to $StagingDir"

# ---------------------------------------------------------------------------
# 5. Pack into .pext
# ---------------------------------------------------------------------------

Step "Packing $PackageName.pext..."
if (Test-Path $PextPath) { Remove-Item $PextPath -Force }
$ZipPath = [System.IO.Path]::ChangeExtension($PextPath, ".zip")
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Push-Location $StagingDir
try {
    Compress-Archive -Path ".\*" -DestinationPath $ZipPath
} finally {
    Pop-Location
}
Rename-Item $ZipPath $PextPath
$sizeKb = [math]::Round((Get-Item $PextPath).Length / 1KB)
Ok "Created $PextPath ($sizeKb KB)"

# ---------------------------------------------------------------------------
# 6. Check for duplicate release tag
# ---------------------------------------------------------------------------

if ($ManifestOnly) {
    Step "Manifest-only mode — skipping build and release creation."
    Step "Resolving existing release $Tag from GitHub..."
    $releaseData = gh release view $Tag --repo $GitHubRepo --json assets 2>&1 | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { Fail "Could not find release $Tag on $GitHubRepo. Run without -ManifestOnly to create it." }
} else {
    Step "Checking GitHub for existing release $Tag..."
    $tagExists = $false
    try { $null = gh release view $Tag --repo $GitHubRepo 2>&1; $tagExists = ($LASTEXITCODE -eq 0) } catch { $tagExists = $false }
    if ($tagExists) { Fail "Release $Tag already exists on $GitHubRepo. Bump Version in extension.yaml first." }
    Ok "Tag $Tag is available"

    # -----------------------------------------------------------------------
    # 7. Dry-run exit
    # -----------------------------------------------------------------------

    if ($DryRun) {
        Write-Host ""
        Warn "DRY RUN complete. Would have run:"
        Warn "  gh release create $Tag --repo $GitHubRepo --title 'Backlog Beater $Tag' --notes '...' $PextPath"
        Warn "  gh api PUT /repos/$GitHubRepo/contents/$ManifestFile  (update.json manifest)"
        exit 0
    }

    # -----------------------------------------------------------------------
    # 8. Create GitHub Release
    # -----------------------------------------------------------------------

    Step "Creating GitHub Release $Tag..."
    $releaseOutput = gh release create $Tag $PextPath `
        --repo $GitHubRepo `
        --title "Backlog Beater $Tag" `
        --notes $Notes 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($releaseOutput -match "Repository is empty") {
            Fail "The $GitHubRepo repo has no commits. Push at least one commit (e.g. a README) before creating a release."
        }
        Fail "gh release create failed (exit $LASTEXITCODE): $releaseOutput"
    }
    Ok "Release created"

    Step "Resolving asset download URL..."
    $releaseData = gh release view $Tag --repo $GitHubRepo --json assets | ConvertFrom-Json
}

$assetUrl   = ($releaseData.assets |
               Where-Object { $_.name -like "*.pext" } |
               Select-Object -First 1).url
$releaseUrl = "https://github.com/$GitHubRepo/releases/tag/$Tag"
if ($assetUrl) { Ok "Asset URL : $assetUrl" } else { Warn "No .pext asset URL found; DownloadUrl will be empty." }

# ---------------------------------------------------------------------------
# 10. Push update.json manifest
# ---------------------------------------------------------------------------

Step "Updating $ManifestFile..."
$manifestObj = [ordered]@{
    Version     = $Version
    ReleaseUrl  = $releaseUrl
    DownloadUrl = if ($assetUrl) { $assetUrl } else { $releaseUrl }
    Notes       = $Notes
}
$manifestJson  = $manifestObj | ConvertTo-Json -Compress
$manifestB64   = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($manifestJson))

# Fetch existing file SHA so GitHub accepts the update (required for PUT on existing files)
$existingSha = $null
$existing = $null
try { $existing = gh api /repos/$GitHubRepo/contents/$ManifestFile 2>&1 | ConvertFrom-Json } catch { $existing = $null }
if ($existing -and $existing.sha) { $existingSha = $existing.sha }

$ghApiArgs = @(
    "api", "--method", "PUT",
    "/repos/$GitHubRepo/contents/$ManifestFile",
    "-f", "message=Update manifest for $Tag",
    "-f", "content=$manifestB64"
)
if ($existingSha) { $ghApiArgs += @("-f", "sha=$existingSha") }

& gh @ghApiArgs
if ($LASTEXITCODE -ne 0) { Fail "Failed to push $ManifestFile (exit $LASTEXITCODE)." }
Ok "Manifest pushed"

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "Released successfully!" -ForegroundColor Green
Write-Host "  Release : $releaseUrl"
Write-Host "  Manifest: https://raw.githubusercontent.com/$GitHubRepo/main/$ManifestFile"
