# WinPool V0.41 启动、欢迎、监控与基础交互计划

## 0. 状态、授权与基线

- **计划状态**：implementation complete
- **创建日期**：2026-08-14
- **基线提交**：`daf241ac1f523d262f7dbbbf68505ebdd8b604ee`
- **当前工作分支**：`refactor/v039-architecture-hardening`
- **批准后建议实施分支**：`feature/v041-interaction-polish`
- **当前产品版本**：V0.41
- **目标产品版本**：V0.41
- **阶段性质**：V0.4 产品线的第一轮启动体验、视觉资源、监控交互和设置一致性完善
- **自动基线**：530 passed、0 failed、0 skipped；Release build 0 warning、0 error

用户已认可首版计划的其余内容，并授权检查现状、修订本计划以及直接完成安装路线文档。
用户随后明确授权 V0.41 在本开发机上完整重置并重新生成现有本地状态，不要求 V0.41 读取、
迁移或兼容 V0.4 的偏好与数据库。该授权不等于立即清理：实施时仍须先停止全部 WinPool 进程，
核对准确路径，并把旧数据移动到项目根 Rubbish 的可恢复位置。
用户已于 2026-08-14 最终确认欢迎页默认/最小尺寸和监控事件模态页面；这两个参数在
V0.41 中不再作为待定 UI 决策，V0.41 计划内容全部确认。
用户随后要求执行本计划；实施已于 2026-08-14 开始。Git commit、push、tag、GitHub Release、
二进制上传和本机部署仍需单独授权。

### 已最终确认的 UI 参数（2026-08-14）

| 项目 | 确认参数 | 实现边界 |
| --- | --- | --- |
| 欢迎窗口 | 默认打开 `960×640`；最小 `720×480`；可由用户在本次打开中手动拉伸 | 不保存用户拉伸后的尺寸；下次打开仍从默认尺寸开始。 |
| 监控事件 | 由主窗口内、相对主窗口居中的模态 `ContentDialog` 展示 | 不是独立顶级窗口；V0.41 只提供事件列表、详情、空态和关闭，不引入事件中心、筛选或确认流程。 |

`docs/Reference/轻松交流.txt` 是本计划中 QQ 群入口的需求来源。该文件当前为用户提供的
未跟踪参考文件；计划编制阶段只读取，不修改、不移动、不提交。实施时只使用其中固定的
HTTPS QQ 群链接，不执行参考文件中的移动端示例代码。

## 1. 目标

V0.41 在不开放真实存储结构修改的前提下完成以下结果：

1. 冷启动时欢迎内容先于工作区扫描和非关键 Agent 恢复呈现，Agent 启动中不会被误报为失败。
2. 使用 C 系列图标和现有 00～07 看板娘资源，形成可访问、可缩放且不会拖慢首帧的欢迎体验。
3. 监控改为一个持久化的“持续监控”开关，默认 5 Hz，关闭主窗口后由 Agent 继续运行。
4. 托盘菜单、语言、主题色、外部工具状态、缓存扫描和启动设置具有一致且可解释的状态来源。
5. 管理、编辑和设置页完成用户指定的基础交互与布局调整。
6. 将本地持久状态收敛为一个偏好文件和一个 Agent 数据库，其他运行辅助文件具有固定目录、
   生命周期和清理规则。
7. 所有自动和原生结果如实记录；未执行的重启、托盘、长时监控、设备和视觉用例保持
   `unverified`。

本计划不是完整的 V0.4 视觉重做，也不提前实现 V0.5 真实编辑、V0.6 完整测试/监控、
V0.8～V0.9 打包或 V1.0 后 Microsoft Store 上架工作。

## 2. 现状检查与 25 项需求评价

| # | 需求 | 当前证据与判断 | V0.41 处理结论 |
| --- | --- | --- | --- |
| 1 | 首次启动慢、托盘连接失败，第二次正常 | App 激活后并发连接 Agent；Agent 在启动消息循环和发布 IPC 端点前同步完成 SQLite 初始化、测试恢复、系统状态恢复和历史读取。12 秒超时会直接发布“失败”，因此冷启动中的“尚未就绪”被当成终态失败。 | 修正启动阶段和错误分类；托盘消息循环尽早可见，Agent 以 Starting/Recovering/Ready/Failed 表达状态，App 等待或重连，只有终态失败才告警。 |
| 2 | “轻松交流”按钮和 QQ 群链接 | 设置页已有“轻松交流”，但仅显示“QQ 群（暂未开放）”。 | 改为固定“加入 QQ 群”按钮，打开参考文件中的 HTTPS 链接；显示群名和群号 732019606，失败时给出可复制信息。 |
| 3 | C 系列作为 WinPool 图标 | `assets/icons/C_16.png`～`C_1024.png` 已齐全；运行时仍使用旧 `AppIcon.ico`。 | 以 C 系列生成多尺寸 ICO，并统一 App 标题栏、App/Agent/Worker/Broker 可执行文件和托盘图标来源。保留 C 系列 PNG 为源。 |
| 4 | 欢迎页面放大 | 当前 ContentDialog 内容宽度固定 480，图形 112×112。 | 欢迎窗口固定默认打开尺寸 960×640、最小尺寸 720×480；用户可手动拉伸。本次手动尺寸不持久化，下次仍按默认尺寸打开。 |
| 5 | 欢迎优先且快速加载 | 欢迎已早于 `WorkspaceViewModel.InitializeAsync`，但仍等待 XamlRoot 和偏好加载，且完整主窗口先激活；Agent 冷启动竞争仍会影响后续可用性。 | 使用 MainWindow 内建轻量欢迎覆盖层作为首个可见内容；扫描、工具检测、恢复和页面构建不得阻塞欢迎首帧。 |
| 6 | 左侧看板娘；00 默认，其他概率出现 | `assets/welcome/00-随机切换.png`～`07-绷不住了.png` 已存在且为 ARGB PNG，目前未接线。 | 每次打开 WelcomeView 都重新随机选择：00 权重 70%，01～07 平分剩余 30%。关闭后从托盘或设置再次打开也必须重新抽取并刷新图片。 |
| 7 | 移除欢迎页现有图片 | 当前所谓图片实际是 Accent Border 内的 FontIcon。 | 删除该图标块；看板娘成为唯一主视觉。 |
| 8 | 默认监控 5 Hz | App 默认 1 Hz，监控页回退到 1 Hz，托盘启动监控也硬编码 1 秒。 | 建立唯一默认值 5 Hz，App、Agent、托盘和偏好默认值统一；允许用户从现有频率列表调整并持久化。 |
| 9 | 一个“持续监控”开关替代启停逻辑 | 当前进入 Monitor 页自动启动；离页在未勾后台时停止；背景开关仅存在内存中，已有偏好字段未接线。 | 移除页面导航副作用和开始/停止按钮。“持续监控”勾选即启动并持久化，取消即停止；默认关闭；主窗口关闭不停止；下次启动按偏好自动恢复。 |
| 10 | 监控状态浮动在曲线左上角；事件按需展开 | 当前状态和采样诊断位于下方独立 Expander。 | 状态移到曲线左上角非遮挡浮层；曲线下方仅放一个“事件”按钮，点击后打开小型事件页面显示具体事件。V0.41 不实现更复杂的事件中心。 |
| 11 | Ctrl+1 小提示自动出现 | 六个 `KeyboardAccelerator` 全部挂在 `RootGrid`，WinUI 会为宿主产生自动快捷键提示；该结构与观察到的 Ctrl+1 临时提示一致。 | 保留 Ctrl+1～Ctrl+6 导航，但隐藏宿主自动提示或把快捷键归属到各导航项；增加“静置不出现提示”的原生验收。 |
| 12 | 语言切换同步托盘 | 托盘菜单和状态文本全部硬编码英文，Agent 没有语言更新入口。 | Agent 启动读取当前语言；App 改语言后通过封闭状态更新通知 Agent，托盘原位重建文字，不重启进程。 |
| 13 | 更换语言是否需要重启；当前卡顿 | 当前不需要重启。卡顿的重要直接原因是语言处理器重建工具行后再次异步全量检测所有外部工具。 | 明确定义“语言切换无需重启”；只重绘文本和格式，不触发硬件扫描、工具检测或页面导航，目标 500 ms 内完成，托盘 1 秒内同步。 |
| 14 | 预设主题色无效 | 代码修改 Application 级颜色/Brush，但现有控件混用 StaticResource、ThemeResource 和控件主题字典；现有原生证据未验证预设色，用户观察表明刷新边界不完整。 | 建立单一 Accent palette 应用入口，更新活动主题字典和已生成控件使用的同一 Brush；逐色验证导航选择、按钮、开关、焦点框、拓扑选择及新建控件。 |
| 15 | 外部工具路径和状态应记录 | 自定义路径已写入独立 `tool-paths.json`，检测结果又写入 SQLite `external_tools`；设置页进入和语言切换还会全量 Detect，形成双来源和隐式刷新。 | 新数据根中自定义路径只进统一偏好；版本、哈希、可用状态和检测时间只进 SQLite。UI 先显示缓存，仅在显式检测、路径改变、安装完成或执行前验证时重检。旧 `tool-paths.json` 不迁移。 |
| 16 | 启动先显示上次扫描再刷新 | Agent 已保存并可加载本机扫描，但 MainPage Loaded 直接 await 新扫描后才执行最终表格构建，cache-first 行为未被锁定。 | 初始化先投影上次成功快照并标注时间/“正在刷新”；新扫描在后台运行，成功后原位替换，失败则保留缓存。无缓存时才显示空态。 |
| 17 | 托盘菜单按指定顺序和语义 | 当前包含 6 个页面入口、状态行、开始/停止监控、取消测试和不可用暂停项，且只有英文。测试层没有运行中暂停合同。 | 菜单精确收敛为：欢迎、显示主界面、暂停/恢复测试、暂停/恢复监控、退出 WinPool。无测试时测试项禁用；取消测试和详细设置只留主界面。 |
| 18 | 三种安装方式路线 | 当前是 unpackaged portable。 | 安装路线已直接整合进 Product、Development 和 README：当前仅便携式；V0.8～V0.9 开发并验证 MSIX；V1.0 正式版完成后再申请 Microsoft Store。V0.41 不创建安装包，也不保留独立安装文档。 |
| 19 | 拓扑右键菜单跟随光标 | 当前按节点边界计算 Left/Right placement，并 `ShowAt(element)`。 | 传递右键事件位置，使用相对根视图的指针坐标显示，同时做屏幕边缘钳制。 |
| 20 | 删除重置模拟数据和仅模拟警告 | Edit 页顶部正好包含这两个控件。 | 从页面移除两者；不因此开放真实修改。真实系统上的不可用操作继续由按钮禁用、工具提示和既有授权边界表达。 |
| 21 | 不用下拉选择磁盘；列出所有可分区磁盘 | 当前 `DiskSelector` 下拉列出全部 `OsDisks`，无独立 eligibility policy。 | 建立纯投影的磁盘资格规则并一次显示所有合格本地块磁盘；RAW/GPT/MBR、容量有效的 OS disk 可展示，网络/伪组/无效对象不展示。系统/启动盘仍可见，但危险操作继续禁用。 |
| 22 | 磁盘与分区左右布局 | 当前磁盘下拉、分区条和按钮全部纵向堆叠。 | 左侧为可滚动磁盘行及其分区条，右侧为随当前磁盘/分区选择变化的固定按钮组；窄窗口退化为上下布局。 |
| 23 | “登录启动”改“开机启动”，登录 Windows 自动启动 WinPool | 当前 HKCU Run 只启动 `WinPool.Agent.exe --windows-login`，不是主界面。 | 文案改“开机启动”；启用后在用户登录 Windows 时启动 `WinPool.App.exe`，再由 App 协调 Agent。仍为当前用户、默认关闭，不创建服务。 |
| 24 | 数据路径与下拉框同一行 | 当前二者在 StackPanel 中分两行。 | 改为同一行 Grid；路径占剩余宽度、单行省略，Tooltip/自动化名称提供完整路径。 |
| 25 | 关于标题占大块空位；设置子块布局不一致 | About 卡使用固定 220 左列且左列只有“关于”，其他设置卡也没有统一列宽。 | 完全删除卡片内部的“关于”标题；所有设置子块共用一致的两列布局、标签列宽、间距和窄窗口折叠规则，About 只保留实际字段和值。 |

## 3. 已确认的产品语义

### 3.1 欢迎内容

- 正常启动仍显示欢迎内容；托盘“欢迎”和设置“打开欢迎内容”复用同一个 WelcomeView。
- 欢迎首帧不得等待 Agent、存储扫描、模拟文档、外部工具检测或监控快照。
- WelcomeView 使用固定默认打开尺寸 960×640，并允许用户手动拉伸；最小尺寸为 720×480。
  手动拉伸只影响本次打开，不持久化窗口尺寸，再次打开恢复默认尺寸。
- 看板娘使用 `Uniform` 缩放、保持比例，不拉伸；在 100%～200% DPI 和最小支持窗口下不得
  遮挡标题、正文和确认按钮。
- 每次创建或重新打开 WelcomeView 时都执行一次独立随机抽取；启动自动打开、托盘打开和设置
  打开行为一致。关闭 WelcomeView 即丢弃本次选择，下次打开不得复用图片缓存键。
- 00 的权重为 70%，01～07 平分剩余 30%；图片解码缓存可以复用，但随机选择结果不能复用。
- 当前 FontIcon 图片块完全退役，不与看板娘叠加。

### 3.2 持续监控

- `持续监控 = false`：Agent 不维持用户常驻监控会话。
- 用户勾选：保存偏好后请求 Agent 以当前采样频率启动；成功后显示运行中，失败则恢复开关并
  显示原因，不能显示为已开启。
- `持续监控 = true`：关闭主窗口只断开 UI 订阅，不停止 Agent 会话；再次打开直接接回现有
  会话。Agent 或 WinPool 整体重启后按持久偏好自动恢复。
- 用户取消勾选：请求停止、flush 持久化并保存关闭偏好；未知结果必须重新查询 Agent 对账。
- 默认采样率为 5 Hz。采样率变化持久化；运行中变更通过一次受控重配完成，不产生两个会话。
- 页面切入、切出、最小化和关闭窗口均不得隐式改变持续监控设置。
- 托盘“暂停/恢复监控”操作同一个持久开关，因此托盘和 UI 必须立即互相同步。

### 3.3 监控事件入口

- 曲线左上角浮层只显示监控状态、实际采样频率、最近成功时间和必要的丢样/失败提示。
- 曲线下方只保留一个“事件”按钮；没有事件时仍可点击并显示空态，不用禁用按钮制造歧义。
- 点击后打开一个相对主窗口居中的模态 `ContentDialog` 小型事件页面，不增加独立顶级窗口、
  复杂筛选、事件确认工作流或完整事件中心。
- 小页面按时间倒序显示时间、级别、来源、摘要和详情；支持关闭、键盘导航和屏幕阅读器。
- V0.41 只展示当前已有的持久化存储健康事件和监控诊断投影，更详细的聚合、搜索、导出和
  跨会话事件中心留到后续阶段。

### 3.4 测试暂停/恢复

现有工具不提供统一、可靠的进程内立即暂停接口，V0.41 不使用未文档化线程冻结或任意
SuspendThread 来伪造暂停。统一语义如下：

1. “暂停测试”是一个类型化、可审计的 pause request。
2. Agent 不再分派新步骤或新 CopyBatch；正在执行的原子外部工具步骤允许完成，状态先显示
   “正在暂停”，到安全边界后进入“已暂停”。
3. “恢复测试”从同一不可变计划和下一待执行边界继续；不能新建 run、跳过验证或重算授权。
4. 单一长步骤不能瞬间冻结，但暂停请求保持有效；菜单仍可用并显示“正在暂停”，不能谎报
   “已暂停”。
5. 无活动测试时该菜单项置灰。取消、停止、详情和配置只存在于主界面。
6. 进程崩溃、Agent 重启和托盘退出仍遵守现有 interrupted/cancel/shutdown 规则；暂停不扩大
   测试目标和外部工具权限。

### 3.5 安装方式路线

- 当前用法、三种交付方式和后续验收边界直接属于 Product、Development 和 README，不建立
  独立安装文档。
- **当前至 V0.7**：便携式是唯一实现和发布方式。
- **V0.8～V0.9**：增加并验证 MSIX，解决签名、升级、卸载、数据位置和启动注册差异；便携式
  继续保留。
- **V1.0 完成后**：在正式版证据、身份、隐私、签名和商店材料具备后再申请 Microsoft Store；
  Microsoft Store 不作为 V1.0 完成前置条件。
- 三种方式共享产品功能和安全边界，但允许使用各自平台认可的数据目录与启动注册机制。

### 3.6 本地状态归属

#### 当前审计结果

正常 Agent 模式当前可能产生以下本地内容；它们不是都会在第一次启动的同一时刻创建：

| 当前内容 | 创建时机 | 当前用途 | 当前问题 |
| --- | --- | --- | --- |
| `%LocalAppData%\WinPool\winpool.db` | Agent 第一次初始化 | schema、扫描缓存、模拟文档、监控、测试、工具检测、会话和恢复 | 正常运行的主要数据源，方向正确；SQLite 可能短暂产生 `-wal`/`-shm` 辅助文件。 |
| `%LocalAppData%\WinPool\agent-endpoint.json` | Agent 每次运行 | App 寻找当前 Agent IPC 端点 | 临时运行文件与持久数据同层，容易被误认为用户数据。 |
| 活动数据根的 `settings.json` | 第一次保存偏好 | 主题、语言、欢迎、隐私等 UI 偏好 | 是当前实际偏好源，但与未使用的 SQLite `preferences` 全局记录重叠。 |
| 活动数据根的 `tool-paths.json` | 第一次配置工具路径 | 外部工具自定义路径 | 与 SQLite `external_tools` 形成双来源。 |
| 活动数据根的 `workspace.json`、`machine.json` | 无 Agent 开发回退路径被使用时 | 工作区和本机扫描 JSON 回退 | 正常产品不应生成；与数据库职责重叠。 |
| 活动数据根的 `last-crash.txt`、`monitor-debug.log` | 崩溃或监控异常 | 早期故障诊断 | 无统一目录、格式、大小和保留策略。 |
| 活动数据根的 `Monitoring\*.csv` | 使用当前 App 侧监控时 | 自动会话 CSV | 与 SQLite 监控持久化重复；自动生成会不断堆积。 |
| 活动数据根的 `tool-downloads`、`tools` | 安装受管外部工具时 | 下载暂存和受管工具文件 | 属于载荷而非偏好/数据库，目录和清理责任需要明确。 |
| 标准根的 `storage-location.json` | 用户切换数据位置时 | 在打开活动数据根前选择 Standard/Portable | 是不可放进活动根自身的启动指针，但应是唯一持久化例外。 |
| `HKCU\...\Run` 的 `WinPool.Agent` | 用户启用登录启动时 | Windows 登录启动 | 是系统集成投影，不应成为偏好权威；当前还指向 Agent 而非 App。 |

第一次正常打开 WinPool 时，确定会建立数据库并在 Agent 运行期间发布 endpoint；首次扫描、
会话恢复和关闭主窗口会继续向数据库写入缓存或工作区状态。`settings.json` 只有在首次保存偏好
时才建立；日志、监控 CSV、工具配置、受管工具目录、数据位置指针和启动注册均由对应事件
条件触发，不是无条件首次启动产物。

#### V0.41 目标模型

1. **唯一偏好权威：`settings.json`。** 保存主题、强调色、语言、欢迎开关、硬件 ID 显示、
   MSR 默认值、持续监控、采样率、开机启动期望值和外部工具自定义路径。使用格式版本、原子
   替换和损坏回退；App 与 Agent 通过同一适配器/封闭 IPC 使用它。V0.41 不读取旧 V0.4 文件。
2. **唯一运行数据权威：`winpool.db`。** 保存库存快照、工作区状态、模拟文档、工具检测缓存、
   监控会话/采样/事件、测试计划/历史/证据、审计、恢复和进程会话。偏好不得复制进数据库。
3. 不迁移 SQLite 现有 `preferences` 表、全局偏好、工作区会话或测试预设。新数据库直接采用
   清晰的专用表：工作区状态进入 `workspace_state`，测试预设进入测试领域表，用户偏好不进入
   SQLite。持久化语义改变时使用新的明确 schema 版本，不在 schema 12 下静默换义。
4. 旧 `tool-paths.json`、`workspace.json` 和 `machine.json` 均不导入新状态。后两者只允许隔离
   测试显式注入的开发回退，不由正式构建生成。
5. `storage-location.json` 是唯一持久化启动例外，固定在标准根且只含模式和格式版本；它不保存
   其他偏好。Windows Run 项只是 `StartWithWindows` 的系统投影，启动时检查并修复漂移。
6. endpoint 放入固定 `Runtime` 子目录，属于可重建临时文件，正常退出移除，陈旧端点启动时
   回收。诊断文件统一进入 `Diagnostics`，采用结构化格式、容量上限和滚动保留。
7. 停止自动创建 `Monitoring\*.csv`；监控以 SQLite 为权威，只有用户显式导出时才向用户选择
   的目标写 CSV。受管工具载荷放在固定 `ManagedTools`，下载暂存放在可回收 `Staging`。
8. V0.41 建立干净根以后，同版本数据位置切换仍复制并验证 `settings.json`、`winpool.db`、诊断
   和受管载荷；不迁移 Runtime，
   不把标准根的启动指针复制进活动根。设置页提供当前活动根和各类别说明。
9. 实施重置顺序固定为：停止 App/Agent/Worker/Broker，确认 `%LocalAppData%\WinPool` 和程序旁
   `Data` 的实际状态，把存在的旧根移动到项目根
   `Rubbish\YYYYMMDD_v041_local_state_reset\...`，确认源不再活动且备份存在，然后由 V0.41
   创建全新偏好和数据库。不得在进程运行中修改 SQLite，不直接删除旧根。

## 4. 架构约束

- 保留 App、Agent、TestWorker、ElevatedBroker 四进程模型。
- 保留 Agent 的 SQLite 单写者所有权和 typed named-pipe IPC；新增消息必须封闭、有鉴权、有
  超时/未知结果对账，不增加自由命令。V0.41 可定义新的干净 schema，不实现 schema 12 数据
  迁移；发现旧数据库时明确拒绝打开，由本计划授权的开发机重置流程处理。
- V0.41 不实现真实磁盘、分区、卷、Storage Pool、Storage Tier 或 Virtual Disk mutation。
- Edit 页移除“仅模拟”提示不改变执行能力；真实操作仍不允许。
- 欢迎页不能通过预创建所有页面、扫描硬件或加载大图原尺寸来换取表面上的“先显示”。资源应
  使用适合显示尺寸的派生文件，源文件保留在 `assets`。
- 不新增通用事件总线、插件系统、主题框架或第二套状态数据库。偏好、Agent 状态、工具缓存和
  inventory cache 各自只有一个权威来源。
- 不为解决托盘语言而重启 Agent，不为解决主题色而重建整个 MainWindow。
- 不在设置页进入、语言切换或普通页面导航时执行隐式外部工具全量检测。
- 完整文件若确认退役，不直接删除；按项目规则移动到根级 Rubbish 并保留相对路径。

## 5. 工作包与实施顺序

### WP1：特征基线、启动计时和状态合同

1. 为冷启动建立可观测时间点：App process start、MainWindow first paint、Welcome visible、Agent
   launch requested、tray message loop ready、endpoint published、recovery completed、App connected。
2. 为 Agent 增加 Starting、Recovering、Ready、Failed 的内部状态，不把 endpoint 暂缺、进程
   存活且恢复中解释为失败。
3. 为连接器增加单次启动协调和持续重连；并发调用共享同一连接任务，不重复拉起 Agent。
4. 锁定真实失败：Agent 进程退出、端点身份无效、ACL/协议不兼容和恢复终态失败仍必须告警。
5. 先写自动状态转换和 timeout 测试，再修改启动顺序。

### WP2：欢迎首帧、缓存优先和冷启动修复

1. 将托盘消息循环提前到 Agent 非关键恢复之前；恢复期间托盘显示“WinPool 正在启动”。
2. 恢复未完成时只允许状态查询、主界面激活和安全偏好读取；测试、监控和其他工作请求明确
   返回 Recovering，不在恢复完成前并发执行。
3. 将 WelcomeView 作为 MainWindow 首个轻量可见层；偏好采用快速本地读取，失败时使用系统
   主题和系统语言，不等待 Agent。
4. 工作区初始化拆为 cache projection 与 background refresh。缓存投影完成即可导航；新扫描
   不占用 UI 线程，也不阻塞表格和拓扑首屏。
5. 扫描成功原位替换并保留选择；失败保留上次结果和时间，显示非阻塞错误。

### WP3：欢迎视觉、C 图标和社区入口

1. 为 C_16/32/48 或 64/128/256/512 建立确定性的多尺寸 ICO 生成/验证流程；不得只把 256 PNG
   重命名为 ICO。
2. 统一 AppWindow、可执行文件和 NotifyIcon 使用的 C 系列图标，验证 16/20/24/32/48/256
   像素下没有透明边缘、裁切或错误底色。
3. 接线 00～07 看板娘为 app content，生成合适尺寸派生资源并保留 ARGB；不得覆盖源 PNG。
4. 实现双列 WelcomeView：默认 960×640、最小 720×480、允许手动拉伸；左侧看板娘，右侧
   标题、说明和确认按钮，窄尺寸可上下排列，本次拉伸尺寸不持久化。
5. 每次打开前使用可注入随机源重新抽取看板娘；图片解码缓存与选择结果分离，关闭即清除选择。
6. 移除旧 FontIcon 图片块。
7. 设置页“轻松交流”改为按钮，使用固定 HTTPS 地址和 `UseShellExecute=true` 打开；增加中英文
   文案、键盘可达性和链接失败提示。

### WP4：状态归属、偏好、语言、主题色、工具缓存与开机启动

1. 建立状态清单和所有权测试：`settings.json` 是唯一偏好权威，`winpool.db` 是唯一运行数据
   权威；Runtime、Diagnostics、ManagedTools、Staging 和启动指针只有明确辅助职责。
2. 抽取 App/Agent 共享、原子、有明确当前格式版本的偏好存储适配器，避免页面各自读写；工具
   自定义路径直接写入新偏好，不读取或写入旧 `tool-paths.json`。
3. 定义新的干净 SQLite schema，使工作区会话和测试预设使用各自专用表；不实现 schema 12
   或旧 `preferences` 表迁移。旧库拒绝打开，不能被新代码静默覆盖。
4. 正式构建不再生成 `workspace.json`、`machine.json` 或自动监控 CSV；统一 Runtime、Diagnostics、
   ManagedTools 和 Staging 目录及保留策略。同版本数据位置切换按类别复制，Runtime 不迁移。
5. 增加并持久化 `ContinuousMonitoringEnabled` 和 `MonitoringSampleRateHz`；旧偏好缺字段时
   使用 false 和 5 Hz；由于采用干净偏好，不继承 V0.4 的主题、语言、隐私或 MSR 选择。
6. Agent 启动读取语言和持续监控偏好；App 修改后发送封闭更新，托盘在原线程重建菜单文本。
7. 语言切换只更新本地化投影；移除 `RefreshExternalToolsAsync` 隐式调用，不重启 App/Agent。
8. 将 Accent 资源更新集中到一个 palette service；修复 Application/ThemeDictionary 资源作用域，
   保证现有与新建控件都使用选择值；System 仍监听 Windows accent 变化，预设色不随系统漂移。
9. Settings 先读取 Agent 持久化的 `external_tools` 状态；增加显式缓存查询合同和“上次检测”
   显示。手动检测、配置、安装和执行前验证仍更新 SQLite。
10. 将启动注册从 Agent 改为主 App，显示“开机启动 / 登录 Windows 时自动启动 WinPool”，
   保持当前用户、默认关闭和可逆关闭；重置时移除旧 `WinPool.Agent` Run 项，避免双启动。
11. 在开始新版本原生验证前执行一次获授权的本机状态重置；记录停止的进程、源路径、Rubbish
   目标、重建后的文件清单和新 schema，不把旧数据读取成功作为验收条件。

### WP5：持续监控页面和 Agent 恢复

1. 建立共享 `MonitoringDefaults`，默认 5 Hz；移除 App 与托盘硬编码的 1 Hz。
2. 删除 Monitor 页进入自动 Start、离开自动 Stop、最小化降级和 BackgroundEnabled 内存分支。
3. 用一个“持续监控”CheckBox 取代后台勾选框和 Start/Stop 按钮；保留采样频率、自动颜色和
   导出功能。
4. Agent 根据持久偏好在启动恢复完成后建立至多一个监控会话；已有会话时 App 只附着。
5. 频率变化执行 stop/reconfigure/start 的单一序列，并通过 snapshot 对账终态。
6. 曲线区使用叠层布局；左上角显示 stopped/starting/running/pausing/failed、实际 Hz、最近成功
   时间和丢样警告。浮层不得拦截曲线选择或键盘焦点。
7. 删除下方采样/事件 Expander，曲线下方放一个“事件”按钮；点击打开小型模态事件页，按时间
   倒序展示当前已有事件及空态。V0.41 不实现筛选、搜索、事件确认或独立事件中心。

### WP6：托盘菜单和安全暂停/恢复

1. 托盘菜单按用户顺序精确重建；允许视觉分隔线，但不增加其他命令或状态行。
2. “欢迎”打开同一 WelcomeView；“显示主界面”只激活/恢复现有窗口，不强制切换当前页面。
3. 增加 typed PauseTest/ResumeTest 请求、状态和事件；TestWorker/Agent 在安全步骤或 CopyBatch
   边界停止继续分派。
4. 测试菜单根据 none/running/pausing/paused/terminal 显示和启用；无测试时禁用。
5. 监控菜单操作持久的持续监控偏好，并与主界面 CheckBox 双向同步。
6. “退出 WinPool”继续执行现有有界 shutdown、flush、恢复系统状态和子进程树清理；暂停测试
   仍视为活动测试，需要既有确认。

### WP7：管理、编辑和设置页基础交互

1. 拓扑 RightTapped 传递最新指针坐标；MenuFlyout 相对根视图按坐标显示并限制在工作区内。
2. 隐藏 RootGrid 的自动 KeyboardAccelerator 提示，保留 Ctrl+1～Ctrl+6 和屏幕阅读器名称。
3. Edit 页移除 ResetSimulationButton、SimulationOnlyInfoBar 及专属事件处理器；清理无消费者
   本地化键和测试时先做引用审计。
4. 提取 `PartitionableDiskPolicy` 纯投影并测试 RAW/GPT/MBR、网络/无效对象、启动/系统保护。
5. 磁盘与分区改为左列表右按钮组；每个左侧磁盘行同时显示编号、名称、大小、状态、分区条和
   未分配空间；选择行/分区只更新右侧状态，不重建整个页面。
6. 数据位置 ComboBox 与路径改为同一行；窄宽度下允许受控响应式换行，但 1440×900 默认
   必须同一行。
7. 提取所有设置子块共用的两列布局和窄窗口折叠规则；完全删除 About 卡片内部“关于”标题，
   只保留实际字段和值；加入 QQ 群按钮纳入同一两列信息区。

### WP8：文档、全量验证和计划冻结

1. 同步 README、Product、Development、Quality、CHANGELOG 和中英文副本中的实际结果。
2. 自动门全部通过后执行目标原生矩阵；没有重启证据时不得把真正 cold boot 写成 passed。
3. 用户确认 V0.41 完成后，才把产品版本从 V0.4 改为 V0.41、冻结本计划到
   `docs/Archive/V0.41/` 并更新归档索引。
4. Git commit、push、tag、Release、二进制上传和部署分别遵守当时用户授权；计划批准本身不
   自动授权发布动作。

## 6. 自动验证

### 6.1 必须新增或调整的自动测试

- 并发 Connect 只启动一个 Agent；Starting/Recovering 不产生失败通知；终态失败产生一次通知。
- Agent 恢复期间拒绝新工作但允许状态查询，恢复完成后转换 Ready。
- cached inventory 先于 refresh 投影；刷新失败保留缓存；刷新成功保持有效选择。
- Welcome 每次打开都调用可注入随机源重新抽取；00/其他权重边界正确，关闭重开不复用选择；
  默认/最小尺寸、可拉伸和无旧 FontIcon 块均有覆盖。
- C 系列 ICO 包含规定尺寸，所有产品进程的 ApplicationIcon/运行时路径一致。
- 语言切换不调用 DetectTool，不重启 App/Agent；托盘菜单文本按语言更新。
- 自定义 accent 的资源值和关键控件 Brush 更新；System 模式才响应 Windows accent 事件。
- 工具缓存可读取、显示检测时间；进入 Settings 和切语言不做全量探测。
- 偏好和数据库所有权测试覆盖干净创建、旧格式明确拒绝、损坏回退、并发写、正式构建不读取
  `tool-paths.json`、不生成 workspace/machine JSON、Runtime 回收、日志滚动和不再自动创建
  监控 CSV；不编写 V0.4 数据迁移测试。
- 默认 5 Hz；持续监控偏好默认 false、保存/恢复、UI close detach、Agent restart auto-start。
- 持续监控开关和托盘状态对账；timeout/unknown outcome 后以 snapshot 为准。
- PauseTest 状态机覆盖 running→pausing→paused→running、terminal race、恢复中断和重复请求。
- 托盘无测试项禁用；菜单顺序和命令集合没有额外项目。
- 磁盘 eligibility 与左右布局架构门；移除 reset/warning；右键坐标传递。
- 开机启动注册指向 App，重置后旧 Agent Run 项不存在且不会并存。

### 6.2 完整门

从 WinPool 根目录执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

还必须执行 `build/Publish-Staged.ps1` 到一个不存在的新 staging 路径，验证四进程层级、V0.41
ProductVersion、C 系列图标内容和禁止文件。计划实施期间不得把 staging 当成已授权部署。

## 7. 原生、重启和人工验收

### 7.1 冷启动与欢迎

1. 在一次真实 Windows 重新登录或重启后、没有 WinPool 进程时启动 App。
2. 记录 Welcome visible、tray visible、Agent Ready 和 inventory refreshed 时间；欢迎必须先于
   inventory refresh，且启动中不得出现“托盘连接失败”。
3. 在当前验证机目标：欢迎可交互首帧不超过 1.5 秒，托盘可见不超过 3 秒；如硬件环境无法
   达到，记录实测值并由用户决定是否接受，不能调整日志口径冒充通过。
4. 第二次 warm launch 激活已有窗口，不创建第二 App/Agent。
5. 100%、125%、150%、200% DPI 检查欢迎布局、看板娘缩放、C 图标和按钮。
6. 连续关闭并从启动、托盘和设置入口打开欢迎至少 20 次，确认每次重新抽取；手动拉伸后再次
   打开恢复默认尺寸，不继承上次尺寸。

### 7.2 监控与托盘

1. 默认持续监控关闭、频率 5 Hz；进入/离开 Monitor 不改变状态。
2. 勾选后关闭主窗口至少 10 分钟，SQLite 采样继续；重开 App 接回同一会话和曲线窗口。
3. Agent 重启或 Windows 重新登录后，已启用偏好自动恢复一个会话；关闭偏好不自动启动。
4. 托盘中英文切换、指定菜单顺序、测试禁用状态、监控暂停/恢复和完整退出逐项验证。
5. 用至少一个多步骤/多批次测试验证安全暂停；单长步骤验证“正在暂停”不会谎报为已暂停。
6. 点击曲线下方“事件”按钮，验证小页面的倒序列表、详情、空态、键盘关闭和 DPI 布局。

### 7.3 页面与设置

1. 静置主界面 15 秒且移动/悬停各导航区，不出现无请求的 Ctrl+1 提示；Ctrl+1～Ctrl+6 仍可用。
2. 六个预设 accent 和 System 模式分别检查；切换后立即生效，重新启动保持选择。
3. 中英文切换无需重启且不触发工具检测；托盘 1 秒内同步。
4. Settings 首次进入立即显示工具缓存和上次检测时间；手动检测才访问文件/版本/哈希。
5. 管理拓扑在节点不同位置右击，菜单靠近光标且不越过屏幕边界。
6. Edit 页在 1440×900 和最小支持窗口检查磁盘列表、分区条、右侧按钮、键盘选择和滚动。
7. 数据路径默认同一行且可通过 Tooltip 读取完整值；About 没有内部“关于”标题；全部设置子块
   在宽/窄窗口使用一致的两列或统一折叠布局。
8. 检查活动数据根：持久偏好只有 `settings.json`，运行数据只有 `winpool.db`；旧根已在 Rubbish
   留有可恢复副本且新版本未读取，Runtime/Diagnostics/ManagedTools/Staging 与启动指针符合
   声明的生命周期。

所有 UAC、真实外部工具、设备、长时监控、重启和视觉结果必须按实际执行记录为 `passed`、
`failed`、`unverified`、`not_required` 或 `deferred_by_user`。

## 8. 性能预算与防回退

- Welcome 首帧路径不得访问网络、枚举外部工具、扫描硬件或等待 Agent IPC。
- 看板娘派生资源按显示尺寸加载；单张解码目标不超过实际需要，不同时解码 8 张大图。
- Settings 进入不得自动哈希所有工具；缓存读取应为一次有界查询。
- Monitor UI 在 5 Hz 下绘图采用有界窗口和合并刷新，不要求每个样本触发一次完整布局。
- 托盘菜单重建只在语言或状态改变时发生，不用轮询重建。
- 不把所有新逻辑堆回 MainWindow、SettingsPage、MonitorPage 或 TrayApplicationContext；启动
  协调、欢迎选择、偏好、托盘投影、磁盘 eligibility 和测试暂停各自保持单一职责。
- 为新增服务设定消费者和边界测试；禁止创建无消费者的“未来扩展接口”。

## 9. 明确不做

- 不开放或实现 V0.5 真实存储结构 mutation。
- 不创建 MSIX、Store package、证书、商店账户或提交材料。
- 不上传 QQ 群二维码，不调用未审查的移动端 URI scheme。
- 不增加自由命令、脚本执行入口或第三方工具捆绑。
- 不实现任意线程/进程强制冻结式测试暂停。
- 不在 V0.41 实现完整事件中心、复杂筛选、搜索、事件确认或跨会话事件管理 UI。
- 不全面重做 Manage、Test、Development 页面视觉。
- 不修改冻结 Archive 中历史计划来改写旧事实。
- 不清理 Research、Tests、Dite、KS、Showcase 或其他项目。

## 10. 停止条件

出现以下任一情况，停止对应工作包并请求用户决定：

- 必须使用未文档化进程冻结才能满足测试暂停；
- 冷启动修复要求在安全恢复完成前允许测试、监控或系统支持动作；
- 本机状态重置时仍有 WinPool 进程持有数据库，或准确源/备份目标无法确认；
- C 系列或看板娘资源存在来源/授权问题；
- QQ 固定链接失效或跳转到非预期域名；
- 新 schema 无法从空根稳定创建、验证或重新生成；
- 5 Hz 在目标设备上造成持续丢样、UI 卡顿或持久化背压，无法通过有界批处理解决；
- 页面布局要求与无障碍、DPI 或最小窗口约束直接冲突；
- 工作区出现与 V0.41 同文件的用户修改，无法安全合并；
- 自动门出现无法归因的回归，或真实失败被建议改写成 warning/ignored。

## 11. 批准门与完成门

### 11.1 实施授权

- 计划已经确认，用户已明确要求开始执行；实施中的代码、测试和资源接线变更必须保持在本计划范围内。
- 不提交、不推送、不部署，除非用户另行授权。

### 11.2 V0.41 完成条件

- 原 25 项需求和本轮补充的欢迎、事件、状态归属及设置布局要求均有已实施结果，或有用户明确
  接受的延期决定。
- 冷启动不再把 Recovering 误报为 Agent failure，cache-first 和欢迎优先有真实证据。
- 持续监控、5 Hz、托盘菜单、语言、accent、工具缓存和开机启动状态一致。
- 测试暂停遵守安全边界，不使用强制线程冻结，不丢失不可变计划或审计。
- Edit/Manage/Settings 指定布局和交互通过目标原生检查。
- 完整自动门和 staging 验证通过；实际计数写入 CHANGELOG。
- 产品版本、README、Product、Development、Quality 和运行时显示一致为 V0.41。
- 未经单独授权不 push、不 tag、不创建 Release、不上传二进制、不部署。

## 12. 实施记录

计划已于 2026-08-14 完整确认：欢迎页默认 960×640、最小 720×480、允许手动拉伸且不保存
尺寸；监控事件使用主窗口内居中的模态 `ContentDialog`。

实施于 2026-08-14 开始。WP1 已增加 Starting/Recovering/Running/Failed 生命周期状态，并以
自动测试确认恢复期间只允许快照、拒绝新工作，恢复完成才允许正常请求。WP3 已接线首帧独立
欢迎窗口、每次打开重新抽取看板娘，以及 C 系列多尺寸 ICO 到四个可执行文件和主窗口。Agent
实际提前发布 endpoint、持久状态重整、托盘与其余页面工作仍在进行，尚不可视为完成。WP5 已
将默认采样率改为 5 Hz，监控页改为持久化“持续监控”开关、非导航副作用，并把状态移入曲线
左上浮层、事件改为主窗口内居中的模态 `ContentDialog`。Agent 启动会按该偏好尝试恢复会话。
WP6 已建立封闭的 `PauseTest`/`ResumeTest` IPC 请求，并在 Agent 测试协调器中实现“请求暂停 →
当前原子步骤结束 → 安全边界暂停 → 原计划恢复”的基础状态机；托盘菜单已接线对应请求。该项的
Agent 自动测试为 83 passed、0 failed，客户端自动测试为 14 passed、0 failed。原生托盘行为、
真实多批次外部工具步骤和完整退出链仍未验收，WP6 不能视为完成。

WP2 已调整 Manage 页的首次刷新：在既有缓存读取之后，新扫描改为后台启动，避免 `Loaded` 等待
冷扫描后才完成首轮页面重建；扫描成功仍原位更新，失败仍由既有错误路径保留当前内容。App Release
构建为 0 warning、0 error。缓存存在/不存在、刷新失败保留缓存及真实冷启动首帧的原生用例尚未
执行，WP2 仍在进行。

WP7 已将 `RootGrid.KeyboardAcceleratorPlacementMode` 设为 `Hidden`，保留 Ctrl+1～Ctrl+6 的
导航处理而隐藏 WinUI 自动加速键提示；App Release 构建为 0 warning、0 error。静置、悬停和
实际快捷键的原生窗口验证尚未执行，不能仅凭构建结果关闭该项。

WP7 还已从 Edit 页移除“重置模拟数据”按钮、“仅模拟”提示、其事件处理器及无消费者的本地化
键；领域层的模拟重置契约与既有测试未删除。残留引用检查为空，App Release 构建为 0 warning、
0 error。页面布局和不可用真实操作的原生呈现仍待验收。

WP7 已把拓扑节点的 RightTapped 相对坐标传递到 `MenuFlyout` 的 `Position`，菜单不再以整个
选中节点边界作为锚点；WinUI 继续处理工作区边缘钳制。App Release 构建为 0 warning、0 error。
高 DPI、靠近窗口边缘和不同节点的原生交互验收尚未执行。

设置页 About 卡片的内部标题已经移除，七个 About 字段的标签列统一为与其他设置子块一致的
220px；无 140px 遗留列，App Release 构建为 0 warning、0 error。窄窗口折叠和视觉间距仍待
原生验收。

WP7 已新增 `PartitionableDiskPolicy`：容量有效且分区样式为 RAW/GPT/MBR 的 OS 磁盘可显示，
启动/系统盘仍显示但继续由操作保护逻辑禁用。Edit 页不再用下拉框选择磁盘，改为左侧所有合格
磁盘行（编号、名称、容量、状态和紧凑分区条）与右侧详情/按钮组。策略自动测试为 54 passed、
0 failed，App Release 构建为 0 warning、0 error；真实磁盘、DPI 和窄窗口原生验收尚未执行。

WP4 已把外部工具自定义路径并入 `settings.json` 的 `CustomToolPaths`，通过
`PreferencesToolPathConfiguration` 由同一原子偏好写入器读写；正式 App、Agent 和 Broker 已不再
创建 `tool-paths.json`。Agent 暂不可用时，正式 App 使用内存态工作区和库存服务，不再新建
`workspace.json` 或 `machine.json`。`AgentSnapshot` 的工具状态已改为 SQLite `external_tools`
缓存读取，Settings 后台显示缓存而不触发检测，显式“检测”按钮仍会刷新缓存。Infrastructure 测试
41 passed、Persistence 测试 98 passed，Agent、Broker 和 App Release 构建均为 0 warning、0 error。
旧本机状态已按授权流程重置：确认无 `WinPool*` 进程后，将
`C:\Users\Admin\AppData\Local\WinPool` 移至项目根
`Rubbish\20260814_v041_local_state_reset\LocalAppData\WinPool`，其中保留四个旧文件；验证过程中
重新生成的单一 `settings.json` 另移至同目录下 `post_reset_test_artifacts\LocalAppData\WinPool`。
活动 Standard 根与便携根目前均不存在。随后已创建 schema 13：移除了泛用
`preferences` 表，工作区状态使用 singleton `workspace_state` 表，测试预设使用
`test_presets` 表；schema 12 及更早数据库仍 fail-closed，不做迁移或静默改写。
原先只服务 SQLite 偏好的仓储及其测试已移动到项目根
`Rubbish\20260814_v041_sqlite_preferences_retired\...`。持久化自动测试为
96 passed、0 failed；干净根与原生验证仍在进行。

`LocalUserPreferencesService` 现只接受 `FormatVersion = 1`，未知格式或损坏 JSON 回退到安全默认；
保存时规范化采样率为 0.2～20 Hz、滤除非绝对工具路径，并将工具路径写入 `CustomToolPaths`。该改动
的 Infrastructure 与 App Release 构建均为 0 warning、0 error；首次实际启动的干净文件清单已记录，
托盘菜单与长时行为的原生验收仍未执行。

WP1/WP2 随后进一步前移了 Agent endpoint：进程先创建 Recovering 生命周期、托盘上下文、受鉴权
named-pipe server 和 `Runtime\agent-endpoint.json`，再同步初始化 SQLite、测试恢复和完整运行时。
bootstrap coordinator 只返回不含工作负载的 Recovering snapshot，其他请求一律返回
`agent.request.recovering`；runtime 附接且恢复完成后才 MarkReady。该合同已有 Agent 自动测试
84 passed、0 failed。2026-08-14 从不存在的 Standard 数据根进行的原生冷启动记录为：Agent process
约 0.84 秒、WelcomeWindow 约 0.90 秒、endpoint 约 1.69 秒；欢迎窗口真实尺寸为 960×640，未观察到
Agent 连接失败文本。该次启动建立的根仅有 `winpool.db` 与
`Runtime\agent-endpoint.json`。这证明首次连接不再等待完整恢复；托盘右键菜单、长时监控与
各类设备上的完整启动计时仍待后续原生验收。

Runtime endpoint 现固定在 `Runtime`，崩溃与监控诊断写入 `Diagnostics` 下有 1 MiB 上限和单个
滚动副本的 JSONL 文件；`ManagedTools` 与 `Staging` 的固定布局契约已建立，受管工具实际安装和
保留策略仍待对应功能验收。Edit 页分区条也已改为使用可验证存在的 `WinPoolAccentBrush`，避免
预设主题色下查找未定义系统资源的运行时失败。

设置页原生验收已确认所有卡片使用统一 220px 标签列，About 卡不再有单独“关于”占位，数据位置选择
与完整路径同一行，开机启动文案为“登录 Windows 时自动启动 WinPool”。原始预设主题色问题也已
复现：下拉值变化但 Shell 的选中态仍只使用系统灰色。Shell 导航项现有显式、可观察的强调色背景
和前景绑定，随单一 `WinPoolAccentBrush` 更新；选择“红色”后当前“设置”项即时变红，截图存于
`Rubbish\20260814_v041_native_verification\settings-red-accent-fixed.png`，随后已恢复“跟随 Windows”。

数据位置迁移清单现显式排除活动根的 `Runtime` 子树；设置、SQLite、诊断和受管载荷仍以清单、
哈希和数据库审计复制验证，临时 endpoint 不会跨根迁移。对应 `StorageLocationManagerTests`
为 24 passed、0 failed；测试中的无关 SQLite 连接池在夹具清理前只清理自身连接，避免出现与
迁移逻辑无关的临时文件占用假失败。

语言原生验收首次发现偏好原子保存的并发读锁缺陷：下拉框会显示 English，但 Agent 对
`settings.json` 的只读句柄禁止替换，保存失败后留下 `.tmp`，使语言没有真正持久化或重绘。偏好
读取现允许读写/删除共享，写入采用唯一临时名、刷盘和原子替换；新增回归测试覆盖已有文件替换与
临时文件不残留，结果为 1 passed、0 failed。将在重启后的原生语言、托盘同步验收中确认该修复。

重启后原生语言验收已完成：选择 English 后约 0.78 秒内持久化 `Language=EnUs`，标题、Shell 导航、
设置字段、操作按钮和工具说明均即时切换；无 `settings.json.tmp-*` 残留。外部工具目录原有的中文
Purpose 已补入本地化投影，避免英文界面混杂中文说明。验收后已恢复 `Language=SystemDefault` 与
`AccentColor=System`。Agent 的托盘 Watcher 仍须在托盘菜单原生可见性检查中最终确认。

监控页默认原生检查已通过：持续监控为 Off、偏好为 false、采样率选择和持久化值均为 5 Hz；曲线
左上角状态浮层和“事件”模态空态均可见，截图为
`Rubbish\20260814_v041_native_verification\monitor-default-5hz.png`。但开启持续监控时开关会在
约 3 秒内回退到 Off 且偏好回写 false，故“后台持续/重启恢复”尚未通过。独立 IPC 诊断程序按设计
被 Agent 以 `ipc.handshake.client-image-mismatch` 拒绝，确认不能绕过受信任 App 映像；下一步需在
App→Agent 正常监控路径补充失败可见性并修复实际拒绝或启动故障，不能把该项标记为已完成。

后续 dump 显示该回退的根因是 Agent 启动链在 `PreferencesToolPathConfiguration.CreateAsync`
等待 `LocalUserPreferencesService.LoadAsync`；该方法对小偏好文件仍使用异步文件流反序列化，
在该验证机上会一直停在 `JsonSerializer.DeserializeAsync` 的异步读，使 Agent 永远保持
Recovering。偏好文件很小，已改为同步读取和反序列化；Infrastructure 相关测试 3 passed、0 failed，
Agent 与 App Release 构建均为 0 warning、0 error。重启后原生验证已通过：持续监控勾选后 Agent
进入 Ready、采样状态运行中；关闭主窗口 Agent 与会话继续，重开 App 后勾选仍为 On 并接回同一
会话（窗口样本从 66 继续增长）；随后已取消勾选，偏好恢复 false，默认状态不自动启动监控。

全量 Release 自动门已执行：`dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1`
为 549 passed、0 failed、0 skipped；其中架构边界测试更新了 3 个已过时的旧断言，指向当前
`EphemeralWorkspaceStateService`、独立 `WelcomeWindow` 和 `ContinuousMonitoringCheckBox`。
`dotnet build WinPool.slnx -c Release --no-restore -m:1` 为 0 warning、0 error；
`dotnet list WinPool.slnx package --vulnerable --include-transitive` 未发现易受攻击包。
`Publish-Staged.ps1` 到新的 `Rubbish\20260814_v041_staging_verify` 已验证四进程层级、禁止文件、
禁止外部工具和 V0.4 ProductVersion，全部通过。随后产品版本已按确认条件升为 V0.41，并已将
本计划冻结到 `docs/Archive/V0.41/`。

补充原生检查：欢迎窗口实际打开尺寸为 960×640；Edit 页已无“重置模拟数据/仅模拟”，磁盘区呈现
左侧三条合格磁盘行与右侧按钮组；Settings 的数据位置下拉与路径同一行，About 卡片无内部“关于”
标题，“加入 QQ 群”按钮存在。前台发送 Ctrl+1 后未观察到自动快捷键提示元素。托盘图标可见且
文本为 `WinPool — Ready`；托盘右键菜单项仍未在 UIAutomation 树中稳定枚举，仍标 `unverified`。
