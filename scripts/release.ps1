<#
.SYNOPSIS
PicoHex release helper - decide the next version, refresh API baselines, tag.

.DESCRIPTION
Implements the PicoHex version rule:

    <four-digit year>.<x>.<y>

      x bumps when the public API changed (y resets to 0)
      y bumps when it did not
      the year segment is the current year and does not by itself reset x/y
      (pass -ResetOnYearChange for repos that prefer per-year counters)

The public API of every packable project is compared against the committed
baseline in <RepoRoot>/api/<PackageId>.public.txt. A baseline always holds the
surface of the LAST RELEASE, so it is refreshed as part of the release commit.

What the tag actually does. Pushing a v* tag is the release trigger, and
.github/workflows/release.yml is the only workflow it starts. That workflow has
three jobs, chained:

    verify   (ubuntu-latest)                 restores, builds Release and runs
                                             every test project in the tree
    pack     (ubuntu-latest, needs: verify)  packs the projects listed in the
                                             workflow at the tag version and
                                             uploads the .nupkg artifacts
    publish  (ubuntu-latest, needs: pack)    pushes those artifacts to NuGet and
                                             creates the GitHub release

The pack list is hand-maintained inside release.yml; it is not derived from the
project scan this script performs. That scan asks MSBuild for each candidate's
IsPackable / PackageId / AssemblyName / TargetFramework (dotnet msbuild
-getProperty), so a property that lives in an imported props file is seen too -
reading the csproj's raw XML is what let v2026.12.0 delete the five .Gen
baselines - and it refuses to continue unless the count is exactly 11.
The test run, however, is not optional any more: verify runs all ten test
projects on ubuntu-latest and pack declares
`needs: verify`, so a tag whose tests fail stops before a single .nupkg is
built - v2026.11.0 went out while CI had been red for three days, which is the
failure that gate exists to prevent.

This script itself still runs no tests: it only builds (dotnet build -c Release)
and diffs the public surface. verify is one platform and build + tests only (the
five-platform matrix, the AOT validation and the benchmark canary stay in CI),
so the tag is still the wrong place to discover a first failure. Two gates then,
and the manual one comes first:

  - main must already be green in the FULL CI run before the tag is created;
    verify is a backstop, not a substitute - it re-runs one platform only.
  - confirm the CI verdict yourself anyway, e.g.
        gh run list --workflow=ci.yml --limit 1
    and expect a completed / success conclusion for the commit you are tagging.

.PARAMETER RepoRoot
Repository to release. Defaults to the current directory.

.PARAMETER DryRun
Report the API delta and the next version without touching the baselines,
committing, or tagging. The Release build still runs and a dirty working tree is
tolerated (only a real release requires a clean tree).

.PARAMETER Bootstrap
Create the api/ baselines. Use with -FromTag <tag> (preferred) or -AssumeCurrent.

.PARAMETER FromTag
Bootstrap source revision, e.g. v2026.9.0. Builds that tag in a temporary worktree.

.PARAMETER AssumeCurrent
Bootstrap from the current HEAD instead of a tag. The API delta since the last
release cannot be recovered this way; the next release compares against HEAD.

.PARAMETER BaselineDir
Baseline directory relative to RepoRoot. Default: api

.PARAMETER Push
After tagging, push the branch and the tag (this triggers the release workflow,
where verify runs the tests first and only then do pack and publish ship).

.PARAMETER ResetOnYearChange
Reset x and y when the year changes instead of carrying them forward.

.EXAMPLE
# once per repository: seed the baselines from the last release
./scripts/release.ps1 -Bootstrap -FromTag v2026.9.0

.EXAMPLE
# inspect the decision without changing the repository
./scripts/release.ps1 -DryRun

.EXAMPLE
# cut the release: refresh baselines, commit, tag, push
./scripts/release.ps1 -Push
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path,
    [switch]$DryRun,
    [switch]$Bootstrap,
    [string]$FromTag,
    [switch]$AssumeCurrent,
    [string]$BaselineDir = "api",
    [switch]$Push,
    [switch]$ResetOnYearChange
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$message) { Write-Host "== $message" -ForegroundColor Cyan }
function Write-Ok([string]$message) { Write-Host "   $message" -ForegroundColor Green }
function Write-Note([string]$message) { Write-Host "   $message" -ForegroundColor Yellow }
function Fail([string]$message) { Write-Host "error: $message" -ForegroundColor Red; exit 2 }

function Invoke-Native([string]$command, [string[]]$arguments) {
    # Native stderr output must not become a terminating error under
    # $ErrorActionPreference='Stop' (git writes progress lines to stderr).
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $command @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}

function Invoke-Git([string[]]$arguments) {
    $result = Invoke-Native "git" (@("-C", $RepoRoot) + $arguments)
    if ($result.ExitCode -ne 0) { throw "git $($arguments -join ' ') failed: $($result.Output)" }
    return $result.Output
}

function Invoke-GitRaw([string[]]$arguments) {
    return Invoke-Native "git" (@("-C", $RepoRoot) + $arguments)
}

function Invoke-Tool([string[]]$arguments) {
    $result = Invoke-Native "dotnet" (@($script:ToolDll) + $arguments)
    $script:ToolExit = $result.ExitCode
    return $result.Output
}

# The release is defined over ELEVEN packages: the pack list in
# .github/workflows/release.yml is hand-maintained and names exactly those. This
# scan must agree with it, so a different count is an ERROR, not a new fact - the
# way it goes wrong is a property that leaves the csproj (into an imported props
# file or a Directory.Build.props) while the scan keeps reading raw XML, which is
# how v2026.12.0 silently dropped the five api/*.Gen.public.txt baselines. Bump
# this number and release.yml together when the package list really changes.
$script:ExpectedPackableCount = 11

# `dotnet msbuild -getProperty` costs ~0.6 s per project and Invoke-Build and
# Get-Surface both scan, so what each file evaluated to is kept for the run.
$script:EvaluatedProjectProperties = @{}

function Get-EvaluatedProjectProperties([string]$projectPath) {
    if ($script:EvaluatedProjectProperties.ContainsKey($projectPath)) {
        return $script:EvaluatedProjectProperties[$projectPath]
    }
    # Ask MSBuild instead of reading the csproj's raw XML: a raw read sees neither
    # an <Import> (shared/Gen/PicoSerDe.Gen.props carries IsPackable and
    # TargetFramework for the five .Gen projects) nor Directory.Build.props nor the
    # SDK defaults (AssemblyName = project file name). These are the values
    # `dotnet pack` itself would see, which is the whole point.
    $propertyNames = "IsPackable,PackageId,AssemblyName,TargetFramework,TargetFrameworks"
    $result = Invoke-Native "dotnet" @("msbuild", $projectPath, "-getProperty:$propertyNames", "-nologo")
    $text = "$($result.Output -join [Environment]::NewLine)".Trim()
    if ($result.ExitCode -ne 0) {
        Fail "msbuild evaluation failed for $projectPath (exit $($result.ExitCode)): $text"
    }
    # The JSON object is the outermost {...}; anything around it is a warning.
    $open = $text.IndexOf("{")
    $close = $text.LastIndexOf("}")
    if ($open -lt 0 -or $close -le $open) { Fail "unexpected msbuild -getProperty output for $projectPath`: $text" }
    $properties = ($text.Substring($open, $close - $open + 1) | ConvertFrom-Json).Properties
    $script:EvaluatedProjectProperties[$projectPath] = $properties
    return $properties
}

function Get-PackableProjects([string]$root, [switch]$NoCountGuard) {
    $srcRoot = Join-Path $root "src"
    if (-not (Test-Path $srcRoot)) { $srcRoot = $root }
    # The scan never descends into a directory that holds *copies* of the
    # repository or its build output: leftover worktrees under .worktrees/,
    # staged packages under artifacts/, process state under .superpowers/.
    # Every copy carries the same PackageId, so a copy looks like a duplicate
    # project and aborts the whole release. Dot-prefixed directories (.git,
    # .idea, ...) are skipped for the same reason.
    # The match is made against the path RELATIVE to the scan root so that a
    # repository which itself lives under a dot-directory - this script
    # running from inside .worktrees/<lane> - still scans its own sources.
    $excludedDirectoryNames = @(".worktrees", ".superpowers", "artifacts")
    $projects = @()
    foreach ($file in Get-ChildItem -Path $srcRoot -Recurse -Filter *.csproj -File) {
        $relativeDirectory = [System.IO.Path]::GetDirectoryName(
            $file.FullName.Substring($srcRoot.Length).TrimStart([char[]]@('\', '/')))
        $excluded = $false
        foreach ($segment in @($relativeDirectory -split "[\\/]" | Where-Object { $_ })) {
            if ($segment.StartsWith(".") -or $excludedDirectoryNames -contains $segment.ToLowerInvariant()) {
                $excluded = $true
                break
            }
        }
        if ($excluded) { continue }
        if ($file.FullName -match "[\\/](obj|bin)[\\/]") { continue }
        $evaluated = Get-EvaluatedProjectProperties $file.FullName
        if ("$($evaluated.IsPackable)".Trim().ToLowerInvariant() -ne "true") { continue }
        $packageId = "$($evaluated.PackageId)".Trim()
        $assemblyName = "$($evaluated.AssemblyName)".Trim()
        if (-not $packageId) { $packageId = $assemblyName }
        if (-not $packageId) { $packageId = [System.IO.Path]::GetFileNameWithoutExtension($file.Name) }
        if (-not $assemblyName) { $assemblyName = $packageId }
        $targetFrameworks = "$($evaluated.TargetFramework)".Trim()
        if (-not $targetFrameworks) { $targetFrameworks = "$($evaluated.TargetFrameworks)".Trim() }
        if (-not $targetFrameworks) { $targetFrameworks = "net10.0" }
        $projects += [pscustomobject]@{
            Path            = $file.FullName
            Directory       = $file.DirectoryName
            PackageId       = $packageId
            AssemblyName    = $assemblyName
            TargetFramework = ($targetFrameworks -split ";")[0]
        }
    }
    # Always report what the scan picked up: a jump in the count (11 -> 47)
    # means stray copies of the repository crept back into the tree.
    $packageIds = ($projects.PackageId | Sort-Object) -join ', '
    Write-Host "   packable projects ($($projects.Count)) under $srcRoot`: $packageIds" -ForegroundColor DarkGray
    $duplicateIds = $projects | Group-Object -Property PackageId | Where-Object { $_.Count -gt 1 }
    if ($duplicateIds) {
        Fail "duplicate PackageId across packable projects: $(($duplicateIds | ForEach-Object { $_.Name }) -join ', ')"
    }
    if (-not $NoCountGuard -and $projects.Count -ne $script:ExpectedPackableCount) {
        Fail @"
the scan found $($projects.Count) packable project(s) under $srcRoot but the release is defined over $($script:ExpectedPackableCount).
        This is the shape of the v2026.12.0 regression: a property (IsPackable, PackageId,
        TargetFramework) moved into an imported props file or a Directory.Build.props and the
        scan stopped seeing the project - and a scan that silently finds fewer packages
        deletes their api/ baselines on the next release. Check the <Import> elements of
        every csproj and shared/Gen/PicoSerDe.Gen.props before releasing.
        If the package list really changed, update `$script:ExpectedPackableCount in
        scripts/release.ps1 and the pack list in .github/workflows/release.yml together.
"@
    }
    return , $projects
}

function Invoke-Build([string]$root, [switch]$NoCountGuard) {
    Write-Step "Building packable projects (Release)"
    $projects = Get-PackableProjects $root -NoCountGuard:$NoCountGuard
    if ($projects.Count -eq 0) { Fail "no packable projects found under $root" }
    foreach ($project in $projects) {
        $result = Invoke-Native "dotnet" @("build", $project.Path, "-c", "Release", "--nologo")
        if ($result.ExitCode -ne 0) {
            Write-Host ($result.Output -join [Environment]::NewLine)
            throw "build failed: $($project.Path)"
        }
        Write-Ok "built $($project.PackageId)"
    }
}

function Get-Surface([string]$root, [string]$outputDirectory, [switch]$NoCountGuard) {
    $surfaces = @{}
    $projects = Get-PackableProjects $root -NoCountGuard:$NoCountGuard
    foreach ($project in $projects) {
        $assembly = Get-ChildItem -Path (Join-Path $project.Directory "bin/Release") -Recurse -Filter "$($project.AssemblyName).dll" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch "[\\/](ref|obj)[\\/]" } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $assembly) {
            Fail "built assembly not found for $($project.PackageId) (bin/Release/**/$($project.AssemblyName).dll)"
        }
        $surfaceFile = Join-Path $outputDirectory "$($project.PackageId).public.txt"
        $null = Invoke-Tool @("dump", $assembly.FullName, "--out", $surfaceFile)
        if ($script:ToolExit -ne 0) { Fail "api dump failed for $($project.PackageId)" }
        $surfaces[$project.PackageId] = $surfaceFile
    }
    return $surfaces
}

function Get-LastTag {
    $result = Invoke-GitRaw @("tag", "--list", "v*", "--sort=-v:refname")
    if ($result.ExitCode -ne 0 -or -not $result.Output) { return "" }
    return @($result.Output)[0].Trim()
}

# -- locate and build the api-surface tool ------------------------------------

$toolDirectory = Join-Path $PSScriptRoot "api-surface"
if (-not (Test-Path $toolDirectory)) { $toolDirectory = Join-Path $RepoRoot "scripts/api-surface" }
if (-not (Test-Path (Join-Path $toolDirectory "ApiSurface.csproj"))) {
    Fail "api-surface tool not found (looked in '$PSScriptRoot/api-surface' and '$RepoRoot/scripts/api-surface')"
}
$script:ToolDll = Join-Path $toolDirectory "bin/Release/net10.0/picohex-api-surface.dll"
$script:ToolExit = 0

Write-Step "Building api-surface tool"
$toolBuild = Invoke-Native "dotnet" @("build", (Join-Path $toolDirectory "ApiSurface.csproj"), "-c", "Release", "-v", "quiet", "--nologo")
if ($toolBuild.ExitCode -ne 0) {
    Write-Host ($toolBuild.Output -join [Environment]::NewLine)
    Fail "failed to build the api-surface tool"
}

if ([System.IO.Path]::IsPathRooted($BaselineDir)) { $baselineRoot = $BaselineDir }
else { $baselineRoot = Join-Path $RepoRoot $BaselineDir }

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("picohex-release-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$null = New-Item -ItemType Directory -Path $workRoot -Force

try {
    # -- bootstrap mode -------------------------------------------------------
    if ($Bootstrap) {
        if (-not $FromTag -and -not $AssumeCurrent) { Fail "bootstrap needs -FromTag <tag> (preferred) or -AssumeCurrent" }
        $null = New-Item -ItemType Directory -Path $baselineRoot -Force

        if ($FromTag) {
            Write-Step "Bootstrapping baselines from tag $FromTag (temporary worktree)"
            $worktree = Join-Path $workRoot "tag"
            $null = Invoke-Git @("worktree", "add", "--detach", $worktree, $FromTag)
            try {
                # A historical revision: its package count is whatever it was then.
                Invoke-Build $worktree -NoCountGuard
                $surfaces = Get-Surface $worktree $baselineRoot -NoCountGuard
            }
            finally {
                $null = Invoke-GitRaw @("worktree", "remove", "--force", $worktree)
            }
        }
        else {
            Write-Note "-AssumeCurrent: the API delta since the last release cannot be recovered"
            Invoke-Build $RepoRoot
            $surfaces = Get-Surface $RepoRoot $baselineRoot
        }

        foreach ($packageId in ($surfaces.Keys | Sort-Object)) {
            $count = (Get-Content -LiteralPath $surfaces[$packageId] | Measure-Object -Line).Lines
            Write-Ok "$packageId`: $count API lines -> $BaselineDir/$packageId.public.txt"
        }
        Write-Host ""
        Write-Ok "Baselines written. Commit them, then release without -Bootstrap."
        exit 0
    }

    # -- release mode ---------------------------------------------------------
    if (-not (Test-Path $baselineRoot)) {
        Fail "no baselines in $BaselineDir/. Run: ./scripts/release.ps1 -Bootstrap -FromTag <last-tag>"
    }

    $dirtyResult = Invoke-GitRaw @("status", "--porcelain")
    if ($dirtyResult.Output -and -not $DryRun) { Fail "working tree is not clean; commit or stash before releasing" }

    Invoke-Build $RepoRoot

    Write-Step "Comparing the public API against the $BaselineDir baselines"
    $currentSurfaces = Get-Surface $RepoRoot $workRoot
    $changed = $false
    foreach ($packageId in ($currentSurfaces.Keys | Sort-Object)) {
        $baseline = Join-Path $baselineRoot "$packageId.public.txt"
        if (-not (Test-Path $baseline)) {
            Write-Note "$packageId`: no baseline (new package) -> API changed"
            $changed = $true
            continue
        }
        $diff = Invoke-Tool @("diff", $baseline, $currentSurfaces[$packageId])
        $summary = @($diff | Where-Object { $_ -match "^added=\d+ removed=\d+$" })[-1]
        if (-not ($summary -match "added=(\d+) removed=(\d+)")) { Fail "unexpected diff output for $packageId" }
        $added = [int]$Matches[1]
        $removed = [int]$Matches[2]
        if ($added -gt 0 -or $removed -gt 0) {
            $changed = $true
            Write-Note "$packageId`: API changed (+$added / -$removed)"
            $diff | Where-Object { $_ -match "^[+-] " } | ForEach-Object { Write-Host "     $_" -ForegroundColor DarkGray }
        }
        else {
            Write-Ok "$packageId`: API unchanged"
        }
    }

    $lastTag = Get-LastTag
    $apiChanged = "false"
    if ($changed) { $apiChanged = "true" }
    $versionArgs = @("next-version", "--api-changed", $apiChanged, "--year", (Get-Date).Year)
    if ($lastTag) { $versionArgs += @("--last", $lastTag) }
    if ($ResetOnYearChange) { $versionArgs += "--reset-on-year-change" }
    $nextVersion = @(Invoke-Tool $versionArgs)[-1].Trim()
    if ($script:ToolExit -ne 0) { Fail "next-version failed" }

    Write-Host ""
    Write-Step "Decision"
    if ($lastTag) { Write-Host "   last release : $lastTag" } else { Write-Host "   last release : (none)" }
    Write-Host "   API changed  : $apiChanged"
    Write-Host "   next version : v$nextVersion" -ForegroundColor Green

    if ($DryRun) {
        Write-Host ""
        Write-Ok "Dry run - nothing written, no tag created."
        exit 0
    }

    Write-Step "Refreshing baselines and tagging"
    foreach ($packageId in $currentSurfaces.Keys) {
        Copy-Item -LiteralPath $currentSurfaces[$packageId] -Destination (Join-Path $baselineRoot "$packageId.public.txt") -Force
    }
    foreach ($file in Get-ChildItem -Path $baselineRoot -Filter *.public.txt -File) {
        # BaseName strips only the last extension (PicoTui.public), so strip the
        # full .public.txt suffix explicitly.
        $baselinePackage = $file.Name.Substring(0, $file.Name.Length - ".public.txt".Length)
        if (-not $currentSurfaces.ContainsKey($baselinePackage)) {
            Write-Note "removing stale baseline for dropped package $baselinePackage"
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }

    $null = Invoke-Git @("add", "--", $BaselineDir)
    # An unchanged API (y bump) refreshes the baselines to identical content:
    # only commit when something is actually staged, otherwise the no-op commit
    # fails and the tag is never created.
    $staged = Invoke-GitRaw @("diff", "--cached", "--quiet")
    if ($staged.ExitCode -ne 0) {
        $null = Invoke-Git @("commit", "-m", "chore(release): v$nextVersion")
        Write-Ok "committed baseline refresh"
    }
    else {
        Write-Note "baselines unchanged - tagging the current commit"
    }

    $null = Invoke-Git @("tag", "-a", "v$nextVersion", "-m", "v$nextVersion")
    Write-Ok "created tag v$nextVersion"

    if ($Push) {
        $branchResult = Invoke-GitRaw @("rev-parse", "--abbrev-ref", "HEAD")
        $branch = @($branchResult.Output)[0].Trim()
        $null = Invoke-Git @("push", "origin", $branch)
        $null = Invoke-Git @("push", "origin", "v$nextVersion")
        Write-Ok "pushed $branch and v$nextVersion - the release workflow will publish"
    }
    else {
        Write-Host ""
        Write-Host "   Next: git push origin <branch> && git push origin v$nextVersion"
        Write-Host "   The release workflow publishes every package at v$nextVersion."
    }
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
