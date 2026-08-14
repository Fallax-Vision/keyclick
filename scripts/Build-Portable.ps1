[CmdletBinding()]
param(
  [Parameter()]
  [string] $Version,

  [Parameter()]
  [ValidateSet('win-x64', 'win-arm64')]
  [string[]] $Runtime = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
  [xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
  $Version = [string] $buildProperties.Project.PropertyGroup.VersionPrefix
}
$semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
if ($Version -notmatch $semanticVersionPattern) {
  throw 'Version must be a valid SemVer 2.0.0 core or prerelease version without build metadata.'
}

function Compare-NumericIdentifier([string] $Left, [string] $Right) {
  $leftValue = $Left.TrimStart('0')
  $rightValue = $Right.TrimStart('0')
  if ($leftValue.Length -ne $rightValue.Length) { return [Math]::Sign($leftValue.Length - $rightValue.Length) }
  return [Math]::Sign([string]::CompareOrdinal($leftValue, $rightValue))
}

function Compare-SemVer([string] $Left, [string] $Right) {
  if ($Left -notmatch $semanticVersionPattern -or $Right -notmatch $semanticVersionPattern) {
    throw 'Cannot compare an invalid semantic version.'
  }
  $leftMatch = [regex]::Match($Left, $semanticVersionPattern)
  $rightMatch = [regex]::Match($Right, $semanticVersionPattern)
  foreach ($index in 1..3) {
    $comparison = Compare-NumericIdentifier $leftMatch.Groups[$index].Value $rightMatch.Groups[$index].Value
    if ($comparison -ne 0) { return $comparison }
  }
  $leftPre = $leftMatch.Groups[4].Value
  $rightPre = $rightMatch.Groups[4].Value
  if ([string]::IsNullOrEmpty($leftPre)) { return $(if ([string]::IsNullOrEmpty($rightPre)) { 0 } else { 1 }) }
  if ([string]::IsNullOrEmpty($rightPre)) { return -1 }
  $leftIdentifiers = $leftPre.Split('.')
  $rightIdentifiers = $rightPre.Split('.')
  for ($index = 0; $index -lt [Math]::Min($leftIdentifiers.Length, $rightIdentifiers.Length); $index++) {
    $leftNumeric = $leftIdentifiers[$index] -match '^[0-9]+$'
    $rightNumeric = $rightIdentifiers[$index] -match '^[0-9]+$'
    if ($leftNumeric -and $rightNumeric) {
      $comparison = Compare-NumericIdentifier $leftIdentifiers[$index] $rightIdentifiers[$index]
    } elseif ($leftNumeric -ne $rightNumeric) {
      $comparison = if ($leftNumeric) { -1 } else { 1 }
    } else {
      $comparison = [Math]::Sign([string]::CompareOrdinal($leftIdentifiers[$index], $rightIdentifiers[$index]))
    }
    if ($comparison -ne 0) { return $comparison }
  }
  return [Math]::Sign($leftIdentifiers.Length - $rightIdentifiers.Length)
}

$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$repositoryPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifacts.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The artifacts path must remain inside the repository.'
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$artifactPattern = '^KeyClick-(?:Setup|Portable)-Windows-(?:x64|arm64)-(?<version>' + $semanticVersionPattern.TrimStart('^').TrimEnd('$') + ')\.(?:exe|sbom\.cdx\.json)$'
$checksumPattern = '^checksums-(?<version>' + $semanticVersionPattern.TrimStart('^').TrimEnd('$') + ')\.txt$'
$existingVersions = Get-ChildItem -LiteralPath $artifacts -File | ForEach-Object {
  $match = [regex]::Match($_.Name, $artifactPattern)
  if ($match.Success) { $match.Groups['version'].Value }
} | Sort-Object -Unique
$newerExisting = $existingVersions | Where-Object { (Compare-SemVer $_ $Version) -gt 0 } | Select-Object -First 1
if ($newerExisting) {
  throw "Refusing to package $Version because newer artifact version $newerExisting already exists."
}

$appProject = Join-Path $repository 'apps\windows\src\KeyClick.App\KeyClick.App.csproj'
$bootstrapProject = Join-Path $repository 'apps\windows\src\KeyClick.Bootstrap\KeyClick.Bootstrap.csproj'
$checksums = [Collections.Generic.List[string]]::new()
$staging = Join-Path $artifacts ".package-$Version-$([Guid]::NewGuid().ToString('N'))"
$artifactPrefix = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $staging.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The package staging path must remain inside the artifacts directory.'
}
New-Item -ItemType Directory -Path $staging | Out-Null

try {
  foreach ($rid in $Runtime) {
    $architecture = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $work = Join-Path $staging ".work-$architecture"
    $payloadDirectory = Join-Path $work 'payload'
    $payloadZip = Join-Path $work 'payload.zip'
    $setupDirectory = Join-Path $work 'setup'
    $portableDirectory = Join-Path $work 'portable'
    New-Item -ItemType Directory -Path $payloadDirectory, $setupDirectory, $portableDirectory | Out-Null

    dotnet publish $appProject -c Release -r $rid --self-contained true -o $payloadDirectory `
      -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Application publish failed for $rid." }

    Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $payloadZip -CompressionLevel Optimal
    dotnet publish $bootstrapProject -c Release -r $rid --self-contained true -o $setupDirectory `
      -p:Version=$Version -p:PayloadPath=$payloadZip -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
      -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Setup bootstrap publish failed for $rid." }

    dotnet publish $bootstrapProject -c Release -r $rid --self-contained true -o $portableDirectory `
      -p:Version=$Version -p:PayloadPath=$payloadZip -p:PublishSingleFile=true `
      -p:DefineConstants=PORTABLE_BUILD `
      -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
      -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Portable bootstrap publish failed for $rid." }

    $setup = Join-Path $staging "KeyClick-Setup-Windows-$architecture-$Version.exe"
    $portable = Join-Path $staging "KeyClick-Portable-Windows-$architecture-$Version.exe"
    Copy-Item -LiteralPath (Join-Path $setupDirectory 'KeyClick.exe') -Destination $setup
    Copy-Item -LiteralPath (Join-Path $portableDirectory 'KeyClick.exe') -Destination $portable
    foreach ($artifact in @($setup, $portable)) {
      $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
      $checksums.Add("$hash  $([IO.Path]::GetFileName($artifact))")
    }

    Remove-Item -LiteralPath $work -Recurse -Force
  }

  [IO.File]::WriteAllLines((Join-Path $staging "checksums-$Version.txt"), $checksums, [Text.UTF8Encoding]::new($false))
  Get-ChildItem -LiteralPath $artifacts -File | Where-Object {
    $match = [regex]::Match($_.Name, $artifactPattern)
    if (-not $match.Success) { $match = [regex]::Match($_.Name, $checksumPattern) }
    $match.Success -and $match.Groups['version'].Value -eq $Version
  } | Remove-Item -Force
  Get-ChildItem -LiteralPath $staging -File | Move-Item -Destination $artifacts
} finally {
  if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}

$previousVersion = $null
$allVersions = Get-ChildItem -LiteralPath $artifacts -File | ForEach-Object {
  $match = [regex]::Match($_.Name, $artifactPattern)
  if ($match.Success) { $match.Groups['version'].Value }
} | Sort-Object -Unique
foreach ($candidate in $allVersions) {
  if ((Compare-SemVer $candidate $Version) -ge 0) { continue }
  if ($null -eq $previousVersion -or (Compare-SemVer $candidate $previousVersion) -gt 0) { $previousVersion = $candidate }
}
$retainedVersions = @($Version)
if ($previousVersion) { $retainedVersions += $previousVersion }
Get-ChildItem -LiteralPath $artifacts -File | Where-Object {
  $match = [regex]::Match($_.Name, $artifactPattern)
  if (-not $match.Success) { $match = [regex]::Match($_.Name, $checksumPattern) }
  $match.Success -and $match.Groups['version'].Value -notin $retainedVersions
} | Remove-Item -Force

Get-ChildItem -LiteralPath $artifacts | Select-Object Name, Length, LastWriteTime
