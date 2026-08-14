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
  if ($content -match '\b(KeyClick\.Core|InputAction|Statistics|Wellness|ProfileManifest)\b') {
    $failures.Add("Updater references input/statistics/profile types: $($file.FullName)")
  }
  if ($content -match 'public\s+[^\r\n]+\(([^)]*(HttpContent|Stream|byte\[\]|string\s+(body|payload))[^)]*)\)') {
    $failures.Add("Updater exposes a payload/body API: $($file.FullName)")
  }
  if ($content -match '\b(PostAsync|PutAsync|PatchAsync)\b') {
    $failures.Add("Updater contains a non-GET network operation: $($file.FullName)")
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
  'README.md' = @('never stores typed characters', 'never transmitted', 'manual update')
  'PRIVACY.md' = @('never stores typed characters', 'never transmitted', 'manual update')
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

Write-Host 'Privacy Boundary passed: networking is isolated and local statistics cannot enter updater APIs.'
