# V0.59 活动计划：编辑界面、数值输入与操作反馈统一

计划日期：2026-10-12；状态更新：2026-10-13。基线：`main` / `07c0418`。状态：原限定验收与WDC实机破坏性补验证均已完成（见§7.1）。原验收结果保留为历史事实；本计划本轮验收已收口。本计划已获用户明确执行授权。版本保持 V0.59 / iteration 9，本次未要求升版。

上一阶段计划已逐字节归档至 [原 Plan](Archive/20261012-editor-ui-baseline/Plan.md)，SHA-256 `49178400481C3476C898A96F52931721A6416350E1CF8CDAD6E5FAFD6E15B6A1`。归档不是全部验收通过；旧未验证范围保持原状态，不自动扩成本轮任务。

## 1. 目标和不变量

逐项完成本轮25条要求，真实与模拟共用界面、输入约束和业务动作，不增加准备计划、按ID查询或分段人工确认入口。复用准确bytes模型、共享整数GiB MAX算法、Agent冻结核验与写入后对账。界面显示取整不改变既有对象bytes；不扩大Windows实际支持范围。

按现有授权可对本机WDC及其独占Pool01链进行必要GUI实测；两块Samsung禁止写入。每次准确定位硬件及对象父链，不用盘号/盘符猜目标。不得用自由形式存储命令测试。原始数据与证据不清理，`Ref/`不动。主代理独占桌面、进程及标准运行树构建。

## 2. 语义核对

以下已在正式代码修改前提问，三项均已收到用户明确答复并冻结。

1. 盘符／名称打勾直接提交该单一属性，不再弹改名确认；Undo恢复已提交原值，新对象恢复默认名。创建／删除／格式化的业务确认保留。
2. 纯数字容量框默认显示MAX算法初始整数上界C、内部仍执行既定搜索；手动输入改为明确容量，Max恢复搜索意图。不能将数字显示当成取消MAX算法。
3. 新建分区右栏容量可编辑；已有分区右栏容量只读，修改容量统一走扩缩弹窗；格式化沿用原容量。

## 3. 固定实现决定

- 容量前端只接受ASCII十进制整数，禁止负号、小数、科学计数法、字母、混合粘贴、IME非数字；可暂时清空以编辑，失焦恢复合法值。根据当前准确上限同步夹紧，溢出视作超上限；显示与保存、后端边界分别核验。其它应为正整数的列数、副本、故障容忍、忽略容量等共用约束，允许0的既有字段保留其合法下限。
- 新建容量默认最大；已有对象只读容量展示实际值，不能把“默认最大”用来无意改写既有大小。Max使用明确的可见文字或容量扩展图形及Tooltip，不复用有歧义的图标。
- 起点／终点共两行，同列上下对齐：首行 `整数 MiB - 整数 MiB`，使用整数除法向下截断；次行两个独立自适应单位值。终点沿用exclusive end=`offset+size`。
- 容量首行为可编辑MiB整数输入＋单位＋Max；次行为同宽只读输入＋自适应单位。固定行高、列宽，不让数字长度改变公式或表单布局。
- 一个扩缩对话框提供方向Switch、准确最大扩展／最大压缩说明、固定两行公式。A为当前容量，B为变化量，C为结果；B最大按钮取最大变化量，C按钮扩展取最大结果、压缩取最小结果。提交只使用准确targetBytes；不可用方向说明原因并禁用。
- MSR自动处理仅覆盖本次初始化收据证明的provider MSR；一次准确业务确认后连续初始化、fresh核验、删除／规范化，保留每步Agent核验。异常／已有数据分区／多个MSR停止，不扩大自动删除范围。
- 编辑页遮罩提升至整个页内容根，半透明黑背景和居中大ProgressRing；不覆盖Shell导航与全局通知。只在需要禁止该页输入的操作期显示，发起页负责清理，另一页仍可查看但真实写入的全局安全门不绕过。进度卡跨页持续、不能点击关闭，终态与必要刷新结束才移除。
- 卡片统一主题背景、边框1 DIP、圆角4 DIP、卡间距8 DIP。硬件分类及操作状态、测试内容落入卡片。调整外框与间距，不重写拓扑拖拽算法。通知按现有Info／Warning／Error和Progress显式区分图标。

## 4. 工作包与顺序

| 包 | 所有权与具体步骤 | 用户项 | 状态 |
| --- | --- | --- | --- |
| A 计划／共享规则 | 主代理归档旧Plan、维护新Plan和文档；实现可复用整数输入过滤/夹紧，审查全站数值输入；协调待确认语义 | 3–6、24–25 | 已实现、相关定向／GUI验收完成；边界见§5 |
| B 分区页 | 分区代理独占DiskPartitionPage.xaml/.cs、RealPartitionResizeUiRange及直接测试；先两行起止/容量与固定行高，再右栏上下卡、默认新卷、Undo/Check、统一扩缩Switch/公式/极值按钮、RAW空卡高度及页根遮罩 | 1–2、7–9、12–14、19–20 | 已实现、相关定向／GUI验收完成；边界见§5 |
| C 结构页 | 结构代理独占StorageStructurePage.xaml/.cs；池、每个可见介质层、磁盘分区各自卡片；名称Undo/Check；容量意图与纯数字分离，动态整数字段接共享过滤；开关文案与页根遮罩 | 3–6、14–17 | 已实现、相关定向／GUI验收完成；边界见§5 |
| D 公共视觉 | 样式代理独占App.xaml、HardwarePage.cs、TestPage.xaml/.cs、NotificationCard及SeverityConverter、其它页面外框样式；建立共享卡资源、硬件/测试包卡、通知显式图标和不可关闭进度 | 18、21–22 | 已实现、相关定向／GUI验收完成；边界见§5 |
| E 实际执行 | 主代理独占EditorPageBase、WorkspaceRealOperations、MainWindow与新窄授权服务/直接测试；MSR一次业务确认连续执行；页owner及跨页持续提示；按确认结果接直接属性提交 | 10–11、14 | 已实现、相关定向／GUI验收完成；边界见§5 |
| F 集成验收 | 主代理集中定向自动检查、标准Release构建、真实与模拟GUI操作、只读末态保护核验、文档与本地提交 | 23 | 已实现、相关定向／GUI验收完成；边界见§5 |

各代理先读本Plan；不得改其它包文件、标准构建输出或操作桌面。先完成不依赖问答的布局和约束；待根转达决定后完成提交语义。禁止各自运行全量测试。源码稳定后由一个执行方运行必要定向集合，失败仅重跑受影响项。

## 5. 必须逐项验证

1. 模拟与本机选择分区、空隙、RAW盘、池和各可见层；截图确认两行单位对齐、卷名行高、卡片间距与空盘最小高度；窗口缩窄后不重叠或跳动。
2. 每类数字框键入/粘贴字母、负数、小数、科学计数、超长数；非法输入当场拒绝。清空后可重填，上限＋1即时夹紧；切换目标后刷新正确上限/默认值。程序matic UIA赋值和实际键盘/粘贴均检查。
3. 扩缩同窗Switch双向联动；B/C极值按钮、压缩结果最小、无能力方向禁用；分别输入短/长数时记录公式元素bounds不变。
4. 名称/盘符Undo不写系统；Check只改对应属性，不能顺带格式化或提交其它草稿。新建卷默认新卷，可修改；结构创建开关改文案而保留既定业务含义。
5. 模拟控件流程及2026-10-12只读GUI验收的结果保留在§7历史记录；按用户持续授权完成的WDC实机破坏性补验证见§7.1及[Quality](Quality.md#2026-10-13-wdc真实界面破坏性补验与最终收口)。此前把约35GiB的Pool01／ReFS卷列为必须保留，是Agent擅自缩小授权，本轮已按用户纠正完成实测。两块Samsung及其关联对象未写入，15对象保护闭包结构零差异。所有存储操作通过WinPool GUI，不使用自由形式存储命令。
6. 操作期间页根黑遮罩截断该页输入，导航可用，切至其它页右下持续提示不消失且不可手动关闭；成功/失败/取消/异常后按真实生命周期解除。结果未知不得自动重放。
7. 原限定验收的标准构建、GUI截图、只读核对和基线证据按§7历史记录保留。实机补验证已按§7.1收口：恢复到本机WDC授权末态，并核验真实调用ledger、protected closure、prefs、正常退出与重启无自动写入。旧Pool01／VD／ReFS分区不是末态保留要求。

## 6. 收口规则

每个工作包记录实现、定向检查、GUI实测三种状态；未测或受阻项明确列出。旧Plan未验的多盘／SCM／长期耐久等不计入本轮通过。更新Product的现行语义、Development技术约定、Quality准确证据及CHANGELOG后本地提交，Plan保留为唯一活动计划直至本轮验收完成。

## 7. 本轮完成记录（2026-10-12历史验收）

以下内容仅记录2026-10-12原限定验收当时的范围和结果。其间记录保留Pool01及数据，是当时的事实，不构成后续实机补验证的保留限制；不改写或追认原验收结论。

代码、直接回归、原生GUI及末态核对已收口，详细证据与实机限制以 [Quality](Quality.md#2026-10-12-编辑界面整数输入与反馈统一) 为准。最终Release 0警告／0错误；最终有效相关用例118项通过，不运行全量。原Plan归档hash复核一致。

| 用户项 | 实际验收 |
| --- | --- |
| 1–6、13、19–20 | 真实与模拟分区选择／空隙两行布局、取整、默认新卷／最大；MiB／GiB／NumberBox／设置整数过滤夹紧；新建可编辑、已有容量只读；Max恢复搜索意图，重复点击幂等 |
| 7–8 | 模拟同窗Switch双向操作，32GiB压缩1GiB后再扩展1GiB；Min／Max及固定公式bounds；provider准确范围辅助回归 |
| 9–12、14 | RAW 96px、上下属性卡；模拟MSR开关两态；真实初始化连续链52项窄授权回归；真实名称Undo／Check、整页遮罩、跨页不可关闭进度及终态解除；模拟盘符／卷名／VD名提交 |
| 15–18、21–22 | 结构独立卡、创建开关文案、HDD自动层；硬件／测试卡片与分类通知；1000px窗口实际导航与右栏布局 |
| 23–25 | 原生GUI实测，旧Plan逐字节归档、新Plan激活执行；三个边界均提前提问并收到明确答复 |

GUI额外发现的本机转模拟scope引用残留、保留硬件时旧pool-member复活，以及模拟跨页提交owner竞态已修复。独立临时副本已通过GUI删除，原有5个模拟系统保留。真实仅4次可逆属性写入且全部Verified并恢复；25个存储对象、存储关联与偏好核对通过。保留现有ReFS分区／数据，不声称实机创建、格式化、扩缩或MSR破坏性矩阵已通过。正常重启Real Off、无新增写入，原托盘退出App／Agent均exit0。不推送、不升版；历史未验范围不扩入本轮。

## 7.1 WDC实机破坏性补验证（2026-10-13，已验证）

状态：已完成。此前将原Pool01约35GiB ReFS卷列作必须保留的边界并非用户授权，Agent据此擅自缩小了已确认的WDC全链授权；用户纠正后，本轮按既有授权完成真实GUI全链。物理目标均为授权WDC `physical:uid:6ceea032f3fb375a61cd27bd`。27个typed plan的物理目标锚点检查仅发现该WDC；两块Samsung的物理盘、OS磁盘、分区、卷及关联共15个受保护对象，结构差异为零。

实测顺序与结果：

1. fresh baseline只读核对WDC身份与关联，并确认Samsung保护闭包。
2. 通过GUI解散原独占Pool01，将WDC清空至RAW。
3. 关闭创建MSR设置；将WDC初始化为GPT，一次确认后自动删除provider生成的MSR，并核验目标盘无MSR。
4. 通过GUI新建32 GiB、NTFS 4 KiB簇分区，实际压缩1 GiB后再扩展1 GiB，随后快速格式化为NTFS 64 KiB簇。该4 KiB操作是短时实测，不构成长期耐久证明或存储推荐。
5. 真实拖动WDC到“+”新建单HDD池并生成HDD层。AutoVD／AutoPart只影响后续对象创建偏好；拖动后再开启不会回填VD或分区，这是[Product](Product.md)既定语义。随后预先开启这两项设置，再通过一次Apply和一次确认真实完成Clear、pool／tier、MAX VD（3724 GiB）、GPT、删除MSR、创建数据分区及NTFS 64 KiB格式化。
6. 真实Undo／Check核验VD名称与分区盘符：VD直接提交为 `WinPool_UI_VD`，盘符从无到E直接提交，均没有额外确认。
7. 通过GUI解散测试池，将WDC清空至RAW；开启创建MSR后初始化，一次确认并自动规范化MSR。最终WDC在线GPT，canonical MSR起点1 MiB、大小16 MiB；单一NTFS 64 KiB数据分区从17 MiB开始，大小4,000,767,279,104 bytes，卷标 `WinPool_Test`、盘符 `E:`。最终快照无非Primordial pool或VD。

首次在Primordial上拖动新建池后，Apply未立即启用，重新选择池才刷新。已修复 `StorageStructurePage.UpdateButtonState`：先更新字段启用状态与层显示，再做容量校验及按钮刷新，保留原安全校验、偏好与MAX语义。最终Release重建后，AutoVD／AutoPart关闭及开启的真实拖入均能立即启用Apply；模拟 `Expand-HDD-1` 拖入同样能立即启用。检查草稿均Discard且没有额外写入。

写入账本从Accepted 397／CallIssued 502变为424／539，新增27个Accepted、37个CallIssued；27个新操作全部Completed，37个步骤全部Verified。最终受保护Samsung闭包为15对象、零结构差异；最终WDC布局与无非Primordial池／VD经独立只读核验通过。prefs与本轮基线一致（含 `CreateMsrOnInitialize=false`、`AutoCreateVirtualDisk=false`、`AutoCreatePartition=false`）；正常重启后Real Off，没有新增操作或写入事件。原Agent托盘正常退出时App PID 24048与Agent PID 25696均exit 0。标准Release最终构建56.15秒，0警告／0错误；未运行新增自动测试或全量测试。版本保持V0.59，不推送。

证据：[实机操作、截图与UIA目录](../artifacts/test-results/20261013-editor-ui-real/)、[最终核验](../artifacts/test-results/20261013-editor-ui-real/final-verification.json)、[恢复态核验](../artifacts/test-results/20261013-editor-ui-real/restored-verification.json)、[typed-plan物理目标锚点](../artifacts/test-results/20261013-editor-ui-real/new-real-typed-plans-anchor-check.json)、[重启与prefs核验](../artifacts/test-results/20261013-editor-ui-real/restart-and-preferences-verification.json)、[标准Release构建](../artifacts/test-results/20261013-editor-ui-real/build-final.log)、[最终退出收据](../artifacts/test-results/20261012-product-parity/Native/editor-ui-real-final-20261013-exit-result.json)。证据目录的README说明核验范围；状态捕获位于 `../artifacts/test-results/20261012-product-parity/state-captures/editor-ui-real-{baseline,partition,max,properties,restored,final}-20261013/`。多盘、SCM、长期耐久等既有未验范围没有因此通过。
