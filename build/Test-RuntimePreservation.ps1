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
foreach ($script in @('Merge-RuntimeTrees.ps1', 'Clean-WinPool.ps1', 'Preserve-GeneratedOutput.ps1', 'Assert-RealOperationIdle.ps1', 'Record-BuildIdentity.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $scripts
}
function Assert-True([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Merge-Fixture([string]$target = $runtime) {
    $output = & pwsh -NoProfile -File (Join-Path $scripts 'Merge-RuntimeTrees.ps1') -AppDir $appTree -AgentDir $agentTree -Destination $target -ReplaceDestination 2>&1
    $script:mergeExit = $LASTEXITCODE
    $output | Out-File -LiteralPath (Join-Path $fixture ('merge-' + [guid]::NewGuid().ToString('N') + '.log'))
}
. (Join-Path $scripts 'Preserve-GeneratedOutput.ps1')
$probeTree = Join-Path $fixture 'artifacts/process-guard'
New-Item -ItemType Directory -Path $probeTree | Out-Null
$probeExe = Join-Path $probeTree 'WinPool.Agent.exe'
# A harmless, genuinely named native process exercises the production guard;
# it neither starts WinPool nor opens its data/storage interfaces.
Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/ping.exe') -Destination $probeExe
$probe = Start-Process -FilePath $probeExe -ArgumentList '-n', '30', '127.0.0.1' -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $probeTree 'stdout.log') -RedirectStandardError (Join-Path $probeTree 'stderr.log')
try {
    Assert-True (-not $probe.HasExited) 'Native process guard fixture did not start.'
    Assert-True ([WinPoolBuildProcessProbe]::GetRunningImagePath($probe.Id) -eq $probeExe) 'Limited process query did not return the exact image.'
    $matchingProbe = @(Get-Process -Name 'WinPool.App', 'WinPool.Agent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -eq $probe.Id })
    Assert-True ($matchingProbe.Count -eq 1) 'The fixture PID was not returned by the production process-name filter.'
    $blockError = $null
    try { Assert-WinPoolRuntimeStopped $probeTree }
    catch { $blockError = $_.Exception.GetBaseException().Message }
    $blocked = ($null -ne $blockError) -and $blockError.Contains("PID $($probe.Id), $probeExe")
    $observedBlock = if ($null -eq $blockError) { '<no exception>' } else { $blockError }
    Assert-True $blocked "A running Agent in the target runtime must block replacement with its exact identity. Observed: $observedBlock"
    Assert-WinPoolRuntimeStopped $runtime # The probe belongs to a different tree.
} finally {
    if (-not $probe.HasExited) { $probe.Kill() }
    Assert-True ($probe.WaitForExit(5000)) 'Native process guard fixture did not exit.'
}
Assert-True ($null -eq [WinPoolBuildProcessProbe]::GetRunningImagePath($probe.Id)) 'A confirmed exited process must not block rebuilding.'
Assert-WinPoolRuntimeStopped $probeTree
$probe.Dispose()
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

# Exercise the actual MSBuild target as well as the merge script. Old copied
# timestamps used to make every no-change build replace/hash the complete tree.
Copy-Item -LiteralPath (Join-Path $repository 'Directory.Build.targets') -Destination $fixture
$mergeProject = Join-Path $fixture 'WinPool.App.proj'
Set-Content -LiteralPath $mergeProject -Value @'
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <WinPoolLocalTreeRoot>$(MSBuildThisFileDirectory)artifacts\trees\</WinPoolLocalTreeRoot>
    <WinPoolMergeRuntimeScript>$(MSBuildThisFileDirectory)build\Merge-RuntimeTrees.ps1</WinPoolMergeRuntimeScript>
  </PropertyGroup>
  <Target Name="BuildAgentRuntime" />
  <Import Project="Directory.Build.targets" />
</Project>
'@
$script:incrementalBuildIndex = 0
function Invoke-IncrementalFixture([bool]$expectMerge) {
    $script:incrementalBuildIndex++
    $output = & dotnet msbuild $mergeProject -nologo -t:BuildAgentRuntime -verbosity:normal 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    $output | Out-File -LiteralPath (Join-Path $fixture "incremental-$($script:incrementalBuildIndex).log")
    Assert-True ($exitCode -eq 0) "Incremental fixture failed: $text"
    Assert-True ($text.Contains('Union merge:') -eq $expectMerge) "Unexpected merge decision: $text"
}
function Get-IncrementalFixtureState {
    $stamp = Get-Item -LiteralPath (Join-Path $fixture 'artifacts/build/runtime-merge-Release.stamp')
    $identity = Get-Item -LiteralPath (Join-Path $fixture 'artifacts/build/runtime-Release.identity.json')
    return "$($stamp.LastWriteTimeUtc.Ticks)|$($identity.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $identity.FullName).Hash)"
}
(Get-Item -LiteralPath (Join-Path $appTree 'shared.dll')).LastWriteTimeUtc = [DateTime]::UtcNow.AddYears(-1)
(Get-Item -LiteralPath (Join-Path $agentTree 'shared.dll')).LastWriteTimeUtc = [DateTime]::UtcNow.AddYears(-1)
Invoke-IncrementalFixture $true
$unchangedState = Get-IncrementalFixtureState
Invoke-IncrementalFixture $false
Assert-True ((Get-IncrementalFixtureState) -eq $unchangedState) 'No-change build rewrote its merge stamp or build identity.'
Set-Content -LiteralPath (Join-Path $runtime 'Data/user.txt') -Value 'portable data changed'
Invoke-IncrementalFixture $false
Assert-True ((Get-IncrementalFixtureState) -eq $unchangedState) 'Portable Data participated in generated runtime inputs.'
$appAsset = Join-Path $appTree 'Assets/nested.xbf'
New-Item -ItemType Directory -Path (Split-Path -Parent $appAsset) -Force | Out-Null
Set-Content -LiteralPath $appAsset -Value 'app resource'
Invoke-IncrementalFixture $true
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'Assets/nested.xbf')) 'New App resource was not merged.'
$agentAsset = Join-Path $agentTree 'agent-only.dll'
Set-Content -LiteralPath $agentAsset -Value 'agent resource'
Invoke-IncrementalFixture $true
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'agent-only.dll')) 'New Agent file was not merged.'
Move-WinPoolGeneratedOutput $appAsset | Out-Null
Invoke-IncrementalFixture $true
Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtime 'Assets/nested.xbf'))) 'Deleted App resource remained in the runtime.'
Move-WinPoolGeneratedOutput (Join-Path $runtime 'shared.dll') | Out-Null
Invoke-IncrementalFixture $true
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'shared.dll')) 'Missing destination file was not restored.'
Set-Content -LiteralPath (Join-Path $runtime 'unexpected.dll') -Value 'untracked generated file'
Invoke-IncrementalFixture $true
Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtime 'unexpected.dll'))) 'Unexpected destination member was not retired.'
Invoke-IncrementalFixture $false
Assert-True ((Get-Content -LiteralPath (Join-Path $runtime 'Data/user.txt') -Raw).Trim() -eq 'portable data changed') 'Incremental replacement lost portable data.'
& pwsh -NoProfile -File (Join-Path $scripts 'Clean-WinPool.ps1') -BuildIntermediatesOnly | Out-File -LiteralPath (Join-Path $fixture 'pre-rebuild-clean.log')
Assert-True ($LASTEXITCODE -eq 0) 'Rebuild preparation failed.'
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'shared.dll')) 'Rebuild preparation removed the runnable version before a successful build.'
& pwsh -NoProfile -File (Join-Path $scripts 'Clean-WinPool.ps1') | Out-File -LiteralPath (Join-Path $fixture 'clean.log')
Assert-True ($LASTEXITCODE -eq 0) 'Clean failed.'
Assert-True (Test-Path -LiteralPath (Join-Path $runtime 'Data/user.txt')) 'Clean moved portable data.'
Assert-True (Test-Path -LiteralPath $evidence) 'Clean moved prior evidence.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $runtime 'shared.dll'))) 'Generated runtime was not moved.'
Set-Content -LiteralPath (Join-Path $fixture 'result.txt') -Value 'passed: exact live process path, target runtime refusal, other runtime exclusion, exited process, collision, occupied target, merge, native MSBuild no-change/changed members/destination repair, portable data and prior evidence retention'
Write-Output "Runtime preservation passed. Evidence: $fixture"
