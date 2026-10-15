<#
.SYNOPSIS
    One-time setup of the public GitHub repository: settings, labels and starter issues.

.DESCRIPTION
    Run once, after the repository exists and `gh` is signed in as an account that can administer it.
    Everything here is idempotent except the starter issues, which are skipped when an issue with the
    same title already exists. Nothing is published beyond the repository's own settings.

.PARAMETER Repository
    owner/name. Defaults to KakosoAi/gitxen.
#>
[CmdletBinding()]
param(
    [string] $Repository = 'KakosoAi/gitxen'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function Invoke-Gh
{
    $output = & gh @args
    if ($LASTEXITCODE -ne 0)
    {
        throw "gh $($args -join ' ') failed."
    }
    return $output
}

Write-Host "Configuring $Repository" -ForegroundColor Cyan

# --- Repository settings ------------------------------------------------------------------------
Invoke-Gh repo edit $Repository `
    --description 'A fast, honest git client for Windows, built with WinUI 3. GPL-3.0-or-later.' `
    --homepage "https://github.com/$Repository" `
    --enable-issues --enable-discussions --enable-wiki=false `
    --delete-branch-on-merge --enable-squash-merge --enable-merge-commit=false --enable-rebase-merge `
    --add-topic git --add-topic git-client --add-topic git-gui --add-topic windows --add-topic winui `
    --add-topic winui3 --add-topic csharp --add-topic dotnet --add-topic version-control | Out-Null

# Private vulnerability reporting, so SECURITY.md's link works.
Invoke-Gh api -X PUT "repos/$Repository/private-vulnerability-reporting" | Out-Null

# --- Labels ---------------------------------------------------------------------------------------
$labels = Get-Content (Join-Path $root '.github\labels.yml') | Where-Object { $_ -match '^\s*-\s*\{' }

foreach ($line in $labels)
{
    if ($line -notmatch 'name:\s*(?<name>[^,]+),\s*color:\s*(?<color>[0-9a-fA-F]{6}),\s*description:\s*(?<desc>.+?)\s*\}\s*$')
    {
        continue
    }

    Invoke-Gh label create $Matches.name --repo $Repository --color $Matches.color `
        --description $Matches.desc --force | Out-Null
}

# --- Branch protection on main --------------------------------------------------------------------
$protection = @{
    required_status_checks        = @{ strict = $true; contexts = @('Build') }
    enforce_admins                = $false
    required_pull_request_reviews = @{ required_approving_review_count = 1; dismiss_stale_reviews = $true }
    restrictions                  = $null
    required_linear_history       = $true
    allow_force_pushes            = $false
    allow_deletions               = $false
} | ConvertTo-Json -Depth 5

$protection | & gh api -X PUT "repos/$Repository/branches/main/protection" --input - | Out-Null

# --- Starter issues -------------------------------------------------------------------------------
$issues = @(
    @{ t = 'Add screenshots and a short demo GIF to the README'; l = 'documentation,good first issue'; b = 'The README has a placeholder for a screenshot. Add `docs/images/` assets (light and dark) and reference them.' }
    @{ t = 'Test: cover the GitHub parsers (GitHubParsers)'; l = 'tests,good first issue'; b = 'Add NUnit tests for the parsers in `Gitxen.GitHub`, using captured `gh` JSON as input. See `src/app/Gitxen.Tests` for the style.' }
    @{ t = 'Test: TerminalOutputBuffer'; l = 'tests,good first issue'; b = 'Pure string-in/string-out; link the file into `Gitxen.Tests` as `HunkSplitter.cs` is.' }
    @{ t = 'Test: GitDirectory'; l = 'tests,good first issue'; b = 'Cover `.git` directory and worktree resolution in `Services/GitDirectory.cs`.' }
    @{ t = 'Accessibility pass: keyboard navigation and screen reader names'; l = 'ui,help wanted'; b = 'Walk every page with the keyboard and Narrator; add `AutomationProperties.Name` where missing.' }
    @{ t = 'Publish to winget'; l = 'packaging,help wanted'; b = 'Manifests exist under `setup/gitxen/packaging/winget`. Submit once a signed release exists.' }
    @{ t = 'Publish to Scoop and Chocolatey'; l = 'packaging,help wanted'; b = 'Manifests exist under `setup/gitxen/packaging`; set the repository variables described in `setup/gitxen/README.md`.' }
    @{ t = 'Sign the MSI (SignPath)'; l = 'packaging'; b = 'Apply to SignPath for OSS signing and add the signing step to the release workflow.' }
    @{ t = 'gx: shell completions for PowerShell'; l = 'cli,enhancement,good first issue'; b = 'Add `gx completion powershell` that prints a completion script.' }
    @{ t = 'Localisation: move UI strings to resources'; l = 'enhancement,help wanted'; b = 'Strings are inline in XAML. Move them to `.resw` so the UI can be translated.' }
)

$existing = (Invoke-Gh issue list --repo $Repository --state all --limit 200 --json title | ConvertFrom-Json).title

foreach ($issue in $issues)
{
    if ($existing -contains $issue.t)
    {
        continue
    }

    Invoke-Gh issue create --repo $Repository --title $issue.t --label $issue.l --body $issue.b | Out-Null
    Write-Host "  + $($issue.t)"
}

Write-Host 'Done.' -ForegroundColor Green
Write-Host 'Still manual: social preview image (Settings > General), SignPath application, GitHub Sponsors.'
