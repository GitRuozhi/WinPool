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
$identityPath = Join-Path $repository 'artifacts/build/runtime-Release.identity.json'
if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw 'The runtime has no build-time identity; build it before packaging.' }
$buildIdentity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
$baseline = [string]$buildIdentity.buildSha
if ($baseline -notmatch '^[0-9a-f]{40}$') { throw 'The build-time source identity is invalid.' }
$sourceState = [string]$buildIdentity.controlledInputState

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
foreach ($relative in @('Tools/7zip/7za.exe', 'Tools/7zip/License.txt', 'Tools/7zip/NOTICE.txt', 'Tools/7zip/LGPL-2.1.txt')) {
    if (-not $agentFiles.ContainsKey($relative)) { throw "Required bundled 7-Zip file missing: $relative" }
}
if (-not $appFiles.ContainsKey('Tools/Licenses/README.md') -or
    -not $agentFiles.ContainsKey('Tools/Licenses/README.md')) {
    throw 'Bundled runtime license notices are missing.'
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
    if (-not $buildIdentity.runtimeFiles.Contains($relative) -or
        $buildIdentity.runtimeFiles[$relative] -ne $expectedHashes[$relative]) {
        throw "Runtime differs from its build-time identity: $relative"
    }
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

$signatureSummary = @('App', 'Agent') | ForEach-Object {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot "WinPool.$_.exe")
    "WinPool.$_.exe=$($signature.Status)"
}
$notes = @"
# WinPool $version 公开测试版 / Public test release

欢迎使用 WinPool！这是面向所有用户的公开测试版本，欢迎试用并分享问题和建议。请先阅读下方运行说明和已知限制。
Welcome to WinPool! This public test release is open to everyone. Try it, tell us what works, and share problems or suggestions. Please read the running instructions and known limits below.

- 版本 / Version: $version (Windows x64)
- 源码基线 / Source commit: $baseline
- 源码状态 / Source state: $sourceState
- 打包时间 / Packaged at: $([DateTime]::UtcNow.ToString('u'))
- 开发机已测范围 / Developer-machine tested scope: $TestedScope
- EXE 签名状态 / Executable signature status: $($signatureSummary -join '; ')

解压整个目录并运行 WinPool.App.exe。App 与 Agent 必须配套。关闭主窗口后 Agent 仍在托盘；从托盘退出 WinPool，再替换程序文件。
Extract the complete folder and run WinPool.App.exe. Keep App and Agent together. Closing the window leaves the Agent in the tray; exit WinPool from the tray before replacing program files.

产品使用 Windows 自带的 Windows PowerShell 5.1 存储宿主，不要求安装 PowerShell 7 或开发 SDK。.NET、WindowsDesktop 和 Windows App SDK 运行资产已随包提供。Windows 10 22H2、Windows 11 24H2/25H2 是兼容验证目标；本包实测范围以本页登记为准，外部环境由开发者另行提供结果。.NET 10 上游支持列表未列 Windows 10 Pro 22H2，项目兼容目标与上游支持须分别理解。
Storage uses the system Windows PowerShell 5.1 host; PowerShell 7 and development SDKs are unnecessary. .NET, WindowsDesktop and Windows App SDK runtime assets are bundled. Windows 10 22H2 and Windows 11 24H2/25H2 are compatibility targets; this page records the actual tested scope, with external results supplied separately. Windows 10 Pro 22H2 is not listed in the upstream .NET 10 support matrix.

通常以普通权限启动；执行真实存储修改时使用产品的提权交接与确认流程。取消UAC不进入真实写入；不要为普通查看一律以管理员运行。
Start with ordinary user privileges. Use the product elevation handoff and confirmation flow for real storage changes. Cancelling UAC does not enter the real write flow.

默认数据为 %LocalAppData%\WinPool；设置中可显式选择程序旁可写的 Data。程序包不含已有开发数据。数据位置指针为 %LocalAppData%\WinPool.storage-location.json。
Data defaults to %LocalAppData%\WinPool; Settings can explicitly select a writable Data folder beside the program. Existing developer data is absent from this package. The location pointer is %LocalAppData%\WinPool.storage-location.json.

可查看拓扑、编辑模拟系统、导入/导出 JSON、监控及执行产品已支持的真实存储操作。模拟编辑不写真实磁盘；真实编辑默认关闭，按准确目标确认；Unknown 结果阻止后续写入，不自动重放。
Available areas include topology, simulation editing, JSON import/export, monitoring, and supported real storage operations. Simulation edits do not write disks. Real editing defaults off and requires target confirmation; an unknown outcome blocks further writes without replay.

Unknown时使用只读重查确认结果；Agent重启不续写已发请求。结构任务中止后须重新确认剩余目标，才可继续执行。新建MAX采用整数GiB搜索并保留已验证成功值，容量拒绝须证明目标无变化；不以provider报错或原生MAX作为成功依据。
Use read-only recheck for an unknown outcome; restarting the Agent never replays an issued request. Confirm remaining structure goals again after an interrupted job. Creation MAX searches in integer GiB and retains verified successful sizes; capacity rejection requires proof that the target was unchanged.

模拟编辑在合法 Apply/Enter 后立即显示本地候选，后台合并保存；保存中、失败和冲突均有状态。保存失败保留本地内容；退出或提权交接须先完成保存或明确处理未保存内容。完整可导入 JSON envelope 的规范 UTF-8 编码上限为 64 MiB，单个原子编辑 intent 独立限 64 MiB；限制按编码字节计，不是内存上限。
Valid Apply/Enter actions show a local simulation candidate and schedule a merged background save. Pending, failed and conflicting saves are reported; failures retain local content. Exit and elevation handoff require saving or explicitly resolving unsaved content. A complete importable JSON envelope has a 64 MiB canonical UTF-8 limit; an atomic edit intent has a separate 64 MiB limit. These are encoded-byte limits, not memory limits.

尚未Apply／Enter的输入仍为草稿，不表示已保存。程序异常终止时，不保证恢复未保存的内存草稿；正常退出时按保存状态提示处理。
Input not completed with Apply/Enter remains a draft. Unsaved in-memory drafts are not guaranteed to survive a crash; resolve the reported save state during normal exit.

当前限制：没有安装包或 Store 发布；Windows/provider 能力决定真实操作支持；不支持真实 MBR→GPT、已有 VD/层扩展到 MAX、直接池成员迁移；CSV 只导出当前活动监控库，没有历史浏览器。
Current limits: no installer or Store release; real operations depend on Windows/provider capabilities. Real MBR-to-GPT conversion, expanding an existing VD/tier to MAX, and direct pool-member migration are unsupported. CSV exports the current monitoring database; there is no history browser.

外部建议：核对不同 Windows x64 环境的启动/刷新/退出、模拟草稿/提交/导入导出、20 Hz 监控及断连重连。SSD/SCM、多成员、冗余、多层硬件和长时运行/DPI矩阵尚需专门覆盖；本包不代表这些组合均已通过。
Suggested external checks: startup/refresh/exit on different Windows x64 environments, simulation drafts/commit/import/export, and 20 Hz monitoring with reconnect. SSD/SCM, multiple members, redundancy, multiple hardware tiers, long runs, and DPI combinations need dedicated coverage.

反馈入口：https://github.com/GitRuozhi/WinPool/issues 。请附版本及 ZIP SHA-256、Windows/设备环境、复现步骤、预期/实际结果、错误码和自愿提供的相关日志；硬件报告保留原值，分享前确认内容范围。
Feedback: https://github.com/GitRuozhi/WinPool/issues . Include version and ZIP SHA-256, Windows/device environment, reproduction steps, expected/actual results, error codes and any logs you choose to share. Hardware reports retain original values; review the content before sharing.

签名状态见本页顶部；NotSigned 表示未签名，系统可能显示未知发布者或来源提示，不需要关闭系统防护。
The signature status is recorded above. NotSigned means unsigned; Windows may show publisher or origin prompts. Disabling system protection is unnecessary.

WinPool 自有代码保留所有权利。随包运行库的许可／notice 及来源见 Tools/Licenses/README.md；7-Zip 单独见 Tools/7zip/License.txt 和 NOTICE.txt。
All rights are reserved for WinPool's own code. Runtime licenses, notices and sources are indexed in Tools/Licenses/README.md; 7-Zip is covered separately in Tools/7zip/License.txt and NOTICE.txt.
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
