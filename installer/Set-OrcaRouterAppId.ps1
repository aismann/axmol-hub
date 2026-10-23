param([Parameter(Mandatory)][string]$PublishDir)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:ORCAROUTER_APP_ID)) {
    throw 'ORCAROUTER_APP_ID must be set to package Axmol Hub.'
}

# Only the published copy is modified; the tracked manifest never receives the ID.
$taskPath = Join-Path $PublishDir 'manifests/ai-providers.json'
$taskManifest = Get-Content -Raw -LiteralPath $taskPath | ConvertFrom-Json -AsHashtable
$taskProviders = @($taskManifest['providers'] | Where-Object { $_['id'] -ceq 'orcarouter' })
if ($taskProviders.Count -ne 1 -or $null -eq $taskProviders[0]['oauth']) {
    throw 'The published manifest must contain exactly one OrcaRouter provider with OAuth settings.'
}

$taskProviders[0]['oauth']['appId'] = $env:ORCAROUTER_APP_ID
$taskJson = $taskManifest | ConvertTo-Json -Depth 100
[IO.File]::WriteAllText($taskPath, ($taskJson.Replace("`r`n", "`n").TrimEnd("`n") + "`n"), [Text.UTF8Encoding]::new($false))
