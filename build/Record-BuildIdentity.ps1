[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeRoot, [Parameter(Mandatory)][string]$IdentityPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime = [IO.Path]::GetFullPath($RuntimeRoot)
if (-not $runtime.StartsWith($repository + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw "The build runtime is outside the checkout: $runtime"
}
$buildSha = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the built source.' }
$changes = @(& git -C $repository status --porcelain=v1 --untracked-files=normal -- src assets build Directory.Build.props Directory.Build.targets global.json WinPool.slnx NuGet.Config)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the controlled build input state.' }
$files = [ordered]@{}
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($runtime)
while ($pending.Count) {
    foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($runtime,$item.FullName).Replace('\','/')
        if ($relative -eq 'Data') { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Build runtime contains a link: $($item.FullName)" }
        if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
        else { $files[$relative] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    }
}
$identity = [ordered]@{ buildSha=$buildSha; controlledInputState=if($changes.Count){'dirty'}else{'clean'};
    controlledChanges=$changes; builtAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); runtimeFiles=$files }
$identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $IdentityPath -Encoding utf8
Write-Output "Build identity: $IdentityPath ($buildSha, $($identity.controlledInputState), $($files.Count) files)"
