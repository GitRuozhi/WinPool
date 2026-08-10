---
project: WinPool
objective: 完成 V0.31 文档、目录、版本与发布工程重构，并承接 V0.2 未完成事项
status: completed
architecture_version: V0.3
integration_checkpoint: V0.31
acceptance_checkpoint: V0.32
current_work_package: V0.32_USER_CONFIRMATION
next_action: 等待用户人工确认重构结果，并按 CARRY-01～CARRY-11 执行 V0.32 实机验收
branch: main
remote: origin
scope: Program/WinPool only
last_updated: 2026-08-10
code_gate: passed
native_integration_gate: passed
manual_gate: unverified
remote_gate: passed
---

# WinPool V0.3 / V0.31 持续重构计划

## 1. 目标与边界

本计划是 WinPool 当前唯一活动计划，持续到 V0.31 自动门完成并推送源码集合为止。V0.31 是
重构集成候选；它不代表人工验收、二进制发布或 GitHub Release 已完成。用户人工确认重构成果
并完成核心实机门后，版本才可记为 V0.32。

范围仅限 `D:\Coding\Research03_WinPool\Program\WinPool`。不重构 Dite、KS、Research、
Tests、Showcase 或其他项目；不增加真实存储结构修改能力；不改变现有 C# 项目、namespace、
ProjectReference 或进程边界，除非为修复 V0.31 的发布布局缺陷所必需。

最新父项目规则优先于早期草案：WinPool 不创建 `Docs/docs` 或仓库内 Archive。被取代的文档和
其他旧内容都进入项目根 `Old\Program\WinPool`；低价值生成物进入项目根 `Rubbish`。因此此前
有关 `docs/Archive` 的提案已撤销。

## 2. 当前事实与组织目标

当前工作树包含：四份根 Markdown、16 份 Plan 文档、一个根 `Ref` 参考目录、三个未跟踪美术
目录、15 个 `src` 项目、2 个 `workers` 项目和 16 个测试项目。V0.2 文档同时混合设计、历史、
实施数字和未完成验收；根 `Ref`、`Arting`、`Icon`、`Musume` 也没有稳定归属。

V0.31 后的目标树为：

```text
WinPool/
├─ README.md                    # 英文产品入口
├─ README_CN.md                 # 中文产品入口
├─ AGENTS.md                    # 长期 Agent 约束与版本/Git 规则
├─ DEVELOP.md                   # 架构、环境、构建、发布和目录规则
├─ Plan/
│  ├─ README.md                 # 当前计划索引
│  ├─ 16_V0.3文档与目录重构计划.md  # 唯一活动计划
│  └─ Reference/
│     └─ AI-Agent-Harness-项目管理架构参考.md
├─ local-assets/                # 被忽略；不进入 Git
│  ├─ art-source/
│  ├─ icons/
│  └─ welcome/
├─ src/                         # 保持现有 15 个项目布局
├─ workers/                     # 保持现有 2 个进程项目布局
├─ tests/                       # 保持现有测试项目布局
├─ .gitignore
├─ global.json
└─ WinPool.slnx
```

不创建空的 `Docs`、`Archive`、`tools`、`Temp`、`artifacts` 或额外应用目录。

## 3. 文档职责与历史迁移

| 位置 | 唯一职责 | 不保存 |
| --- | --- | --- |
| `README.md` / `README_CN.md` | 产品入口、当前版本、启动、已知限制、导航 | 临时进度和测试数字 |
| `AGENTS.md` | 安全、授权、Git、版本、必读顺序 | 当前任务日志 |
| `DEVELOP.md` | 架构、项目职责、环境、构建、发布和目录策略 | 当前验收结论 |
| `Plan/16...md` | 当前 V0.3 工作包、继承任务、状态、证据、下一动作 | 已完成版本的详细历史 |
| `Plan/Reference/*` | 非权威参考，必须标明来源/日期 | 当前产品需求 |
| `Old\Program\WinPool\Plan\V0.2` | V0.2 的冻结 Plan 原文和索引 | 当前任务 |

迁移规则：

1. 从 Plan 01～15 先提取仍有效的长期规则、V0.21 已发生事实和 V0.2 未完成项。
2. 长期规则合并到四份根文档；已发生事实保留在 Git 标签/提交、README 和 DEVELOP 的版本段；
   不新建独立 CHANGELOG 或 docs 目录。
3. 未完成项必须保留为第 9 节的 `CARRY` 或 `DEBT`，不能随归档消失。
4. 对每份归档文档计算 SHA-256、保留原相对路径，并移动到项目根 Old；不直接删除。
5. `Plan/README.md` 只列出唯一活动计划和参考资料，不再承担 V0.2 历史目录。

## 4. 本地非代码资产

`Arting`、`Icon`、`Musume` 及同类开发者资源迁移到被忽略的 `local-assets`：

- `Arting` → `local-assets/art-source`；
- `Icon` → `local-assets/icons`；
- `Musume` → `local-assets/welcome`。

这些文件不进入 Git，不计入 V0.31 暂存范围，也不得因为被忽略而删除、归档或视为无用。移动前后
必须核对文件数、总字节数和 SHA-256。开发者负责其本地备份；以后只有用户明确批准 Git LFS、
独立资产仓库或其他方案后才能改变策略。

若某项资源将成为运行时构建必需输入，先由用户决定可复现分发方案；当前 V0.31 不接入这些
本地图片、PSD、GIF 或其他二进制资源。

## 5. 版本规则

版本格式为 `Va.bc`：`a` 是大版本，`b` 是小版本，`c` 是小版本内一位迭代阶段。

- 架构和计划只规定 `Va.b`，当前架构为 `V0.3`。
- 未经用户明确指定，不预分配 c；c 在实际开发中形成检查点。
- c 到 8 时提醒范围控制；c=9 后不得继续增加，必须缩小范围、合并工作或提升小版本。
- 普通 c 检查点只做本地提交。
- V0.31 是用户明确授权的例外：自动门全部通过后自动提交并推送源码集合，但不创建 tag、
  release 或二进制发布包。
- V0.32 只能在用户人工确认重构结果和核心实机门后建立本地验收提交；是否推送由用户当次
  指令决定。

V0.31 建立单一版本源，统一以下内容：

- 显示版本 `V0.31` 与架构版本 `V0.3`；
- .NET/文件技术版本 `0.3.1.0`；
- ProductInformation、About/欢迎文本、HTTP User-Agent、导出标题；
- 版本相关架构测试和公开文档。

普通业务错误不再硬编码版本号。

## 6. WinPool 专属质量模型

参考项目的浏览器、DOM、网页视觉回归和服务器测试不适用于 WinPool。WinPool 采用四层证据：

1. **静态与结构门**：目录、链接、计划唯一性、Git 跟踪范围、版本一致性、架构边界、
   无自由命令和敏感信息检查。
2. **.NET 自动门**：单元、集成、SQLite、IPC、Agent、Worker、工具适配器、算法和安全
   测试，串行执行以避免多进程/SQLite 竞态；Release 构建与依赖漏洞检查。
3. **Windows 原生集成门**：真实 publish/staging 的四进程目录、命名管道 ACL、Worker
   回收、SQLite 文件、只读采集和安全外部工具边界。自动化不能点击的 UAC、托盘和原生
   文件夹选择器必须留给人工门。
4. **人工/设备门**：WinUI 窗口、双语、主题、DPI/高对比度、键盘、托盘、UAC、D: 登记
   目录测试、跨窗口监控和数据位置往返。

每项只可记录 `passed`、`failed`、`unverified`、`not_required` 或 `deferred_by_user`。
自动测试通过不能替代人工门；环境不可用不是通过。

## 7. 发布工程修复范围

V0.21 的单次 App publish 根目录缺少运行时所需的 `Agent` 子树，而 GitHub ZIP 依靠人工合并
四个项目输出且含顶层重复可执行文件。V0.31 必须：

1. 让 App、Agent、TestWorker、Broker 的 publish 输出稳定汇聚到一个 staging 根；
2. 保证相对路径为 `Agent/WinPool.Agent.exe`、`Agent/TestWorker/WinPool.TestWorker.exe` 和
   `Agent/Broker/WinPool.ElevatedBroker.exe`；
3. 不出现重复的顶层 Agent/Worker/Broker EXE；
4. 添加自动布局测试或验证脚本，检查真实 staging 树，而不是只查 `.csproj` 字符串；
5. 不把 staging 目录或发布二进制提交到 Git，不创建 release。

## 8. V0.31 工作包

每轮只允许一个 `in_progress`。完成后更新第 12 节、顶部状态和 `next_action`。

### WP0：基线与规则统一

- 对齐本计划、AGENTS、DEVELOP 与最新父项目归档规则；
- 清点当前 Git 修改、未跟踪文件、忽略文件、目录大小和 SHA-256；
- 明确用户既有改动与 V0.31 改动边界；
- fetch 远端但不合并；
- 把本计划状态设为 in_progress。

### WP1：文档与参考重构

- 让 README/README_CN、AGENTS、DEVELOP 和 Plan 各自只承担第 3 节职责；
- 迁移 `Ref` 到 `Plan/Reference`，增加非权威/来源日期说明；
- 提取 V0.2 未完成项到第 9 节；
- 将 Plan 01～15 和旧索引归档到项目根 Old，保留唯一活动 Plan；
- 更新全部链接、路径和版本表述。

### WP2：目录与本地资产重构

- 在 `.gitignore` 精确忽略 `local-assets/`，移除会错误隐藏项目文本的过宽规则；
- 迁移三类本地资产并核对哈希；
- 移除根 `Ref`、`Arting`、`Icon`、`Musume` 的多余入口；
- 不改变 src/workers/tests 的项目路径。

### WP3：版本源与文档版本统一

- 创建仓库级版本属性源，并使所有生产项目采用相同程序集/文件版本；
- 将 App 运行时显示版本和 User-Agent 改为从版本源读取；
- 移除 Execution、页面和本地化中无兼容意义的 V0.21 硬编码；
- 更新 README、AGENTS、DEVELOP 和活动 Plan 为 V0.31 集成候选；
- 添加防漂移的架构测试。

### WP4：四进程 staging 与自动验证

- 修复 publish 输出路径；
- 添加真实 staging 布局检查；
- 确认 staging 不含 `.ps1`、local-assets、SQLite、测试结果或外部工具；
- 在不触发真实存储结构修改的前提下运行发布验证。

### WP5：自动门、提交和推送

- 运行第 10 节全部自动门；
- 记录 V0.2 继承事项为 passed/unverified，不伪造人工结果；
- 将版本升至 V0.31；
- 使用第 11 节白名单建立提交、fetch、验证祖先关系并 push main；
- 不 tag、不 release、不上传发布包。

## 9. V0.2 继承任务

这些任务已纳入 V0.3，不能被归档动作消除。

### V0.32 核心人工门

| ID | 状态 | 任务 | 通过证据 |
| --- | --- | --- | --- |
| CARRY-01 | unverified | 启动、托盘、单实例 | 一个 App、一个可见 Agent，重复启动只激活已有窗口 |
| CARRY-02 | unverified | 六页与只读边界 | 页面可达；模拟可改；本机结构修改和自由命令被拒绝 |
| CARRY-03 | unverified | 双语、相反主题、工具状态 | 中英文和相反主题可用；工具状态真实 |
| CARRY-04 | unverified | App 内 DiskSpd | 仅 D: 登记目录；计划、执行、历史、导出完整 |
| CARRY-05 | unverified | 取消与恢复 | Worker/工具退出；历史保留；下一计划可启动 |
| CARRY-06 | unverified | RoboCopy/FullHash | 生成、复制、哈希、比较/导出正确 |
| CARRY-07 | unverified | 跨窗口监控 | 关闭 App 后 Agent 继续；重开恢复同一会话 |
| CARRY-08 | unverified | R3/UAC D: Flush | 目标绑定、确认、Broker、审计；无结构修改 |
| CARRY-09 | unverified | 托盘完全退出 | App/Agent/Worker/Broker/受监督工具全部退出 |
| CARRY-10 | unverified | 数据位置往返 | 标准→便携→标准，哈希/quick_check/来源保留 |
| CARRY-11 | unverified | Worker 强制终止恢复 | Agent 状态收敛，Job Object 回收子进程 |
| CARRY-12 | passed | 实际 publish 布局 | 2026-08-10 staging：四进程路径正确、无重复 EXE、无禁止内容 |

人工目录固定为 `D:\WinPool-V03-Manual-Test`；不选择 C:、E:、网络盘、磁盘根或源码目录。

### V0.3 后续债务池

| ID | 状态 | 任务 |
| --- | --- | --- |
| DEBT-01 | deferred_by_user | 205 个兼容 ID 的实现/自动/人工证据收敛 |
| DEBT-02 | deferred_by_user | 原生与脚本 inventory 的字段、身份和失败降级对照 |
| DEBT-03 | deferred_by_user | 键盘、屏幕阅读器、高对比度、DPI/窄窗完整矩阵 |
| DEBT-04 | deferred_by_user | 用户批准的双语和视觉回归矩阵 |
| DEBT-05 | deferred_by_user | 多小时监控、重连、背压、句柄/内存证据 |
| DEBT-06 | deferred_by_user | 大目录、容量边界、Dite/fio/RoboCopy 适配证据 |
| DEBT-07 | deferred_by_user | R3 失败、取消、崩溃、审计和恢复矩阵 |
| DEBT-08 | deferred_by_user | 大数据量位置迁移和失败回退 |
| DEBT-09 | deferred_by_user | 隐私/采集策略的用户决策 |
| DEBT-10 | deferred_by_user | 推测算法验证或保持推测标记 |
| DEBT-11 | deferred_by_user | 长测试实时曲线与事件断线补偿验收 |
| DEBT-12 | deferred_by_user | 发布工程的完整可复现性闭环 |

## 10. V0.31 自动门

### 文档与结构

- 所有 Markdown 相对链接有效；
- `Plan` 只保留一个活动计划；
- 根目录无 `Ref`、`Arting`、`Icon`、`Musume`、`Docs/docs`；
- 旧文档在项目根 Old 可恢复，源文件已移走；
- `local-assets` 被忽略且 `git ls-files local-assets` 无输出；
- `git diff --check` 通过。

### .NET 与安全

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

全部测试必须通过；构建零错误；警告必须解释；不存在已知漏洞或有用户批准例外。测试和构建
不得引入真实存储结构修改。

### 原生 staging

- 运行 V0.31 publish/staging 命令；
- 检查四个 EXE 的实际相对路径和无重复 EXE；
- 检查 staging 无 `.ps1`、local-assets、数据库、测试结果、外部工具；
- 检查 App 运行时查找的 Agent 路径与 staging 一致。

人工 UAC、托盘、文件夹选择器和 D: 工具运行不属于 V0.31 自动门；它们仍是 V0.32 CARRY。

V0.31 的 staging 命令是：

```powershell
.\build\Publish-Staged.ps1 `
  -OutputPath ..\..\Rubbish\YYYYMMDD_winpool_v031_staging\Program\WinPool `
  -Configuration Release
```

`OutputPath` 必须是新的空路径；脚本拒绝覆盖已有目录。2026-08-10 的实际验证位于
`Rubbish\20260810_winpool_v031_staging_retry2\Program\WinPool`，四个相对路径、无重复
子进程 EXE 和禁止内容检查均为 `passed`。此前两个失败的尝试也按不可删除规则保留在
`Rubbish`，不属于提交或发布内容。

## 11. V0.31 暂存与远端规则

自动提交只允许：

```text
.gitignore
AGENTS.md
DEVELOP.md
README.md
README_CN.md
Plan/**
global.json
WinPool.slnx
Directory.Build.props
src/**
workers/**
tests/**
build/**
```

禁止暂存：

```text
local-assets/**
bin/** obj/** TestResults/** publish/** artifacts/**
*.db *.db-wal *.db-shm *.log *.trx
发布 ZIP/EXE/MSIX、外部工具、Old、Rubbish
```

执行顺序：显式 `git add` 白名单 → `git diff --cached --name-status` → 提交 → `git fetch origin`
→ 确认 `origin/main` 是待推送 HEAD 的祖先 → `git push origin main`。若任何检查不成立，停止，
不 force push。

## 12. 持续状态台账

| WP | 状态 | 证据 | 下一动作 |
| --- | --- | --- | --- |
| WP0 基线与规则统一 | completed | `main`/`origin/main` 基线 `ec8b34a`，远端已 fetch，迁移边界已记录 | WP5 暂存审查 |
| WP1 文档与参考重构 | completed | 唯一活动 Plan、Reference、根 Old 归档与 V0.2 继承项均已核对 | WP5 暂存审查 |
| WP2 目录与本地资产 | completed | 153 个本地资产、118,909,221 字节已迁入 ignored `local-assets`；根旧入口已移走 | WP5 暂存审查 |
| WP3 版本源 | completed | `Directory.Build.props` + ProductInformation；staging EXE 均为 `0.3.1.0` / `V0.31` | WP5 暂存审查 |
| WP4 staging | completed | `build/Publish-Staged.ps1` 的真实 staging 验证通过 | WP5 暂存审查 |
| WP5 自动门与推送 | completed | 455/455 test、Release build 0/0、漏洞/链接/结构检查通过；`6cf68e3` 已于 2026-08-10 push 至 `origin/main` | 等待 V0.32 用户确认 |

每轮开始读取父/本地 AGENTS、活动 Plan、Git 状态和 `next_action`；每轮结束更新顶部状态、
本表、门禁状态、证据路径与下一动作。任何新范围、人工验收或真实 I/O 都需用户明确批准。

## 13. V0.31 完成定义

V0.31 完成且可自动推送，当且仅当：

- WP0～WP4 完成；
- 第 10 节全部自动门通过；
- V0.2 未完成事项已进入第 9 节且状态真实；
- 暂存内容完全符合第 11 节；
- 版本源、代码和活动文档一致显示 V0.31 / V0.3；
- `remote_gate` 在 push 后记录为 passed；
- 未创建 tag、release 或二进制发布包。

V0.32 需要用户人工确认，不能由 Agent 在 V0.31 自动门后自行声明。
