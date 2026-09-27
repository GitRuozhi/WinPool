[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repository ('artifacts/test-results/runtime-preservation-' + [guid]::NewGuid().ToString('N'))
$scripts = Join-Path $fixture 'build'
$appTree = Join-Path $fixture 'artifacts/trees/App'
$agentTree = Join-Path $fixture 'artifacts/trees/Agent'
$runtime = Join-Path $fixture 'artifacts/Release'
$evidence = Join-Path $fixture 'artifacts/test-results/existing.txt'
foreach ($directory in @($scripts, $appTree, $agentTree, $runtime, (Split-Path -Parent $evidence))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
foreach ($script in @('Merge-RuntimeTrees.ps1', 'Clean-WinPool.ps1', 'Preserve-GeneratedOutput.ps1', 'Assert-RealOperationIdle.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $scripts
}
function Assert-True([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Merge-Fixture([string]$target = $runtime) {
    $output = & pwsh -NoProfile -File (Join-Path $scripts 'Merge-RuntimeTrees.ps1') -AppDir $appTree -AgentDir $agentTree -Destination $target -ReplaceDestination 2>&1
    $script:mergeExit = $LASTEXITCODE
    $output | Out-File -LiteralPath (Join-Path $fixture ('merge-' + [guid]::NewGuid().ToString('N') + '.log'))
}
Set-Content -LiteralPath (Join-Path $runtime 'old.txt') -Value 'old runtime'
New-Item -ItemType Directory -Path (Join-Path $runtime 'Data') | Out-Null
Set-Content -LiteralPath (Join-Path $runtime 'Data/user.txt') -Value 'portable data'
Set-Content -LiteralPath $evidence -Value 'existing evidence'
Set-Content -LiteralPath (Join-Path $appTree 'shared.dll') -Value 'app'
Set-Content -LiteralPath (Join-Path $agentTree 'shared.dll') -Value 'different'
$outside = Join-Path $repository ('artifacts/test-results/runtime-outside-' + [guid]::NewGuid().ToString('N'))
Merge-Fixture $outside
Assert-True ($mergeExit -ne 0) 'A destination outside the script checkout must fail.'
Assert-True (-not (Test-Path -LiteralPath $outside)) 'An outside destination was created.'
$linkedAncestor = Join-Path $fixture 'linked-artifacts'
New-Item -ItemType Junction -Path $linkedAncestor -Target (Join-Path $fixture 'artifacts') | Out-Null
$linkedDestination = Join-Path $linkedAncestor 'linked-runtime'
Merge-Fixture $linkedDestination
Assert-True ($mergeExit -ne 0) 'A destination through a junction must fail.'
Assert-True (-not (Test-Path -LiteralPath $linkedDestination)) 'A junction destination was created.'
Merge-Fixture
Assert-True ($mergeExit -ne 0) 'A hash collision must fail.'
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'old.txt')) 'A collision changed the old runtime.'
Set-Content -LiteralPath (Join-Path $agentTree 'shared.dll') -Value 'app'
$locked = [IO.File]::Open((Join-Path $runtime 'old.txt'), 'Open', 'Read', 'Read')
try {
    Merge-Fixture
    Assert-True ($mergeExit -ne 0) 'An occupied runtime must refuse replacement.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $runtime 'old.txt') -Raw).Trim() -eq 'old runtime') 'Occupied runtime was damaged.'
} finally { $locked.Dispose() }
Merge-Fixture
Assert-True ($mergeExit -eq 0) 'An available runtime should merge.'
Assert-True ((Get-Content -LiteralPath (Join-Path $runtime 'shared.dll') -Raw).Trim() -eq 'app') 'Merged runtime missing.'
Assert-True ((Get-Content -LiteralPath (Join-Path $runtime 'Data/user.txt') -Raw).Trim() -eq 'portable data') 'Portable data lost.'
& pwsh -NoProfile -File (Join-Path $scripts 'Clean-WinPool.ps1') -BuildIntermediatesOnly | Out-File -LiteralPath (Join-Path $fixture 'pre-rebuild-clean.log')
Assert-True ($LASTEXITCODE -eq 0) 'Rebuild preparation failed.'
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'shared.dll')) 'Rebuild preparation removed the runnable version before a successful build.'
& pwsh -NoProfile -File (Join-Path $scripts 'Clean-WinPool.ps1') | Out-File -LiteralPath (Join-Path $fixture 'clean.log')
Assert-True ($LASTEXITCODE -eq 0) 'Clean failed.'
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'Data/user.txt')) 'Clean moved portable data.'
Assert-True (Test-Path -LiteralPath $evidence) 'Clean moved prior evidence.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtime 'shared.dll'))) 'Generated runtime was not moved.'
Set-Content -LiteralPath (Join-Path $fixture 'result.txt') -Value 'passed: collision, occupied target, merge, portable data and prior evidence retention'
Write-Output "Runtime preservation passed. Evidence: $fixture"
