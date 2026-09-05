[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$failures = [Collections.Generic.List[string]]::new()

function Add-Failure([string] $Message) {
  $failures.Add($Message)
}

if (-not (Test-Path -LiteralPath (Join-Path $repository 'AGENTS.md'))) {
  Add-Failure 'AGENTS.md must be tracked so clean and production checkouts receive project policy.'
} else {
  git -C $repository check-ignore --quiet AGENTS.md
  if ($LASTEXITCODE -eq 0) { Add-Failure 'AGENTS.md must not be ignored; clean and production checkouts need project policy.' }
}

$workflowRoot = Join-Path $repository '.github/workflows'
foreach ($workflow in Get-ChildItem -LiteralPath $workflowRoot -File -Include '*.yml', '*.yaml') {
  $lines = Get-Content -LiteralPath $workflow.FullName
  for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match 'uses:\s+[^\s@]+@(?<reference>[^\s#]+)' -and $Matches['reference'] -notmatch '^[0-9a-f]{40}$') {
      Add-Failure "Workflow action is not pinned by full SHA: $($workflow.Name):$($index + 1)"
    }
    if ($lines[$index] -match 'uses:\s+actions/upload-artifact@') {
      $blockEnd = [Math]::Min($index + 14, $lines.Count - 1)
      if (-not ($lines[$index..$blockEnd] -match '^\s+retention-days:\s+(7|14)\s*$')) {
        Add-Failure "Uploaded workflow artifacts need a 7- or 14-day retention: $($workflow.Name):$($index + 1)"
      }
    }
  }
}

$productionRoot = Join-Path $repository 'apps/windows/src'
$xamlFiles = Get-ChildItem -LiteralPath $productionRoot -Recurse -File -Include '*.xaml'
foreach ($file in $xamlFiles) {
  $content = Get-Content -LiteralPath $file.FullName -Raw
  if ($content -match '<DropShadowEffect|HasDropShadow="True"') {
    Add-Failure "Drop shadows are prohibited: $($file.FullName)"
  }
  if ($content -match 'TranslateTransform|translate[XY]') {
    Add-Failure "Translate-on-hover motion is prohibited: $($file.FullName)"
  }
}

$retentionSource = Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/StorageRetentionService.cs'
if (-not (Test-Path -LiteralPath $retentionSource)) {
  Add-Failure 'Runtime storage retention service is missing.'
} else {
  $content = Get-Content -LiteralPath $retentionSource -Raw
  foreach ($required in @('MaximumLogFiles = 7', 'MaximumLogAge = 14', 'MaximumLogFileBytes = 5L * 1024 * 1024',
      'MaximumTotalLogBytes = 25L * 1024 * 1024', 'MaximumGeneralBackups = 3', 'MaximumPendingUpdates = 1')) {
    if ($content -notmatch [regex]::Escape($required)) { Add-Failure "Runtime retention invariant is missing: $required" }
  }
}

$packaging = Get-Content -LiteralPath (Join-Path $repository 'scripts/Build-Portable.ps1') -Raw
if ($packaging -notmatch '\$retainedVersions\s*=\s*@\(\$Version\)' -or $packaging -notmatch '\$retainedVersions\s*\+=\s*\$previousVersion') {
  Add-Failure 'Local release artifacts must retain only the current and immediately preceding version sets.'
}

$releaseWorkflow = Get-Content -LiteralPath (Join-Path $workflowRoot 'release.yml') -Raw
if ($releaseWorkflow -notmatch 'Prune-GitHubReleaseAssets\.ps1.+-KeepReleaseVersions 3.+-Apply') {
  Add-Failure 'GitHub releases must prune KeyClick binary assets beyond the current and two prior version sets.'
}

$gitIgnore = Get-Content -LiteralPath (Join-Path $repository '.gitignore') -Raw
foreach ($required in @('**/bin/', '**/obj/', 'artifacts/', 'TestResults/', '/tmp/')) {
  if ($gitIgnore -notmatch [regex]::Escape($required)) { Add-Failure "Generated output ignore is missing: $required" }
}

if ($failures.Count -gt 0) {
  $failures | ForEach-Object { Write-Host "POLICY ERROR: $_" -ForegroundColor Red }
  throw "Repository policy failed with $($failures.Count) error(s)."
}

Write-Host 'Repository policy passed: durable guidance, immutable CI actions, bounded artifacts, UI constraints, and runtime retention are enforced.'
exit 0
