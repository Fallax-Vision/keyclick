[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)]
  [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
  [string] $Repository,

  [Parameter(Mandatory)]
  [ValidatePattern('^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
  [string] $CurrentTag,

  [ValidateRange(1, 3)]
  [int] $KeepReleaseVersions = 3,

  [switch] $Apply
)

$ErrorActionPreference = 'Stop'
$assetPattern = '^(?:KeyClick-(?:Setup|Portable)-Windows-(?:x64|arm64)-[0-9]+\.[0-9]+\.[0-9]+(?:\.sbom\.cdx\.json|\.exe)|checksums-[0-9]+\.[0-9]+\.[0-9]+\.txt)$'
$releases = [Collections.Generic.List[object]]::new()

for ($page = 1; ; $page++) {
  $batch = @(gh api "repos/$Repository/releases?per_page=100&page=$page" | ConvertFrom-Json)
  if ($LASTEXITCODE -ne 0) { throw 'Could not read GitHub releases.' }
  foreach ($release in $batch) { $releases.Add($release) }
  if ($batch.Count -lt 100) { break }
}

$versioned = $releases |
  Where-Object { $_.tag_name -match '^v(?<version>[0-9]+\.[0-9]+\.[0-9]+)$' } |
  ForEach-Object { [pscustomobject]@{ Release = $_; Version = [version]$Matches['version'] } } |
  Sort-Object Version -Descending
$current = $versioned | Where-Object { $_.Release.tag_name -eq $CurrentTag } | Select-Object -First 1
if ($null -eq $current) { throw "The current release $CurrentTag does not exist." }

$retained = @($current)
$retained += @($versioned | Where-Object { $_.Release.tag_name -ne $CurrentTag } | Select-Object -First ($KeepReleaseVersions - 1))
foreach ($item in $retained) {
  $names = @($item.Release.assets.name)
  foreach ($required in @(
      "KeyClick-Setup-Windows-x64-$($item.Version).exe",
      "KeyClick-Setup-Windows-arm64-$($item.Version).exe",
      "KeyClick-Portable-Windows-x64-$($item.Version).exe",
      "KeyClick-Portable-Windows-arm64-$($item.Version).exe",
      "checksums-$($item.Version).txt")) {
    if ($required -notin $names) { throw "Retained release $($item.Release.tag_name) is missing $required; refusing to prune rollback assets." }
  }
}

$retainedTags = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $retained) { [void]$retainedTags.Add([string]$item.Release.tag_name) }
$deleted = 0
foreach ($item in $versioned | Where-Object { -not $retainedTags.Contains([string]$_.Release.tag_name) }) {
  foreach ($asset in @($item.Release.assets) | Where-Object { $_.name -match $assetPattern }) {
    $target = "$($item.Release.tag_name)/$($asset.name)"
    if (-not $Apply) {
      Write-Host "Would remove $target"
      continue
    }
    if ($PSCmdlet.ShouldProcess($target, 'Remove old KeyClick GitHub release asset')) {
      gh api --method DELETE "repos/$Repository/releases/assets/$($asset.id)"
      if ($LASTEXITCODE -ne 0) { throw "Could not remove $target." }
      $deleted++
    }
  }
}

if ($Apply) { Write-Host "Removed $deleted old KeyClick release asset(s)." }
else { Write-Host "Dry run complete. Retaining: $($retainedTags -join ', ')" }
