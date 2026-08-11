[CmdletBinding()]
param(
  [Parameter()]
  [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$')]
  [string] $Version = '1.0.0',

  [Parameter()]
  [ValidateSet('win-x64', 'win-arm64')]
  [string[]] $Runtime = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\portable'))
if (-not $artifacts.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The artifacts path must remain inside the repository.'
}
if (Test-Path -LiteralPath $artifacts) {
  Remove-Item -LiteralPath $artifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $artifacts | Out-Null

$appProject = Join-Path $repository 'apps\windows\src\KeyClick.App\KeyClick.App.csproj'
$bootstrapProject = Join-Path $repository 'apps\windows\src\KeyClick.Bootstrap\KeyClick.Bootstrap.csproj'
$checksums = [Collections.Generic.List[string]]::new()

foreach ($rid in $Runtime) {
  $architecture = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
  $work = Join-Path $artifacts ".work-$architecture"
  $payloadDirectory = Join-Path $work 'payload'
  $payloadZip = Join-Path $work 'payload.zip'
  $bootstrapDirectory = Join-Path $work 'bootstrap'
  New-Item -ItemType Directory -Path $payloadDirectory, $bootstrapDirectory | Out-Null

  dotnet publish $appProject -c Release -r $rid --self-contained true -o $payloadDirectory `
    -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
  if ($LASTEXITCODE -ne 0) { throw "Application publish failed for $rid." }

  Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $payloadZip -CompressionLevel Optimal
  dotnet publish $bootstrapProject -c Release -r $rid --self-contained true -o $bootstrapDirectory `
    -p:Version=$Version -p:PayloadPath=$payloadZip -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false
  if ($LASTEXITCODE -ne 0) { throw "Bootstrap publish failed for $rid." }

  $source = Join-Path $bootstrapDirectory 'KeyClick.exe'
  $destination = Join-Path $artifacts "KeyClick-Windows-$architecture.exe"
  Copy-Item -LiteralPath $source -Destination $destination
  $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
  $checksums.Add("$hash  $([IO.Path]::GetFileName($destination))")

  Remove-Item -LiteralPath $work -Recurse -Force
}

[IO.File]::WriteAllLines((Join-Path $artifacts 'checksums.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath $artifacts | Select-Object Name, Length, LastWriteTime
