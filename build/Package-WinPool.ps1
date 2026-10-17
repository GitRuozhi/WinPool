[CmdletBinding()]
param(
    [string]$TestedScope = '未登记；打包不执行构建或测试。 / Not recorded; packaging does not run builds or tests.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Preserve-GeneratedOutput.ps1')

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appRoot = Assert-WinPoolCheckoutPath (Join-Path $repository 'artifacts/trees/Release/App')
$agentRoot = Assert-WinPoolCheckoutPath (Join-Path $repository 'artifacts/trees/Release/Agent')
$standardRoot = Assert-WinPoolCheckoutPath (Join-Path $repository 'artifacts/Release')
[xml]$props = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$major = [int]$props.SelectSingleNode('/Project/PropertyGroup/WinPoolVersionMajor').InnerText
$minor = [int]$props.SelectSingleNode('/Project/PropertyGroup/WinPoolVersionMinor').InnerText
$iteration = [int]$props.SelectSingleNode('/Project/PropertyGroup/WinPoolVersionIteration').InnerText
$version = 'V{0}.{1}{2}' -f $major, $minor, $iteration
$fileVersion = '{0}.{1}.{2}.0' -f $major, $minor, $iteration
$baseline = (& git -C $repository rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'The source Git baseline could not be read.' }
$changes = @(& git -C $repository status --porcelain=v1 --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'The source Git status could not be read.' }
$sourceState = if ($changes.Count -gt 0) { 'dirty / 有未提交变更' } else { 'clean / 无未提交变更' }

function Get-RuntimeFiles([string]$Root, [switch]$AllowDevelopmentData) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Runtime tree missing: $Root" }
    $files = @{}
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Root)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Dequeue() -Force)) {
            $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
            $privateData = $relative -match '(?i)(^|/)(Data|Diagnostics|Evidence|Logs|Monitoring|MonitoringArchives|Exports|TestResults)(/|$)' -or
                $relative -match '(?i)(\.(db|sqlite|sqlite3)(-[^/]*)?|\.(wal|shm|log|jsonl|dmp|7z|zip))$' -or
                ($item.Extension -eq '.json' -and $item.Name -notmatch '(?i)\.(deps|runtimeconfig)\.json$')
            if ($privateData) {
                if ($AllowDevelopmentData) { continue }
                throw "Input runtime contains developer/user data; nothing was packaged: $($item.FullName)"
            }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Runtime contains a link: $($item.FullName)" }
            if ($relative -match '(?i)(^|/)(OriginArtWork|local-assets|src|tests|build|App|Agent|trees|publish)(/|$)' -or
                (-not $item.PSIsContainer -and
                    $item.Name -match '(?i)\.(ps1|psm1|psd1|cmd|bat|cs|xaml|csproj|slnx|psd|ai|xcf|blend)$|\.runtimeconfig\.dev\.json$')) {
                throw "Runtime contains source, script, or another runtime tree: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) }
            elseif ($item.Extension -ne '.pdb') { $files[$relative] = $item.FullName }
        }
    }
    return $files
}

$appFiles = Get-RuntimeFiles $appRoot
$agentFiles = Get-RuntimeFiles $agentRoot
$standardFiles = Get-RuntimeFiles $standardRoot -AllowDevelopmentData
foreach ($name in @('App', 'Agent')) {
    $root = if ($name -eq 'App') { $appRoot } else { $agentRoot }
    foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
        $path = Join-Path $root "WinPool.$name.$suffix"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required runtime file missing: $path" }
        if ($suffix -in @('exe', 'dll')) {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
            if ($info.FileVersion -ne $fileVersion -or $info.ProductVersion -ne $version) {
                throw "Version mismatch: $path; expected $version / $fileVersion, found $($info.ProductVersion) / $($info.FileVersion)"
            }
        }
    }
}
foreach ($relative in @('WinPool.App.pri', 'App.xbf', 'MainWindow.xbf', 'Assets/CAppIcon.ico')) {
    if (-not $appFiles.ContainsKey($relative)) { throw "Required App resource missing: $relative" }
}
foreach ($relative in @('Tools/7zip/7za.exe', 'Tools/7zip/License.txt', 'Tools/7zip/NOTICE.txt')) {
    if (-not $agentFiles.ContainsKey($relative)) { throw "Required bundled 7-Zip file missing: $relative" }
}

# Verify the complete runtime union against the already validated standard tree.
# Standard Data is neither traversed nor copied.
$expectedHashes = @{}
foreach ($map in @($appFiles, $agentFiles)) {
    foreach ($relative in $map.Keys) {
        $hash = (Get-FileHash -LiteralPath $map[$relative] -Algorithm SHA256).Hash
        if ($expectedHashes.ContainsKey($relative) -and $expectedHashes[$relative] -ne $hash) {
            throw "App/Agent runtime collision: $relative"
        }
        $expectedHashes[$relative] = $hash
    }
}
if ($expectedHashes.ContainsKey('TESTING.md')) { throw 'Input runtime already contains the package testing-notes path.' }
if ($expectedHashes.Count -ne $standardFiles.Count) { throw 'Runtime inputs differ from the validated standard tree file set.' }
foreach ($relative in $expectedHashes.Keys) {
    if (-not $standardFiles.ContainsKey($relative) -or
        (Get-FileHash -LiteralPath $standardFiles[$relative] -Algorithm SHA256).Hash -ne $expectedHashes[$relative]) {
        throw "Runtime input differs from the validated standard tree: $relative"
    }
}

$batch = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N')
$batchRoot = Assert-WinPoolCheckoutPath (Join-Path $repository "artifacts/packages/$version/$batch")
$packageName = "WinPool-$version-win-x64"
$packageRoot = Join-Path $batchRoot $packageName
if (Test-Path -LiteralPath $batchRoot) { throw "Package batch already exists: $batchRoot" }
New-Item -ItemType Directory -Path $batchRoot | Out-Null
& (Join-Path $PSScriptRoot 'Merge-RuntimeTrees.ps1') -AppDir $appRoot -AgentDir $agentRoot -Destination $packageRoot -SkipPdb | Write-Host
$packagedFiles = Get-RuntimeFiles $packageRoot
if ($packagedFiles.Count -ne $expectedHashes.Count) { throw 'Merged package runtime file set changed.' }
foreach ($relative in $expectedHashes.Keys) {
    if (-not $packagedFiles.ContainsKey($relative) -or
        (Get-FileHash -LiteralPath $packagedFiles[$relative] -Algorithm SHA256).Hash -ne $expectedHashes[$relative]) {
        throw "Packaged runtime hash mismatch: $relative"
    }
}

$notes = @"
# WinPool $version 测试候选 / Test candidate

- 版本 / Version: $version (Windows x64)
- 源码基线 / Source commit: $baseline
- 源码状态 / Source state: $sourceState
- 打包时间 / Packaged at: $([DateTime]::UtcNow.ToString('u'))
- 开发机已测范围 / Developer-machine tested scope: $TestedScope

解压整个目录并运行 WinPool.App.exe。App 与 Agent 必须配套。关闭主窗口后 Agent 仍在托盘；从托盘退出 WinPool，再替换程序文件。
Extract the complete folder and run WinPool.App.exe. Keep App and Agent together. Closing the window leaves the Agent in the tray; exit WinPool from the tray before replacing program files.

默认数据为 %LocalAppData%\WinPool；设置中可显式选择程序旁可写的 Data。程序包不含已有开发数据。数据位置指针为 %LocalAppData%\WinPool.storage-location.json。
Data defaults to %LocalAppData%\WinPool; Settings can explicitly select a writable Data folder beside the program. Existing developer data is absent from this package. The location pointer is %LocalAppData%\WinPool.storage-location.json.

可查看拓扑、编辑模拟系统、导入/导出 JSON、监控及执行产品已支持的真实存储操作。模拟编辑不写真实磁盘；真实编辑默认关闭，按准确目标确认；Unknown 结果阻止后续写入，不自动重放。
Available areas include topology, simulation editing, JSON import/export, monitoring, and supported real storage operations. Simulation edits do not write disks. Real editing defaults off and requires target confirmation; an unknown outcome blocks further writes without replay.

当前限制：没有安装包或 Store 发布；Windows/provider 能力决定真实操作支持；不支持真实 MBR→GPT、已有 VD/层扩展到 MAX、直接池成员迁移；CSV 只导出当前活动监控库，没有历史浏览器。
Current limits: no installer or Store release; real operations depend on Windows/provider capabilities. Real MBR-to-GPT conversion, expanding an existing VD/tier to MAX, and direct pool-member migration are unsupported. CSV exports the current monitoring database; there is no history browser.

外部建议：核对不同 Windows x64 环境的启动/刷新/退出、模拟草稿/提交/导入导出、20 Hz 监控及断连重连。SSD/SCM、多成员、冗余、多层硬件和长时运行/DPI矩阵尚需专门覆盖；本包不代表这些组合均已通过。
Suggested external checks: startup/refresh/exit on different Windows x64 environments, simulation drafts/commit/import/export, and 20 Hz monitoring with reconnect. SSD/SCM, multiple members, redundancy, multiple hardware tiers, long runs, and DPI combinations need dedicated coverage.

反馈请附版本、Windows/设备环境、复现步骤、预期/实际结果和相关错误；硬件报告保留原值，分享前确认内容范围。
Feedback: include version, Windows/device environment, reproduction steps, expected/actual results, and relevant errors. Hardware reports retain original field values; review the content before sharing.

WinPool 自有代码保留所有权利；随附 7-Zip 的许可证与来源说明见 Tools/7zip/License.txt 和 NOTICE.txt。
All rights are reserved for WinPool's own code. Bundled 7-Zip licensing and source notices are in Tools/7zip/License.txt and NOTICE.txt.
"@
[IO.File]::WriteAllText((Join-Path $packageRoot 'TESTING.md'), $notes + "`n", [Text.UTF8Encoding]::new($false))
$zipPath = Join-Path $batchRoot "$packageName.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = $zipPath + '.sha256'
[IO.File]::WriteAllText($checksumPath, "$sha256  $packageName.zip`n", [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Version = $version; Baseline = $baseline; SourceState = $sourceState
    RuntimeFileCount = $expectedHashes.Count; PackageRoot = $packageRoot
    ZipPath = $zipPath; Sha256Path = $checksumPath; Sha256 = $sha256
}
