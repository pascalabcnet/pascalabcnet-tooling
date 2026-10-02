[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [string]$PascalABCSourcePath = '',
    [ValidateSet('all', 'net-framework', 'net10')]
    [string]$Target = 'all',
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerSourceRoot = Join-Path $repositoryRoot 'pascalabcnet'
$canonicalScript = Join-Path $compilerSourceRoot 'scripts\build-compiler-host.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\compiler-host'
}
if ([string]::IsNullOrWhiteSpace($PascalABCSourcePath)) {
    $PascalABCSourcePath = $compilerSourceRoot
}
if (-not (Test-Path -LiteralPath $canonicalScript -PathType Leaf)) {
    throw "Canonical compiler-host build script was not found: $canonicalScript"
}

& $canonicalScript `
    -OutputRoot $OutputRoot `
    -PascalABCSourcePath $PascalABCSourcePath `
    -Target $Target `
    -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Canonical compiler-host build failed with exit code $LASTEXITCODE."
}
