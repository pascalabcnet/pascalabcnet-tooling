[CmdletBinding()]
param(
    [ValidateSet('all', 'net-framework', 'net10')]
    [string]$Target = 'all',
    [string]$PascalABCSourcePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerSourceRoot = Join-Path $repositoryRoot 'pascalabcnet'
$canonicalScript = Join-Path $compilerSourceRoot 'scripts\test-compiler-host.ps1'

if ([string]::IsNullOrWhiteSpace($PascalABCSourcePath)) {
    $PascalABCSourcePath = $compilerSourceRoot
}
if (-not (Test-Path -LiteralPath $canonicalScript -PathType Leaf)) {
    throw "Canonical compiler-host test script was not found: $canonicalScript"
}

& $canonicalScript -Target $Target -PascalABCSourcePath $PascalABCSourcePath
if ($LASTEXITCODE -ne 0) {
    throw "Canonical compiler-host smoke test failed with exit code $LASTEXITCODE."
}
