<#
.SYNOPSIS
    Builds a SuperBar release on this PC and publishes it to GitHub Releases, which is the in-app updater's feed.

.DESCRIPTION
    1. Checks the tools (dotnet, vpk, and gh + git state unless -DryRun).
    2. Cleans artifacts\publish and artifacts\releases.
    3. Downloads the previous release (when there is one), so Velopack can build a small delta package.
    4. dotnet publish: Release, win-x64, self-contained (.NET and the Windows App SDK ship inside the app).
    5. vpk pack: the installer (SuperBar-win-Setup.exe), the update packages and the feed (releases.win.json).
    6. Lists and verifies the artifacts.
    7. Unless -DryRun: tags the commit, creates the GitHub release and uploads every file the updater needs.

    vpk is a local dotnet tool pinned in dotnet-tools.json to the version of the Velopack package the app
    references; a vpk on PATH is used when the local tool is not available.

.PARAMETER Version
    The version to release, e.g. 0.1.0 (or 0.2.0-beta.1; a pre-release is published as a GitHub pre-release, which
    the in-app updater ignores).

.PARAMETER Notes
    Release notes as Markdown text.

.PARAMETER NotesFile
    A Markdown file with the release notes. Without -Notes and -NotesFile, GitHub generates the notes.

.PARAMETER DryRun
    Build and pack only. Nothing is tagged, pushed or published; the release command is printed instead.

.PARAMETER SkipGitTag
    Do not create and push the tag v<Version> with git first; GitHub then creates it on the built commit.

.EXAMPLE
    .\scripts\release.ps1 -Version 0.1.0 -DryRun

.EXAMPLE
    .\scripts\release.ps1 -Version 0.1.1 -Notes "Fixes the clock widget on 24-hour locales."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$Version,

    [string]$Notes,

    [string]$NotesFile,

    [switch]$DryRun,

    [switch]$SkipGitTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ----- What is being released ------------------------------------------------------------------------------------

$PackId      = 'SuperBar'            # The installer's identity and install folder (%LocalAppData%\SuperBar). Never change it.
$PackTitle   = 'SuperBar'
$MainExe     = 'SuperBar.exe'
$Runtime     = 'win-x64'
$Project     = 'src\SuperBar.App\SuperBar.App.csproj'
$Icon        = 'src\SuperBar.App\Assets\SuperBar.ico'
$PublishDir  = 'artifacts\publish'
$ReleasesDir = 'artifacts\releases'
$Tag         = "v$Version"

# ----- Helpers ---------------------------------------------------------------------------------------------------

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "== $Text" -ForegroundColor Cyan
}

# Quotes an argument for display, so a printed command can be pasted back into PowerShell.
function Format-Argument([string]$Argument) {
    if ($Argument -match '[\s"''`$;&|()]') { return "'" + $Argument.Replace("'", "''") + "'" }
    return $Argument
}

function Format-Command([string]$Exe, [string[]]$Arguments) {
    return (@($Exe) + @($Arguments | ForEach-Object { Format-Argument $_ })) -join ' '
}

# Runs a tool, echoes the command, and stops the script when it fails.
function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    Write-Host ('> ' + (Format-Command $Exe $Arguments)) -ForegroundColor DarkGray
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE." }
}

# Runs a tool as a question: returns its output as one string ('' when it printed nothing), or $null when it
# fails. Never stops the script.
# (Windows PowerShell 5.1 turns a tool's stderr into errors when it is redirected, hence the local preference.)
function Get-ToolOutput([string]$Exe, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return (@($output) -join "`n").Trim()
    }
    catch { return $null }
}

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-Vpk([string[]]$Arguments) {
    Invoke-Tool $script:VpkExe (@($script:VpkPrefix) + $Arguments)
}

# ----- Run from the repository root, whatever the current folder is ----------------------------------------------

$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot
try {
    Write-Host "SuperBar $Version  ($(if ($DryRun) { 'dry run: nothing is published' } else { 'RELEASE: publishes to GitHub' }))" -ForegroundColor Green

    # ----- 1. Tools and state ------------------------------------------------------------------------------------
    Write-Step '1/7  Checking tools'

    if ($Notes -and $NotesFile) { throw 'Pass -Notes or -NotesFile, not both.' }
    if ($NotesFile) {
        if (-not (Test-Path $NotesFile -PathType Leaf)) { throw "Notes file not found: $NotesFile" }
        $NotesFile = (Resolve-Path $NotesFile).Path
    }

    if (-not (Test-Command 'dotnet')) { throw 'dotnet was not found. Install the .NET SDK: https://dotnet.microsoft.com/download' }
    Write-Host "dotnet  $(dotnet --version)"

    # vpk: the pinned local tool first, a global vpk second.
    $script:VpkExe = $null
    $script:VpkPrefix = @()
    if (Test-Path 'dotnet-tools.json') { [void](Get-ToolOutput 'dotnet' @('tool', 'restore')) }
    if ($null -ne (Get-ToolOutput 'dotnet' @('vpk', '-h'))) {
        $script:VpkExe = 'dotnet'
        $script:VpkPrefix = @('vpk')
        Write-Host 'vpk     local tool (dotnet vpk, pinned in dotnet-tools.json)'
    }
    elseif (Test-Command 'vpk') {
        $script:VpkExe = 'vpk'
        Write-Host 'vpk     global tool (the local tool in dotnet-tools.json could not be restored)'
    }
    else {
        throw 'vpk (the Velopack CLI) was not found. Run "dotnet tool restore" in the repository, or "dotnet tool install -g vpk".'
    }

    foreach ($required in @($Project, $Icon)) {
        if (-not (Test-Path $required -PathType Leaf)) { throw "Missing file: $required" }
    }

    # The repository the release goes to, and the app reads its updates from: the origin remote.
    $origin = Get-ToolOutput 'git' @('remote', 'get-url', 'origin')
    if (-not $origin -or $origin -notmatch 'github\.com[:/](?<owner>[^/]+)/(?<name>[^/]+?)(\.git)?/?$') {
        throw 'The origin remote is not a GitHub repository.'
    }
    $Repo = "$($Matches.owner)/$($Matches.name)"
    $RepoUrl = "https://github.com/$Repo"
    Write-Host "repo    $RepoUrl"

    $hasGh = (Test-Command 'gh') -and ($null -ne (Get-ToolOutput 'gh' @('auth', 'status')))
    $commit = Get-ToolOutput 'git' @('rev-parse', 'HEAD')
    if (-not $commit) { throw 'This folder is not a git repository with a commit.' }

    if ($DryRun) {
        Write-Host "gh      $(if ($hasGh) { 'signed in' } else { 'not available (not needed for a dry run)' })"
    }
    else {
        if (-not $hasGh) { throw 'gh (the GitHub CLI) is missing or not signed in. Install it from https://cli.github.com and run "gh auth login".' }
        Write-Host 'gh      signed in'

        # A release must be reproducible from its tag: everything committed, and the commit on GitHub.
        if (Get-ToolOutput 'git' @('status', '--porcelain')) {
            throw 'The working tree has uncommitted changes. Commit (or stash) them first, so the tag matches what was built.'
        }
        [void](Get-ToolOutput 'git' @('fetch', 'origin', '--tags', '--quiet'))
        if (-not (Get-ToolOutput 'git' @('branch', '-r', '--contains', $commit))) {
            throw "The commit $($commit.Substring(0, 7)) is not on GitHub yet. Push the branch first."
        }
        if (Get-ToolOutput 'git' @('tag', '--list', $Tag)) { throw "The tag $Tag already exists. Choose a new version." }
        if ($null -ne (Get-ToolOutput 'gh' @('release', 'view', $Tag, '--repo', $Repo))) { throw "The release $Tag already exists on GitHub." }
    }
    Write-Host "commit  $($commit.Substring(0, 7))"

    # ----- 2. Clean ----------------------------------------------------------------------------------------------
    Write-Step '2/7  Cleaning output folders'
    foreach ($dir in @($PublishDir, $ReleasesDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        [void](New-Item $dir -ItemType Directory -Force)
        Write-Host "clean   $dir"
    }

    # ----- 3. Previous release, for the delta --------------------------------------------------------------------
    Write-Step '3/7  Fetching the previous release (for a delta package)'
    $hasPrevious = $null
    if ($hasGh) {
        $published = Get-ToolOutput 'gh' @('release', 'list', '--repo', $Repo, '--exclude-drafts', '--exclude-pre-releases', '--limit', '1', '--json', 'tagName', '--jq', '.[].tagName')
        if ($null -ne $published) { $hasPrevious = [bool]$published }
    }

    if ($hasPrevious -eq $false) {
        Write-Host 'No release has been published yet: this is the first one, so there is no delta to build.'
    }
    elseif ($hasPrevious) {
        # There is a previous release: failing to fetch it would silently ship a release without a delta.
        Invoke-Vpk @('download', 'github', '--repoUrl', $RepoUrl, '--outputDir', $ReleasesDir)
    }
    else {
        # gh could not tell (dry run without gh): try, and carry on without a delta.
        try { Invoke-Vpk @('download', 'github', '--repoUrl', $RepoUrl, '--outputDir', $ReleasesDir) }
        catch { Write-Warning 'No previous release could be downloaded. Continuing without a delta package.' }
    }
    $previousFiles = @(Get-ChildItem $ReleasesDir -File | ForEach-Object { $_.Name })

    # ----- 4. Publish --------------------------------------------------------------------------------------------
    Write-Step "4/7  dotnet publish ($Runtime, self-contained)"
    Invoke-Tool 'dotnet' @(
        'publish', $Project,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:Platform=x64',
        "-p:Version=$Version",
        '-o', $PublishDir,
        '-nologo')

    if (-not (Test-Path (Join-Path $PublishDir $MainExe))) { throw "$MainExe is missing from $PublishDir." }

    # ----- 5. Pack -----------------------------------------------------------------------------------------------
    Write-Step '5/7  vpk pack'
    $notesPath = $NotesFile
    if ($Notes) {
        $notesPath = Join-Path (Resolve-Path 'artifacts').Path "release-notes-$Version.md"
        [System.IO.File]::WriteAllText($notesPath, $Notes, (New-Object System.Text.UTF8Encoding($false)))
    }

    $packArguments = @(
        'pack',
        '--packId', $PackId,
        '--packTitle', $PackTitle,
        '--packVersion', $Version,
        '--packDir', $PublishDir,
        '--mainExe', $MainExe,
        '--icon', $Icon,
        '--runtime', $Runtime,
        '--outputDir', $ReleasesDir)
    if ($notesPath) { $packArguments += @('--releaseNotes', $notesPath) }
    Invoke-Vpk $packArguments

    # ----- 6. Verify ---------------------------------------------------------------------------------------------
    Write-Step '6/7  Artifacts'
    Get-ChildItem $ReleasesDir -File | Sort-Object Name | ForEach-Object {
        '{0,10:N1} MB  {1}{2}' -f ($_.Length / 1MB), $_.Name, $(if ($previousFiles -contains $_.Name -and $_.Extension -eq '.nupkg') { '   (previous release, not uploaded)' } else { '' })
    } | Write-Host

    $setup = "$PackId-win-Setup.exe"
    $fullPackage = "$PackId-$Version-full.nupkg"
    foreach ($name in @($setup, 'releases.win.json', 'RELEASES', $fullPackage)) {
        if (-not (Test-Path (Join-Path $ReleasesDir $name) -PathType Leaf)) { throw "Expected artifact is missing: $name" }
    }

    $feed = Get-Content (Join-Path $ReleasesDir 'releases.win.json') -Raw | ConvertFrom-Json
    if (-not ($feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' })) {
        throw "releases.win.json does not list a full package for $Version."
    }

    # What goes on the GitHub release: the installer, the portable zip, the feed files, and THIS version's packages
    # (full, plus the delta when there is one). The updater reads releases.win.json from each GitHub release and
    # downloads the packages attached to that same release, so the packages of earlier versions stay where they
    # are and are not uploaded again.
    $upload = @(Get-ChildItem $ReleasesDir -File | Where-Object {
            $_.Name -eq $setup -or
            $_.Name -eq "$PackId-win-Portable.zip" -or
            $_.Name -eq 'RELEASES' -or
            $_.Name -like 'releases.*.json' -or
            $_.Name -like 'assets.*.json' -or
            $_.Name -like "$PackId-$Version-*.nupkg"
        } | Sort-Object Name | ForEach-Object { Join-Path $ReleasesDir $_.Name })

    $hasDelta = [bool]($upload | Where-Object { $_ -like '*-delta.nupkg' })
    Write-Host ''
    Write-Host "OK: installer, feed and $(if ($hasDelta) { 'full + delta packages' } else { 'full package (no delta: nothing to diff against)' }) for $Version are in $ReleasesDir" -ForegroundColor Green

    # ----- 7. Publish to GitHub ----------------------------------------------------------------------------------
    $releaseArguments = @('release', 'create', $Tag) + $upload + @('--repo', $Repo, '--title', "SuperBar $Version")
    if ($notesPath) { $releaseArguments += @('--notes-file', $notesPath) } else { $releaseArguments += '--generate-notes' }
    if ($Version.Contains('-')) { $releaseArguments += '--prerelease' }
    if ($SkipGitTag) { $releaseArguments += @('--target', $commit) } else { $releaseArguments += '--verify-tag' }

    if ($DryRun) {
        Write-Step '7/7  Dry run: NOT publishing'
        Write-Host "Try the installer:  $(Join-Path $RepoRoot (Join-Path $ReleasesDir $setup))"
        Write-Host ''
        Write-Host "Running this script without -DryRun (from a clean, pushed commit) would then run:"
        if (-not $SkipGitTag) {
            Write-Host ('  ' + (Format-Command 'git' @('tag', '-a', $Tag, '-m', "SuperBar $Version"))) -ForegroundColor Yellow
            Write-Host ('  ' + (Format-Command 'git' @('push', 'origin', $Tag))) -ForegroundColor Yellow
        }
        Write-Host ('  ' + (Format-Command 'gh' $releaseArguments)) -ForegroundColor Yellow
        return
    }

    Write-Step "7/7  Publishing $Tag to $RepoUrl"
    if (-not $SkipGitTag) {
        Invoke-Tool 'git' @('tag', '-a', $Tag, '-m', "SuperBar $Version")
        Invoke-Tool 'git' @('push', 'origin', $Tag)
    }
    Invoke-Tool 'gh' $releaseArguments

    Write-Host ''
    Write-Host "Released: $RepoUrl/releases/tag/$Tag" -ForegroundColor Green
}
finally {
    Pop-Location
}
