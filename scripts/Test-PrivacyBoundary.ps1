[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$productionRoot = Join-Path $repository 'apps/windows/src'
$updaterRoot = Join-Path $productionRoot 'KeyClick.Updater'
$failures = [System.Collections.Generic.List[string]]::new()

$networkPattern = '\b(HttpClient|HttpRequestMessage|HttpWebRequest|WebRequest|WebClient|WebSocket|ClientWebSocket|TcpClient|UdpClient|System\.Net\.(Sockets|NetworkInformation)|\bSocket\b|\bDns\.)\b'
$productionFiles = Get-ChildItem -LiteralPath $productionRoot -Recurse -Filter '*.cs' |
  Where-Object { $_.FullName -notlike "$updaterRoot*" -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $productionFiles) {
  $matches = Select-String -LiteralPath $file.FullName -Pattern $networkPattern
  foreach ($match in $matches) {
    $failures.Add("Network API outside manual updater: $($file.FullName):$($match.LineNumber)")
  }
}

$updaterFiles = Get-ChildItem -LiteralPath $updaterRoot -Recurse -Filter '*.cs' |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $updaterFiles) {
  $content = Get-Content -LiteralPath $file.FullName -Raw
  if ($content -match '\b(KeyClick\.Core|InputAction|Statistics|Wellness|TypingChallenge|ChallengeResult|ChallengePrompt|ProfileManifest)\b') {
    $failures.Add("Updater references input/statistics/profile types: $($file.FullName)")
  }
  if ($content -match 'public\s+[^\r\n]+\(([^)]*(HttpContent|Stream|byte\[\]|string\s+(body|payload))[^)]*)\)') {
    $failures.Add("Updater exposes a payload/body API: $($file.FullName)")
  }
  if ($content -match '\b(PostAsync|PutAsync|PatchAsync)\b') {
    $failures.Add("Updater contains a non-GET network operation: $($file.FullName)")
  }
}

$statisticsService = Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/StatisticsService.cs'
if (Test-Path -LiteralPath $statisticsService) {
  $content = Get-Content -LiteralPath $statisticsService -Raw
  if ($content -match '(?s)(?<csv>public async Task ExportCsvAsync.*?)(?=\r?\n  public )' -and $Matches['csv'] -match 'ApplicationStatistics|application(_id|_name)|DisplayName') {
    $failures.Add("Per-application statistics entered the CSV export surface: $statisticsService")
  }
}

$challengeService = Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/TypingChallengeService.cs'
if (Test-Path -LiteralPath $challengeService) {
  $content = Get-Content -LiteralPath $challengeService -Raw
  if ($content -match '(?s)(?<csv>ExportCsvAsync.*?)(?=\r?\n  private )' -and $Matches['csv'] -match 'PromptTitle|\.Text\b|Response') {
    $failures.Add("Typing response or prompt content entered the challenge CSV surface: $challengeService")
  }
}

$coreModels = Join-Path $productionRoot 'KeyClick.Core/Models.cs'
if (Test-Path -LiteralPath $coreModels) {
  $content = Get-Content -LiteralPath $coreModels -Raw
  if ($content -match '(?s)(?<bundle>public sealed record StatisticsTransferBundle\(.*?\);)' -and $Matches['bundle'] -match 'Application') {
    $failures.Add("Per-application statistics entered the profile transfer contract: $coreModels")
  }
  if ($content -match '(?s)(?<result>public sealed record TypingChallengeResult\(.*?\);)' -and $Matches['result'] -match 'Response(Text|Content)|Typed(Text|Content)|Ordered') {
    $failures.Add("Typing response content entered the persisted challenge result contract: $coreModels")
  }
}

$storeFile = Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/SqliteAppStore.cs'
if (Test-Path -LiteralPath $storeFile) {
  $content = Get-Content -LiteralPath $storeFile -Raw
  if ($content -match '(?s)ExportStatisticsAsync.*?statistics_application_hourly.*?ImportStatisticsAsync') {
    $failures.Add("Per-application statistics entered the profile transfer surface: $storeFile")
  }
  if ($content -match 'typing_challenge_results[^;]*(response_text|typed_text|typed_content|key_history)') {
    $failures.Add("Typing response content entered the persisted challenge result schema: $storeFile")
  }
}

$profileService = Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/ProfileTransferService.cs'
if (Test-Path -LiteralPath $profileService) {
  $content = Get-Content -LiteralPath $profileService -Raw
  if ($content -match '\bApplicationStatistics\w*\b|statistics_application_hourly') {
    $failures.Add("Per-application statistics entered profile transfer code: $profileService")
  }
}

$startupFiles = @(
  (Join-Path $productionRoot 'KeyClick.App/App.xaml.cs'),
  (Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/StatisticsService.cs'),
  (Join-Path $productionRoot 'KeyClick.Infrastructure.Windows/WellnessService.cs')
)
foreach ($file in $startupFiles) {
  if (Test-Path -LiteralPath $file) {
    $content = Get-Content -LiteralPath $file -Raw
    if ($content -match '\b(CheckAsync|CheckForUpdateAsync)\s*\(') {
      $failures.Add("Startup or background service invokes an update check: $file")
    }
  }
}

$telemetryPattern = '\b(ApplicationInsights|TelemetryClient|SentrySdk|OpenTelemetry|Crashlytics|Mixpanel|Amplitude)\b'
foreach ($file in $productionFiles) {
  if (Select-String -LiteralPath $file.FullName -Pattern $telemetryPattern -Quiet) {
    $failures.Add("Production telemetry dependency found: $($file.FullName)")
  }
}

$projectFiles = Get-ChildItem -LiteralPath $productionRoot -Recurse -Include '*.csproj','packages.lock.json' |
  Where-Object { -not $_.PSIsContainer -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $projectFiles) {
  if (Select-String -LiteralPath $file.FullName -Pattern $telemetryPattern -Quiet) {
    $failures.Add("Production telemetry dependency found: $($file.FullName)")
  }
}

$requiredDocumentation = @{
  'README.md' = @('never stores typed characters', 'challenge responses are never stored', 'never transmitted', 'per-application details are excluded', 'manual update')
  'PRIVACY.md' = @('never stores typed characters', 'challenge responses are never stored', 'never transmitted', 'per-application details are also excluded', 'manual update')
  'SECURITY.md' = @('Privacy Boundary')
  'CONTRIBUTING.md' = @('Privacy Boundary')
}
foreach ($relative in $requiredDocumentation.Keys) {
  $path = Join-Path $repository $relative
  if (-not (Test-Path -LiteralPath $path)) {
    $failures.Add("Required privacy documentation is missing: $relative")
    continue
  }
  $content = Get-Content -LiteralPath $path -Raw
  foreach ($phrase in $requiredDocumentation[$relative]) {
    if ($content.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
      $failures.Add("Privacy invariant '$phrase' is missing from $relative")
    }
  }
}

if ($failures.Count -gt 0) {
  $failures | ForEach-Object { Write-Error $_ }
  exit 1
}

Write-Host 'Privacy Boundary passed: networking is isolated; local/per-application statistics and typing challenges cannot enter updater or network APIs.'
