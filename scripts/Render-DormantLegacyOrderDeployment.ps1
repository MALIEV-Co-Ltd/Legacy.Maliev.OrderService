[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$ApprovedImageRepository,
    [Parameter(Mandatory)][string]$ExistingServiceAccount,
    [Parameter(Mandatory)][string]$ExistingRuntimeSecret
)
$ErrorActionPreference = 'Stop'
# Offline stdout only: no Kubernetes/cloud/build/write adapter or discovery of identities.
$imagePattern = '\A' + [regex]::Escape($ApprovedImageRepository) + '@sha256:[a-f0-9]{64}\z'
$namePattern = '\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z'
if ($ApprovedImageRepository -cnotmatch '\A[a-z0-9-]+-docker\.pkg\.dev/maliev-website/[a-z0-9._-]+/legacy-maliev-order-service\z' -or
    $Image -cnotmatch $imagePattern -or $Image.EndsWith(('0' * 64), [StringComparison]::Ordinal) -or
    $ExistingServiceAccount -cnotmatch $namePattern -or $ExistingRuntimeSecret -cnotmatch $namePattern) {
    throw 'An explicit immutable owner image and existing identity/secret references are required.'
}
$path = Join-Path $PSScriptRoot '../deploy/disabled/order-deployment.template.yaml'
$bytes = [IO.File]::ReadAllBytes($path)
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
if ($digest -cne '4b2e210039c6b4e961d291d679a3ac64f781012051c53449d9b034e0cc2b717d') { throw 'The reviewed dormant template changed.' }
$template = [Text.Encoding]::UTF8.GetString($bytes)
$slots = @{
    '__REQUIRED_IMMUTABLE_IMAGE__' = $Image
    '__REQUIRED_EXISTING_SERVICE_ACCOUNT__' = $ExistingServiceAccount
    '__REQUIRED_EXISTING_RUNTIME_SECRET__' = $ExistingRuntimeSecret
}
foreach ($slot in $slots.Keys) {
    if ([regex]::Matches($template, [regex]::Escape($slot)).Count -ne 1) { throw 'Invalid dormant template slot.' }
    $template = $template.Replace($slot, $slots[$slot])
}
[Console]::Out.Write($template)
