param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-beta.1',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$RelayUrl = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PSBoundParameters.ContainsKey('RelayUrl')) {
    $defaultsPath = Join-Path $projectRoot 'src/speciRPG.App/bridgen.defaults.json'
    $RelayUrl = [string](Get-Content -LiteralPath $defaultsPath -Raw | ConvertFrom-Json).relayUrl
}
if ($RelayUrl) {
    $relayUri = [Uri]$RelayUrl
    if (-not $relayUri.IsAbsoluteUri -or $relayUri.Scheme -ne 'https' -or $relayUri.UserInfo -or $relayUri.Query -or $relayUri.Fragment) {
        throw 'RelayUrl must be a public HTTPS base URL without credentials, query or fragment.'
    }
}

$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot ('publish-' + [Guid]::NewGuid().ToString('N'))
$projectPath = Join-Path $projectRoot 'src/speciRPG.App/speciRPG.App.csproj'
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
& dotnet publish $projectPath -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Only the public relay URL belongs in the distributable. Never copy connection.json,
# invitations, .env files or the developer's environment variables into it.
@{ relayUrl = $RelayUrl.TrimEnd('/') } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $publishDirectory 'bridgen.defaults.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/user-guide.md') -Destination (Join-Path $publishDirectory 'README.md')
$archivePath = Join-Path $artifactRoot "BridgEN-$Version-$Runtime.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath ($archivePath + '.sha256') -Encoding ascii
Write-Output "Release ZIP: $archivePath"
Write-Output "Checksum: $archivePath.sha256"
