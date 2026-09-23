# WinPool 验证与验收

本文件规定验证选择与结果含义。有活动阶段时，范围和进度记入 `docs/Plan.md`；已完成阶段见[归档](Archive/README.md)。技术约定见 [Development](Development.md)，真实操作边界见 [Product](Product.md)。

## 2026-09-23 界面分隔与消息表面：定点验证

关闭已核实位于标准 `artifacts/Release` 的 WinPool App / Agent 后，执行 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，最终结果 0 警告、0 错误，产物直接重建于标准运行目录。未另建隔离运行树。本轮为可逆界面调整，未扩成全套测试。

最终标准 App 原生启动并截图核对：`settings.png` 中标签列收紧、主题下拉不拉满、路径／7Z 下拉与图标按钮紧邻，标题栏系统选择器变窄；`storage-structure.png` 与 `disk-partition.png` 中右栏紧邻单个 8 DIP 分隔槽；`development-detail.png` 中消息列表无表头、详情文本背景不透底；硬件只读刷新时的 `notification.png` 中右下 InfoBar 背景为实色。图片均在 `artifacts/test-results/20260923-ui-feedback-final/`。UIA 读到设置页两个路径下拉宽约 188 DIP、相邻图标按钮各 32 DIP；开发页消息条目仍为 28 DIP。只读刷新没有提交存储编辑。

当前 `TestPage.xaml` 只有静态规划说明，没有子块或分隔槽；用户所说“测试页双倍分隔”无法对应当前源码，已请求位置确认，本轮未擅自改动其他页面。未做主题／DPI／最窄窗口全矩阵，也未把这些项目记为通过。

## 2026-09-23 控件角色尺寸与分隔槽：验证结果

标准 Release App 原生启动成功，UIA 读到存储结构编辑页右栏宽 319 DIP（布局取整后约为设计值 320 DIP）、分隔槽宽 8 DIP，拓扑区及操作区滚动容器可见。桌面前台保护多次拒绝拖动；单次工具报告成功后页面和尺寸观测不一致，故**拖拽效果不计为已验证**。480 DIP 窄窗、监控／管理／开发页及分区编辑页本轮未完成原生核对。截图文件生成但画面全黑，不能作为视觉证据；证据目录为 `artifacts/test-results/control-size-final-20260923/evidence/`。测试后恢复原 StorageStructure 页与 1440×900 窗口，只读确认设置未变；App 正常关闭、Agent 随后退出，最终无 WinPool App/Agent 进程，未执行存储操作。

按用户最新要求执行项目标准 `dotnet build WinPool.slnx -c Release --no-restore -m:1`，直接重建 `artifacts/Release`：exit 0、0 warning、0 error，App/Agent 的 `.deps.json`、`.runtimeconfig.json` 与 App `.pri` 五个启动文件均在运行树。Architecture 项目全量单次回归 47/47 passed、0 failed、0 skipped；TRX 位于 `artifacts/test-results/control-size-final-20260923/architecture-control-size-final.trx`。定点编辑页布局检查也曾 1/1 通过；完整回归额外发现标题栏交互容器的旧静态断言，已改为与当前 `ActiveSystemSelectorHost` 实现一致，复跑通过。`git diff --check` exit 0，目标 XAML 均通过静态 XML 解析。

此前两次隔离编译虽然各自 0 错误，其合并运行树均缺上述五个文件，因此不能作为原生运行证据；第一次直接启动在进入界面前崩溃。缺失文件实际被生成到默认 `artifacts/trees/Release`，与覆盖运行树路径的结果分离；失败日志保留在 `artifacts/verification-control-size-20260923-195807-10b39158/`，完整隔离树的定点补齐记录在 `artifacts/verification-control-size-r2-20260923-202038-7c75851b/`。本轮最终以用户指定的标准 Release 重建结果为准，不把隔离尝试算作有效界面验证。

## 2026-09-23 七项界面反馈：验证结果

最终隔离构建位于 `artifacts/verification-ui-feedback-20260923-responsive-8238ee77/`，App/Agent Release 编译 exit 0、0 警告、0 错误，运行树合并 288 shared／294 App-only／10 Agent-only／0 collisions；直接相关的 Architecture 两项 2/2 passed、0 failed、0 skipped，TRX、命令和日志保留在该目录。已有 `artifacts/Release` 未作为构建目标。第一次隔离构建在 `artifacts/verification-ui-feedback-20260923-a2225ec1/` 通过编译和开发页定点测试，但原生检查发现分区属性栏 320 DIP 时长 MiB 数字使右侧单位被裁，原始画面在该目录的 `evidence/partition-before-width-fix.png`，UIA 读到两个单位均 offscreen。第二次隔离构建 `artifacts/verification-ui-feedback-20260923-final-1b28b26b/` 改为 420 DIP 后，同一对象的完整单位可见；随后独立审查发现固定 420 DIP 会挤占最小窗口的拓扑视口，因此最终改为随窗口宽度在 320–420 DIP 之间调整，并为窄窗右栏增加横向滚动。前两次结果均未冒充最终通过。

原生界面：开发页四列表头依次为时间、级别、来源、信息标题，标题列实际显示“采集成功”，没有拼接正文；消息列表行 UIA 为 28 DIP，文字纵向位于中间。双击详情后，唯一只读 TextBox 位于页面中央，实色主题背景与暗色遮罩可从 `evidence/development-detail.png` 区分；截图中框内左右空白采样同为 RGB(28,28,28)，框外为 RGB(21,21,21)，未出现底层左右区域透出。分区页复验本机 C: 时，起终点的 MiB 数字分别处于同一列，括号内数字另处一列；`952,664.54199219MiB (930.34 GiB)` 的两个单位都在宽窗 420 DIP 栏内，截图 `evidence/partition-aligned-complete.png`。选择模拟空隙后，起点 `593,937MiB (580.02 GiB)`、终点 `1,907,348MiB (1.82 TiB)` 分列，第一行容量 `1313411` MiB、第二行 `1.25 TiB` 用普通字号显示，截图 `evidence/partition-gap-capacity.png`。窗口缩至 900 DIP 时右栏仍为 420 DIP，两个单位可见，见 `evidence/partition-900.png`；缩至最小 480 DIP 时右栏为 320 DIP、左侧拓扑视口约 142 DIP，与旧布局相同，横向滚到右端后两个单位可见，见 `evidence/partition-480.png`、`evidence/partition-480-scrolled-end.png`。本轮仅选择对象，未提交创建或格式化。

UIA 读到系统自绘标题栏和界面标题行均高 48 DIP。左侧标题区域从 (1080,294) 到 (1140,294)、以及从 (1080,310) 到 (1140,310) 的两次真实鼠标拖动，各使 1440×900 窗口水平移动 60 像素；每次都拖回初始 (1000,270)。导航、可见的系统选择器容器、真实编辑控件和系统窗口按钮是 Passthrough／系统按钮区域，不能作为拖动区。App 全局与结构／分区页显式 Button 样式都设置 4 DIP 圆角，管理和分区页截图已看；未逐一遍历所有页面、主题与 DPI。实际屏幕取证使用 `winapp ui screenshot --capture-screen --focus`，本次返回可辨认画面。测试后恢复原来的本机系统和管理页，并退出经路径核实的隔离 App/Agent；没有修改真实存储。

## 2026-09-23 通知卡透明余高修正

基线为 `40d8a9b` 加本轮修改。移除 `NotificationCard.xaml` 外层 `MinHeight=72`，让 384 DIP 宽的卡片高度跟随单层 InfoBar 内容；200 DIP 最大高度、堆叠间距和向右离场规则未修改。更新旧的架构断言后，`NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails` 定点测试 1/1 passed；隔离 App/Agent Release 构建 exit 0、0 warnings、0 errors，合并 288 shared／294 App-only／10 Agent-only／0 collisions。有效命令、日志、TRX 和运行树位于 `artifacts/verification-notification-card-height-20260923-r2/`；既有 `artifacts/Release` 的 592 个文件哈希及时间戳未变。首次 r1 命令因工作目录错误在编译前退出，记录已保留；r2 编译的项目库和测试中间输出仍使用共享 `artifacts/build/<项目>/Release`，没有将其当作完全隔离。

当时用户运行的 `artifacts/Release` App/Agent 是旧版本，未为了该次小幅布局修正中断。因此新卡片的原生视觉高度与离场后占位未验证；该轮通过范围为源码、定点架构测试和隔离编译。此前 UIA 读到的短卡 InfoBar 高度约 53，但当时外层仍有 72 DIP 下限，不能作为修复后的原生证据。

## 2026-09-23 上一轮 13 项界面反馈：验证结果

基线为 `5424902` 加本轮修改。最终 Architecture 47/47 passed、0 failed、0 skipped；`WinPool.slnx` 隔离 Release 构建 exit 0、0 warnings、0 errors，App/Agent 合并为 288 shared／294 App-only／10 Agent-only／0 collisions。TRX、实际构建命令与日志位于 `artifacts/verification-20260923-final-integration-5c02b9a7/`；合并产物位于其 `Release/`，共 592 文件。保护目录 `artifacts/Release` 的 App EXE 哈希和时间在构建前后相同。自动测试不代替原生界面检查。

原生 App 与 Agent 从该隔离 Release 目录运行，使用现有标准数据根，不执行模拟创建、格式化或真实存储写入。开发页横向、纵向分隔条均实际拖动，消息区 UIA 边界由 746×525 变为 860×461；两条消息以单行列出，列表及区域自动化名称均为“消息列表”，页面没有可见标题。悬停第一条后点击，UIA 读到 `IsSelected=True`；双击后唯一只读 `MessageDetailText` 边界为 640×171，中心与内容区中心一致，点击框外后控件消失。实际 Ctrl+C、长文本滚动和悬停/选中颜色视觉未核对。

分区边界模拟系统选择可见间隙时，UIA 读到“起点” `593,937MiB (580.02 GiB)`、“终点” `1,907,348MiB (1.82 TiB)`、默认容量 1313411 MiB，第二行 `1.25 TiB`；第一行输入、MiB、MAX 和第二行换算值的边界分行清楚。该系统只出现一段大未分配空间；恢复的小间隙阈值源码核对通过。选间隙时唯一 `PartitionActionButton` 为“新建分区”，选已有 C: 时切为“格式化分区”。管理页下区 UIA 见池仅 1 个编辑按钮、磁盘仅编辑及系统属性 2 个、分区指定 4 个；本机磁盘编辑实际进入分区页。层分类在本次样例中无条目，按钮未原生核对；入池物理盘路由按分区拓扑可见投影修正，未另造样例进行原生触发。

自动本机只读刷新期间，UIA 见一张宽 384 的通知 Group，只有一个 `NotificationInfoBar` 子控件；XAML 已移除外层 Border。截图工具的窗口捕获返回全黑 4 KB PNG，屏幕区域捕获包含其它应用而非 WinPool 窗口，均保存在同一验证目录 `evidence/`，不能作为卡片单层外观、暗色遮罩或选中颜色的视觉证据。通知右退场、不同文本高度和减少动态效果本轮仍 `unverified`。测试后恢复原系统 `[模拟] 其它与网络` 和硬件页，并退出隔离 App/Agent；未清理数据。

## 2026-09-23 上一轮 21 项要求：历史验证结果

以下以 `1d94d2e` 加上一轮源码为基线的回归、构建和原生验收，只适用于上一轮 21 项要求，不覆盖本轮改动。Release 自动回归：Application 291/291、Infrastructure 93/93、Architecture 47/47；Persistence 130 passed、3 个既有大型监控测量按门控 skipped，均 0 failed。新增启动只读读取器的子代理定点 Debug 回归 7/7 passed。TRX 位于 `artifacts/verification-20260923-final-r1/test-results`。Architecture 首轮 46/47：旧断言要求先呈现默认系统，已改为检查预显顺序，R2 47/47；Infrastructure 首轮 90/93：三个格式化测试夹具用非整数 MiB 创建分区，与新规则冲突，改为有效的 500 MiB 后 R2 93/93。首轮失败 TRX 均保留。新增分区几何回归还覆盖大小写不同的磁盘关联与删除中间分区后编号唯一。隔离 App/Agent Release 构建见同一验证目录的 `release-build.log`，exit 0、0 警告、0 错误，运行树在 `artifacts/verification-20260923-final-r1/Release`，合并 288 shared／294 App-only／10 Agent-only／0 collisions；未替换 `artifacts/Release`。

有限原生核对在上述隔离 Release 运行树和现有标准数据根进行，没有清理数据。关闭 Agent 后冷启动：计时脚本约 663 ms 找到主窗口、约 1883 ms 读到 `[模拟] 分区边界`，当时标题栏选择器可见但禁用，说明 Agent 完成前已呈现上次系统；随后选择器启用。该计时包含 WinApp CLI 调用开销，不能作为准确绘制时延或逐帧“零闪烁”证明。重新启动后 UIA 与 `evidence/restart-partition-simulation.png` 核对上次分区页和模拟系统。测试结束将原选择恢复为本机系统／开发页，下一次启动再核对读回；App 和隔离 Agent 均已退出。

开发页 `evidence/startup-restored-development.png` 显示左上单行日志、右上空区及下方提示；第二次双击日志后 `evidence/development-log-detail-r2.png` 显示仅有文本框的详情浮层，UIA 读到 `IsReadOnly=True` 和完整内容，点击下方空区后该编辑控件消失。未实际改动剪贴板，Ctrl+C 复制动作本轮 `unverified`。分区页 `evidence/partition-gap-mib-max.png` 显示独立的右侧“新建／格式化”、固定 MiB 输入后缀、第二行自动单位及图标式最大值；选择 MSR 后空隙时 UIA 读到起点 `17MiB (17 MiB)`、终点 `953674MiB (931.32 GiB)`、默认容量 `953657` MiB，改为 100 MiB 后按最大值恢复 `953657` MiB。选择磁盘头部 1 MiB 空隙时“新建”禁用，UIA HelpText 为“起点按 1 MiB 对齐后，剩余空间不足 1 MiB。”；未提交模拟新建或格式化。成功通知卡曾在 UIA 中显示 384×72 DIP；长短内容的高度变化及向右退场画面本轮 `unverified`。设计表及归档做了内容、链接和范围检查；图标及样式 HTML 在本机 Edge 呈现后的截图为 `evidence/buttons-table.png` 和 `evidence/styles-table.png`，图形与色块可见。真实存储写入未执行。

下面“最近已记录的验证基线”及其后各段只说明此前对应提交/基线的范围，不自动覆盖本轮变化。

## 最近已记录的验证基线

2026-09-23 系统 JSON、格式化方式、通知卡和开发页本轮：基线 `87c4f6e` 后的本轮改动。定点 Release 回归为 Application `V049PartitionSemanticsTests` 15/15、Infrastructure 格式协调与命令预览 28/28、Architecture 47/47，均 exit 0、0 failed/0 skipped；TRX 和日志在 `artifacts/verification-20260923-112235`。导入 JSON 嵌套形状补充检查的 Application 定点 Debug 回归另为 3/3 passed、0 failed（终端结果，未另存 TRX）；它只验证该校验器，不替代系统导入原生往返。首次 Infrastructure 因新增计划参数 bool 与文本辅助方法类型不匹配而编译失败；首次 Architecture 44/47，三项旧断言仍要求已移除的开发页路线标题和固定取三卡写法。修正类型并把断言更新为当前导航门、内存日志、三区域和卡片边界后，Infrastructure 与 Architecture 分别在 R2 得到上述通过结果。没有把首轮失败覆盖或记为通过。

第一次隔离 Release 构建因格式开关 UIA 命名遗漏 `Microsoft.UI.Xaml.Automation` 引用而失败，同轮还报告 XAML 类型解析错误；失败日志保留于上述验证目录。补齐引用后在全新 `artifacts/verification-build-r2-20260923-113438/Release` 完整构建 exit 0、0 警告、0 错误，表明该轮已无上述 XAML 诊断；App/Agent 合并为 288 shared／294 App-only／10 Agent-only／0 collisions。导入形状校验器接入 App 后又进行一次隔离 WinUI App Release 编译，`artifacts/verification-20260923-import-json-app-release-r1/logs/app-release-build.log` 记录 exit 0、0 警告、0 错误，合并报告仍为 288／294／10／0；目标执行前确认不存在。所有构建均未替换 `artifacts/Release`。本轮未运行全解决方案测试、真实存储写入或完整设备/DPI/高对比度矩阵；有限原生交互结果如下。

有限原生核对使用上述隔离运行树及现有标准数据根，未清理或迁移数据。主代理查看 `evidence/development-page.png`：无大标题，左上日志/右上空区/下方提示卡三块位置与边界正确；双击现有本次运行条目弹同窗只读详情，内容和复制结果由 UIA/剪贴板核对，测试后恢复原剪贴板。主代理查看 `format-full-on.png` 与 `format-quick-restored.png`：完整开启时快速关闭、恢复快速时完整关闭，两个开关的中文 UIA Name 和模拟说明可读；未点击格式化。保存选择器截图显示 `JSON (*.json)`；实际导出 `evidence/WinPool-SystemExport.json` 能由标准 JSON 解析，扩展名 `.json`、`Product=WinPool`、外层/内层 schema 均为 3、系统类型 Simulation。仅在本地保留原始系统数据，不公开。测试后 LastActivePage 恢复 StorageStructure，DeveloperMode、主题及语言保持原值，App/Agent 均正常退出。

通知卡固定尺寸与向右退场已有源码、WinUI 元数据及编译核对，原生画面为 `unverified`。一次 15 秒 WinPool 单窗口 gdigrab 录屏正常封装 438 帧，但提取帧为全黑，不能作为卡片出现或动画证据；原视频、日志和黑帧留在同一 evidence 目录，不重录也不把黑帧报成通过。录制期间再次导出的模拟系统 JSON 可解析，未提交格式化或真实存储修改。完整中英文长消息、减少动态效果、480×300 窄高窗口卡片堆叠、点击／超时／挤出动画、完整 DPI/高对比度矩阵保持 `unverified`。

2026-09-22 系统盘删除与模拟扩缩修正：用户回报的“同盘分区全部不能删除”和“扩展／压缩无效”经只读审计确认为实现缺陷。修正后 Application 278/278、Infrastructure 90/90、Architecture 47/47 全通过，TRX 位于 `artifacts/v055-delete-resize-fix-20260922-r1/test-results`，主代理已核对；完整隔离 Release 构建写入 `artifacts/v055-delete-resize-fix-20260922-r1/Release`，0 警告、0 错误，并集合并 288 shared／294 App-only／10 Agent-only／0 collisions。首轮隔离命令漏写路径尾部分隔符导致合并目标落错位置，该轮构建失败未被当作通过；修正后重新构建取得明确成功，误建的两个中间树已按文件处置规则移入项目根 Rubbish。修正后的运行树已替换为用户原先使用的 `artifacts/Release`（592 文件），替换前旧树完整保留在 `Rubbish/20260922_release-before-delete-resize-fix/Program/WinPool/artifacts/Release`。本轮原生扩缩与内置模拟删除仍为 `deferred_by_user`，未做真实存储修改。

2026-09-22 更正：下述反馈修复只是已取得的局部证据，上轮据此归档过早，不能代表用户 15 项全部完成。当前已重开 [Plan](Plan.md)，逐项补查实现遗漏及原生验证缺口；既有通过记录仍保留，未验证项不自动转为完成。

本轮代码补齐后：Application 272/272、Infrastructure 88/88、Architecture 47/47 全通过，TRX 位于 `artifacts/v055-feedback-completion-20260922-r1/test-results`，主代理已核对。最终完整隔离 Release 构建为 `artifacts/v055-feedback-completion-20260922-r2/Release`，exit 0、0 警告、0 错误；r1 构建会话没有最终退出状态，保留 unverified，之后在新 r2 目录构建取得明确结果，未重复测试。原运行目录未覆盖。模拟扩缩的几何、方向文件系统、容量差值、只读门控及持久化偏好回归已覆盖；扩缩容与容量重置的原生操作按用户决定为 `deferred_by_user`，不计为通过。

补验已确认：当时用户运行的 `artifacts/Release` 普通实例上，普通成功卡出现后 422 ms 真鼠标点击，同一调用内 UIA 与截图证实移除；动态“删除模拟系统”和静态“格式化”禁用按钮真实悬停均显示具体原因。主代理实际查看前后图和 tooltip popup，证据保存于 `artifacts/v055-feedback-completion-20260922/evidence`。该实例不是已消失的 r4 临时树，不能混写构建身份；本轮四处用途提示新增代码和模拟扩缩容恢复已通过上述统一构建，但不因此冒充原生验证。

管理员补验：同一当前 Release 的提升实例 PID 11748 实际打开模拟导入“打开”和导出“另存为”窗口；主代理查看 `admin-open-picker-open-20260922-120027936.png`、`admin-save-picker-open-20260922-120027936.png`，并核对 cleanup JSON 的两个 `Cancelled=true`。两窗口均已取消，未导入数据或保存文件；此证据只覆盖管理员窗口打开/取消，不代表管理员文件往返。此前辅助脚本的中文编码、导航匹配和误判同进程文件窗口造成数次自动化失败，不记为产品失败或通过。原“单盘池／磁盘分区编辑”已恢复，主代理核对读回 JSON 和 `admin-restored-singlepool-20260922-120224378.png`，App/Agent 保持运行。

2026-09-22 V0.55 人工反馈修复完成实现及有限验证：Application 首轮 265 项中 264 passed、1 failed（新增用例误用 `Single()`，不是产品实现失败）；修正测试夹具后，受影响用例及通知测试 9/9 passed。Infrastructure 86/86 passed。Architecture 首轮 46/47 passed，磁盘改名入口绕过投影的新增代码已修正，失败守卫定点复测 1/1 passed，未放宽原断言。主代理已核对 TRX 与相关代码，不重复执行已通过测试；证据位于本地 `artifacts/v055-feedback-repair-20260922-r1/test-results`，详细范围见[反馈修复归档](Archive/20260922-feedback-repair/README.md)。

最终完整隔离 Release 构建为 `artifacts/v055-feedback-repair-20260922-r4/Release`，exit 0、0 警告/0 错误。首轮发现 WinUI Border 不可继承，已通过项目 WinMD 元数据核验改用可继承的 Grid；第二轮开发页遗漏命名空间引用已修复；r3 未取得最终退出状态，保持 unverified，不能作为最终构建证据。各轮输出原位保留，未替换原运行树。

本轮有限原生通过：普通进程模拟导出→导入新副本→仅删除该副本、简洁错误卡与真实鼠标点击消息对话框、可用动态按钮用途悬停、两个自动创建偏好跨模拟系统保持并恢复。主代理实际查看有效截图。普通卡前后截图相隔 23 秒，不能区分点击和自然超时；禁用按钮悬停被前台限制及窗口遮挡影响；两项保持 unverified。管理员选择器、重启偏好、删除内置后重启及完整主题/DPI矩阵未原生验证。原有模拟系统未删除，真实存储未修改。

2026-09-22 V0.55 通知与轻量消息阶段收口：基线 `d304942` 加本轮改动，限定 Application 回归 60 passed、0 failed、0 skipped；Architecture 47/47 passed；完整隔离 Release 构建 0 警告/0 错误。Application 测试编译曾有一条 xUnit2031 风格警告，不影响测试结果。主代理已审阅代码、TRX 和截图；未重复执行子代理已通过的检查，不代表全套回归或完整设备验收。命令与本地证据见[阶段归档](Archive/20260922-information-system/README.md)。

有限原生验证通过：实际 Diagnostics 路径复制、选中消息详情复制、清空、开发者导航门开关与恢复、中英文切换与恢复、深色系统主题、上下文帮助和 900×900 窄窗。测试实例正常退出，偏好恢复为 System 主题/SystemDefault 语言/DeveloperMode=true/Settings 页面；隔离 Agent 经路径核实后停止。未迁移或清理数据，未修改真实存储。超量通知、重要错误和悬停/焦点计时未原生注入验证；服务行为测试及源码检查不能替代这些原生交互证据。完整 DPI/高对比度矩阵、重启后历史清空、数据根切换保持 `unverified`。开发页首屏的“复制全部/清空消息”需滚动至上方内容区域下部，作为已知布局限制保留。

本机 WinApp CLI 0.6.1 默认 WGC 路径返回全零帧，且第五次重试后仍报告成功；默认截图不可作为通过证据。同窗口 `--capture-screen` 截图正常，已实际查看。后续在此前提下使用屏幕区域截图并确认前台无遮挡；先核对本机 `--help`，不假设较新技能中的 `ui yield`、`find-api` 已可用。

2026-09-21 V0.54 设置页小幅调整：7Z 同宽对齐、删除浏览按钮并显示路径、选择自定义调起选择器、MSR 简称及版本更新。按用户要求不重跑自动测试或原生验收，只执行一次隔离编译，产物位于 `artifacts/settings-tools-v054/Release`，未替换现有运行树。构建执行未返回最终退出码/汇总，完成状态为 `unverified`，不以产物存在代替通过；未为取得结果重复编译。此前设置页验证仅覆盖下述初稿，不视为 V0.54 最终界面验证。

2026-09-21 设置页工具布局初稿：基线 `600dc25` 加当时改动，隔离 App/Agent Release 构建 0 警告/0 错误；`WinPool.Application.Tests` 中 `AgentPreferencesReloadCoordinatorTests` 3/3 passed（`dotnet test -c Release --no-restore --filter FullyQualifiedName~AgentPreferencesReloadCoordinatorTests --maxcpucount:1 -m:1`）。结果保留于执行终端，未另存 TRX；不是全套回归。主代理核对初稿 XAML 中 9 个 Button 均包含图标，这是源码检查，不代替视觉验收。

有限 WinApp CLI / UIA 验证覆盖卡片顺序及控件存在、7Z 默认/自定义选项、调起 EXE 选择器、取消后保持默认且 Agent 偏好仍为空。测试使用隔离程序树、现有标准数据根，未迁移或清理数据，App/Agent 正常退出。自定义成功保存、两处 Explorer 实际打开目标、中英文窄窗及完整视觉为 `unverified`；`artifacts/settings-tools-check/evidence/` 中截图全黑或被遮挡，不能作为视觉通过证据。

首轮隔离构建遗漏最终合并命令覆盖，部分清理了旧运行目录的 17 个资源。旧目录已保留于项目根 `Rubbish/20260921_settings-build-recovery/Program/WinPool/artifacts/Release`，完整运行树恢复后与隔离树逐文件 SHA-256 比较一致（591 文件）；最终更新在 App/Agent 退出后执行。标准 AppData 数据不在该清理范围。构建隔离注意事项已记入 Development。

2026-09-21 监控轮换阶段：基线 `92047b9` 加本轮实现，最终完整 Release 测试 655 passed、0 failed、3 个门控测量 NotExecuted；完整 Release 构建 0 警告/0 错误，22 项目依赖审计无已知漏洞包。最终日志与 11 份 TRX 位于本地 `artifacts/test-results/20260921-full-gate-runtime-closeout-final/`。三个跳过的测量已分别执行，包括真实 1 GiB 阈值轮换及压缩期间新活动库继续写入、原始参数测量和两组压缩参数比较；不是把跳过当作通过。

有限原生 WinApp CLI 验证覆盖正常运行/停止、页面重入同会话时长、归档失败关闭后新失败重现、恢复默认 7Z 后成功隐藏、中英文窄窗单行异常、7Z 设置及托盘正常退出。双库/归档往返使用生产迁移器和真实 SQLite/7z，quiesce 为接口替身；不冒充原生设置迁移验证。实际 DPI/主题全矩阵、跨 Windows 会话、强制断联未知状态注入和本轮 UAC 全流程未重跑，保持 `unverified`；未做真实存储结构修改。详见[阶段记录](Archive/20260921-monitoring-rotation/README.md)。

2026-09-16 统一层模拟池/模拟层阶段记录：Release 613/613 测试通过，完整构建 0 警告/0 错误，依赖审计无已知漏洞包；实现提交 `9e15f54`，文档归档断言同步提交 `98f69b7`。有限原生验证覆盖中英文、选择、空容量及无来源弹窗；完整主题/DPI/拖放和生命周期退出未获完整验证。详见[阶段归档](Archive/20260916-unified-synthetic/README.md)及[整理前验证记录](Archive/20260916-docs-review/Quality-before.md)。

以上是已有证据，不代表后续改动自动通过。测试数量不是固定验收门槛；每次验证须记录实际基线、范围和结果。历史阶段的设备数量、DPI 和界面行为只适用于当时条件，不能合并成当前版本的全量验收结论。

## 选择验证范围

2026-09-16 编辑页整页重建修复：主代理执行 `TitleBarProvidesStorageSystemSelector` 单项架构检查，1/1 通过；这是系统身份导航守卫的源码回归检查，不是原生交互测试。隔离编译 `WinPool.App.csproj -c Release --no-restore` 及其 Agent 目标通过，0 警告/0 错误，输出在 `artifacts/editor-refresh-check/trees/Release`。用户要求不跑完整测试，本轮未重跑全套；保留正在运行的 `artifacts/Release`，原生操作后不闪烁的目视复测为 `unverified`。监控数据库轮换计划没有因此开始实施。

- 纯文档任务：检查内容一致性、当前链接、归档完整性及 Git 范围；不运行代码、原生、设备或视觉测试。
- 普通修复/小功能：运行能验证风险的直接相关检查，不自动扩成全套验收。低影响、可逆修改不为凑数量添加测试。
- 执行已确认 Plan：其中明确要求的回归和自动门属于任务本身，无须重复申请；阶段收口运行 Plan 指定范围。
- 完整人工、平台、设备和发布验收：仅在用户明确要求或已确认计划明确包含时进行。完成代码不自动表示完整人工验收开始或结束。
- 发现失败先判断是实现缺陷还是已失效的旧约定；不能删除有效安全断言来让测试通过，也不能让旧断言恢复用户已经否定的行为。

结果只能使用 `passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过、缺少环境和未运行均不能报 passed；源码推断与实测证据要分开。

## 文档与架构检查

- 当前内部文档为中文单一权威，仅根 README 成对维护；历史副本原样保留，不要求归档一律配对或翻译。
- 有活动阶段时只有一个 `docs/Plan.md`。用户要求保留的待执行计划可继续留在该文件中，但须明确标记“未激活”，不得当作当前实施授权。具体激活状态以 Plan 为准。Design 不被枚举为待执行计划；标题或旧正文中的命令式措辞不改变其状态。
- 检查当前文档链接和路径、当前版本与目标版本的区分、已实现与待验证的区分。
- 历史文档的原相对链接按归档说明追溯，不为修历史链接重写当前规则。
- 保留真正的依赖、单写入方、类型化命令与默认拒绝边界测试。文档检查验证当前约定，不依赖整段固定句子或把所有旧文件数量当成永久产品要求。
- Git 排除生成物、数据库、日志和源图，保留软件实际消费的资源。

内部文档为中文单一权威，仅根 README 成对维护。该约定由架构测试 `CurrentInternalDocumentsAreChineseAuthoritativeAndOnlyRootReadmeIsPaired` 固定。

## 存储语义与提交检查

优先使用固定输入、纯函数规则测试和实际应用服务集成测试；不以扫描源代码字符串代替行为验证。

- 已知合法、已知非法、信息不足都覆盖；未知不能被默认值转换为允许。
- 原始容量、逻辑容量、占用和可用范围分别检查；覆盖不同冗余、边界、对齐、溢出和估算来源。测试值不是 Windows 实测证据。
- 采集 → 转换 → 保存 → 加载保持身份、用途、层参数、卷和挂载点；缺失值与采集失败仍可辨认。
- 对象关联和关系投影一致；创建、删除、成员变更后无悬空引用、重复身份和错误归属。
- 预览与提交使用同一操作序列；拒绝时不产生部分模拟提交；修订冲突不覆盖其他修改。
- 覆盖“提交成功但回复丢失”，验证结果未知和持久化对账，不能仅断言抛出了异常。
- 能查看 Windows 结构不代表已验证相应修改操作。只读采集样本不能冒充真实写操作验证。

## 监控持久化与归档验证口径

监控轮换阶段的具体场景归[阶段计划归档](Archive/20260921-monitoring-rotation/Plan.md)，以下规定后续回归的证据口径，不自动赋予未运行的场景通过结论：

- 连续性测试必须在切换期间持续产生样本，并覆盖多次轮换；按样本身份或完整值的多重集核对，不只比较总条数。多个设备共享时间戳、同库多个会话、NULL 与零都须可区分。
- 故障测试验证最终数据和文件状态，不只断言抛出异常；覆盖旧库改名前后、新库初始化及归档发布/原库释放边界。重启不能以新建空库掩盖唯一有效旧库失联。
- 数据根迁移需证明恢复记录只引用目标根内的文件，未在旧根继续压缩、写入或释放源库；归档恢复以完整性和内容校验为证据，不以文件存在代替。
- 验证有界缓冲和后台故障时，同时检查实时采样、实际持久化、已知未保存数量与用户诊断的一致性。正常窗口淘汰不能计入持久化丢样。
- 实际 7z 测量记录输入数据、固定参数、原始/归档大小、耗时及峰值资源；小阈值故障测试不能代替生产 1 GiB 阈值与完整归档验证。构建成功不能代替这些行为测试。

## 自动门

正式阶段如 Plan 要求，在 WinPool 仓库根运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

自动测试使用各自夹具规定的数据，不修改真实存储结构。开发阶段可直接关闭 WinPool 进程、修改或重建已核实的 WinPool 开发数据，构建输出默认写入标准 `artifacts/Release`；不为保留旧运行实例额外搭建隔离产物或重复测试。记录实际命令、提交基线和结果；警告逐项说明，未解决失败不能作为完成。

App 和 Agent 独立产出、SHA-256 并集合并与碰撞失败机制继续验证；运行时查找与目录布局必须一致。命名管道身份/ACL、SQLite 所有权、原子提交及只读边界仍受直接回归保护。

## 人工与设备证据

WinPool 是原生多进程应用。浏览器 DOM 测试不替代 WinUI、托盘、原生选择器和设备检查。

需要实际证据的项目包括页面与拖拽、软件中英切换、主题/DPI/高对比度、键盘操作、托盘生命周期、文件夹选择器、监控启停和数据位置往返。1.x 的测试页验证路线占位及可访问性；V0.55 开发页增加日志路径和有界内存消息，验证其导航门、选择复制、清空和重启清空，不据此开发完整工作区或日志查看器。

模拟规则验证不执行真实存储写操作。未来允许真实写入的阶段，仍须按 Product/AGENTS 取得准确授权并记录目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。

## 历史验证入口

- [2026-09-16 文档整理归档](Archive/20260916-docs-review/README.md)：保留整理前 Quality 全文，包括 613/603/598 项回归与各次原生范围。
- [V0.52 实施核对](Archive/V0.52/实施核对.md)：统一事实与模拟编辑的原始验证。
- [硬件报告实施核对](Archive/20260915-hardware-report/实施核对.md)：硬件报告、来源与有限原生验证。
