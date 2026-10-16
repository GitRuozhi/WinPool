# WinPool 验证与验收

日期：2026-10-16。当前产品版本为 V0.60／迭代0（0.6产品线）。当前阶段只做相关定点回归和GUI验收；真实验证限于已授权WDC。下方各日期结果仅代表各自范围，不合并成当前版本的全量通过结论。

<a id="v060-compact-actions"></a>

## 2026-10-16 紧凑布局、待实施动作与实际属性基线

标准Release完整编译35.14秒、零警告／错误。GUI发现MAX文字裁切、属性卡内边距、成员推荐覆盖显式Mirror和数字框旧Maximum夹紧新建议值后，仅重建受影响App，最终17.15秒、零警告／错误。临时添加的输入焦点防护会破坏副本及手动列数提交，已撤回，沿用既有填表事件抑制机制；调试失败与原首次编译失败日志均保留。直接回归共150个不同最终有效用例通过：App原146项、新增布局保留1项、Application间距3项；最后策略8项通过，其余重跑不重复计数，已撤回防护的测试不计入最终结果，不跑全量测试。

真实GUI通过原拓扑拖动WDC建立64 GiB、Simple／1列／64 KiB分层VD，并在同一次Apply确认后完成自动GPT／删除provider自动MSR／创建数据分区／NTFS 64 KiB快速格式化。分区页再快速格式化为ReFS；实际卷为ReFS／65,536-byte分配单元，返回结构页显示ReFS、0待实施动作、Apply禁用。既有真实分区的结构页格式字段保持只读；恢复按钮改回实际值另在模拟Pool02的已提交ReFS基线上实测。新建VD自动分区、已有RAW VD不自动补建、显式删除VD后不回生也有直接回归。

真实与模拟动作清单均通过同一普通内容渲染器完全展开、仅滚动，无对话框内Expander；模拟标题按当前语言显示。MAX目标不显示内部0-byte哨兵。右栏实测中文380／英文420 DIP，标签84／112 DIP，值至少112 DIP；1440×900及1000×800英文窗口中标签至多两行，中文标签一行。各页外边距8/0/8/8 DIP、不可拖块间距6 DIP、分隔槽8 DIP；卡内边距与页外边距分开计量。完整条带为不可编辑输入框，显示`XX KiB + XX KiB`，解释保留在Tooltip和辅助功能信息。窗口截图曾受其它窗口遮挡，后续临时置顶WinPool取证，遮挡截图不算视觉通过。

模拟GUI核对4盘／2副本推荐2列，继续拖入至6盘保留Mirror并推荐3列，改成3副本推荐2列，手动1列不被副本数变化覆盖；最终通过见`complete-mirror-check.json`和`complete-*`截图。已提交ReFS改选NTFS后恢复为ReFS并回到0动作。跨页草稿动作数14→14；三页实际模拟VD脱机的图标和文字水平排列，随后恢复联机。软件中的研究性警示和额外ReFS提示已移除，真实数据损失确认及Windows错误仍保留。

WDC验证后解散测试池并经分区页清空，恢复本轮开始的RAW、零分区、无具体池／VD。全机磁盘、分区及卷所选稳定字段与开始一致，包含两块Samsung。新增10个真实计划全部Completed，12个普通步骤全部Verified；Accepted增加10、CallIssued增加12，没有MAX尝试或其它盘写入。模拟只增加修订计数与更新时间，原对象及属性恢复；临时草稿放弃、中文／原偏好恢复，最终本机结构页Real Off。详细过程、GUI、TRX、账本及保护对象比较见[本轮证据](../artifacts/test-results/20261016-ui-refinement/README.md)。本轮不声称多盘真实Mirror或其它硬件配置已实测。

<a id="v060-editor-topology"></a>

## 2026-10-14 V0.60拓扑与编辑交互

范围按[本轮Plan](Plan.md)；原Plan逐字节归档至[历史记录](Archive/20261014-v059-completed-plan/Plan.md)，SHA-256 `BB6F1C6B1D49EDD7D4FE8F9841174E3D8528D4DB4B97488F4EC4C29F7078E076`。Pool01两页不一致的[诊断](Review/Pool01-Topology-20261014.md)确认管理构图重复呈现模板与actual tier，属于软件显示分叉；准确Windows对象和创建参数一致，未重建池。

标准Release完整构建40.83秒、零警告／错误；GUI发现缓存续行边界后只重建App及直接测试项目，App构建31.71秒、零警告／错误；短卡状态说明布局调整仅重建App，22.27秒、零警告／错误；录屏发现选中描边覆盖动画后调整绘制顺序，最终仅重建App20.98秒、零警告／错误，并通过最终录屏复验。首次构建的两处Clip类型错误及级联XAML错误保留日志，按微软UIElement.Clip文档修正为RectangleGeometry；本机WinApp CLI缺少find-api命令，未安装／升级工具。

当前有效独立窄回归共**23项通过**：完整条带6项，草稿重入5项（包括隐藏页放弃后刷新，以及隐藏页仍有剩余草稿），业务层工作区11项，管理来源对象保留1项。第一次App集合9项之后仅重跑重入集合5项，其中3项重复，不累计为14项。未运行全量、耐久、多盘硬件或本阶段真实写入矩阵。

实际GUI已核对1440×900中英文、1000×800英文布局：中文项名一行，英文至多两行；默认侧栏520／568 DIP，项名112／160 DIP、项值至少144 DIP；属性卡独立折叠，顶部／下部字段同列对齐。真实Pool01两页均仅呈现实际HDD业务层Simple／1列／16 KiB／1926 GiB，管理页仍保留分区查看内容，原始详情仍有模板和实例两个Windows对象。

模拟实际拖入Expand-HDD-1生成新池草稿，直接修改名称、64 GiB容量、16 KiB条带单元后，拓扑和动作数立即更新；切管理页再返回时名称、容量、布局、MAX意图及Undo／Redo保留。Undo将16 KiB恢复64 KiB，Redo恢复16 KiB；操作清单移入只读弹窗，原固定网格、常驻清单和保存按钮均消失。确认放弃后草稿与历史清除，未应用到模拟存储结构。

健康对象悬停和选中已实际恢复；修改对象及Apply复用1 DIP两对移动描边。周期按稳定对象身份散列生成18–36秒的不同值，重绘不重抽。模拟RAW盘及含分区的VD联机／脱机实际切换，三个视图验证单斜链平铺、大小随卡片裁剪、父覆盖不重复画子覆盖、无底色大图标及原因文字；短卡采用横向图文，避免82 DIP卡片裁掉说明。只读／故障／失联图标完成SVG解析与共享加载路径检查，本阶段未分别制造这些故障做GUI矩阵。半透明纯黑通知在实际模拟操作中核对，进度及终态生命周期未改。

GUI发现的额外缓存问题已修复：页面隐藏期间放弃／Apply完成，缓存HasChanges和表单输入可能过期，导致返回后仍保留旧来源；现在以BindingGeneration／ContentRevision失效缓存并重新判断剩余草稿。隐藏页确认放弃→分区页模拟VD脱机→返回结构页的复验已显示最新脱机状态且0待实施动作。

只读末态比较保持本轮开始时真实Pool01、actual tier、VD、GPT／E: ReFS与两块Samsung的准确身份、布局和属性；真实Accepted／StepCallIssued／MaximumAttemptCallIssued增量均为0。模拟脱机测试结束后全部恢复联机。设置恢复中文、System主题及主题色、原AutoVD／AutoPart、MSR与监控偏好；最终GUI回到本机管理页、Real Off。详细证据在[本阶段目录](../artifacts/test-results/20261014-v060-editor/)，最终状态以该目录README和核验JSON为准。

<a id="real-pool-generalization"></a>

## 2026-10-13至14 新池成员与规格完整贯通（已完成）

用户确认的新池完整成员、按介质分层、合法布局与成员角色已接入原Apply流程，取代此前仅清理草稿锁的范围。范围与收口见[Plan §10](Archive/20261014-v059-completed-plan/Plan.md#real-pool-generalization)。最终标准Release构建29.26秒、0警告／错误；没有运行全量测试。定点失败和重试原日志均保留。

授权WDC的真实GUI完成Clear→池→16 KiB HDD模板（AutoVD关闭仍创建模板）、Hot Spare及拖回原池恢复Auto-Select、手动创建16 KiB MAX VD、GPT／自动删除MSR／创建数据分区／NTFS 64 KiB格式化、名称打勾直接提交、解散池及物理盘恢复。MAX重试在3725 GiB被拒绝、3724 GiB成功；actual HDD tier、VD和OS disk均为3,998,614,552,576 bytes，实际交错16 KiB。一次Apply只进行一次业务确认，完成后待应用动作清空，页面遮罩和持续进度正常结束；期间切换硬件页仍保留进度。

GUI暴露的问题均已修复：采集脚本CRLF／LF边界、主题资源查找崩溃、VD／解散／角色入口旧白名单、角色目标漏父池UID、未调用Windows却无法解除的错误锁定、角色结果说明漏项、同池恢复误调用成员迁移并重算provider容量、分层VD漏传共同布局。最后入口复测修正了Primordial误当已有真实池的判断，真实含数据WDC可拖入新草稿、拖回Primordial及Undo，恢复16 KiB／MAX草稿后Apply可用。分层漏参造成的旧MAX连续20次拒绝已停止并只读终结，无对象生成、无自动重放；修正参数后新的搜索两次即完成。该漏参为WinPool缺陷，未改用户算法及既有原生MAX微软侧结论。

定点验证覆盖类型契约、合法布局、全成员闭包、能力／范围、MAX及恢复、App规划／授权／结果映射和固定脚本。末轮追加有效结果为：角色目标与无调用恢复13项、确认／结果与容量说明29项、unused template投影4项、固定脚本布局参数19项。最后两项脚本夹具起初因首次模块加载的CLIXML进度噪声失败，夹具设置SilentlyContinue后2/2通过；原17项脚本用例已通过，不重复计算。此前相关定点TRX均保留，不把旧完整工程门当成本轮源码验证。

最终18个新真实计划全部只锚定WDC：16个Completed、2个经只读对账Failed，无未终结计划；Accepted增加18，普通步骤CallIssued增加23，MAX attempt CallIssued增加22（20次失败旧搜索＋2次修复后搜索），模拟提交未增加。CallIssued是账本边界，不代表每次均进入Windows：角色漏目标的失败有NoWindowsCall证明。Samsung物理／OS盘、分区、卷及对应身份关联与基线一致。WDC末态在线GPT，MSR起点1 MiB／16 MiB，数据分区起点17 MiB／4,000,767,279,104 bytes，NTFS 64 KiB、WinPool_Test、E:，无concrete pool／VD。偏好恢复（CreateMsr、AutoVD、AutoPart均Off）；原托盘正常退出App28276／Agent8308均0，正常重启Real Off且账本不变。末次Primordial入口修复后重编译并GUI核验，最终App29492保留用户16 KiB／MAX未应用草稿、Real On，未新增写入。

证据：[目录说明](../artifacts/test-results/20261013-real-pool-generalization/README.md)、[MAX候选](../artifacts/test-results/20261013-real-pool-generalization/vd-max-journal-summary.json)、[末态与Samsung比较](../artifacts/test-results/20261013-real-pool-generalization/final-native-verification.json)、[账本](../artifacts/test-results/20261013-real-pool-generalization/final-ledger-verification.json)、[正常退出](../artifacts/test-results/20261013-real-pool-generalization/normal-exit-result.json)、[最终构建](../artifacts/test-results/20261013-real-pool-generalization/build-fifteenth.log)。多盘、SSD／SCM、冗余布局和多层MAX没有本轮对应实机硬件结果，受控夹具和命令验证不能外推为设备或耐久验收。版本保持V0.59，不推送。

## 2026-10-13 新池草稿成员和属性限制清理

新池草稿不再继承来源盘的含数据属性锁；真实／模拟共用逐层容量、MAX、保存及复原交互，新池可设置和撤销热备／退役。模拟应用携带成员角色并排除特殊角色的数据容量；现有真实执行能力范围保留，不支持的配置仍可编辑，只限制Apply并说明原因，不显示伪造的HDD执行动作。

最终标准Release构建19.90秒、0警告／0错误。10项有效定点回归通过（Application 5、App 5）；首轮夹具缺少足够容量／canonical卷事实、角色拼写及测试using错误均已修正，原失败日志保留，不计为通过。未跑全量。GUI实际验证含数据真实SSD拖入草稿后容量／MAX／列数／交错可编辑，64 GiB／32 KiB保存重选保持，热备及Undo有效；模拟双HDD草稿Simple／2列、热备／退役与Undo有效。模拟Apply保留角色及容量的后态由窄回归验证；GUI只改草稿，全部Discard。最终运行版本再次核验真实SSD能力说明，只有拒绝原因、无错误HDD创建动作；WGC截图为黑帧，不计视觉证据，保留UIA及此前实际桌面截图。

只读核验21个非Computer存储／映射对象及15个Samsung保护对象结构不变，关联排序后相同；完整采集改变Computer根key，仅按同一SystemId及主机名规范化其引用，未忽略存储身份或关联。原未规范化、未排序的失败比较保留。真实操作463、Accepted 432／CallIssued 548及完整操作和写入边界行均不变，偏好恢复；WDC保持本轮基线脱机状态，未对其联机或写入。App20788／Agent24340从原托盘正常退出，均exit0；最终版本App22128已打开，本机磁盘分区页、Real On、无草稿，供用户继续测试。版本保持V0.59。

证据：[说明与定点TRX／日志](../artifacts/test-results/20261013-draft-restrictions/README.md)、[存储及偏好核验](../artifacts/test-results/20261013-draft-restrictions/storage-verification-sorted.json)、[托盘退出收据](../artifacts/test-results/20261012-product-parity/Native/draft-cleanup-after-gui-20261013-exit-result.json)。

## 2026-10-13 用户指定前端交互与视觉阶段收口（V0.59）

Plan §8的23项已按本阶段范围完成构建、窄回归与定向GUI验收，范围及截图见[证据说明](../artifacts/test-results/20261013-editor-refinement/README.md)。D盘当前为整数953,868 MiB；provider范围517060.078125–953868.69482421875 MiB，对应目标517061–953868 MiB、最大压缩436,807 MiB，当前不能扩展。查询曾被错误套用的Resize mutation安全检查在provider读取前拦截；修复仅移除只读查询的该项检查，保留身份／关系、本机来源、拓扑与provider边界核验。用户确认的Resize运行路径例外仅移出`MayInterruptRuntime`；Delete、Format、Offline及其余写入核验不变。synthetic Prepare/Preflight验证通过，未真实写入。

最终Release构建21.89秒、0警告／错误；有效窄测6项（Infrastructure 5项含替换的Resize boundary 1/1，Agent 1项），不跑全量。App两项fractional断言仅更新并编译，未执行。GUI覆盖MAX切换、拖动建单HDD层、Apply刷新、草稿撤销和三页离线遮罩；readonly／fault／lost映射及SVG仅作源码／资源核对，未伪造故障。只读[末态核验](../artifacts/test-results/20261013-editor-refinement/verified-final-state.json)通过：对完整GUI基线15个Samsung保护对象及全机22对象结构差异为零。最初普通权限基线只有14个保护对象，后续完整采集补入EFI FAT32/1024及一个volume，因此不声称14与15集合完全一致，原失败比较保留。`realOperations=461`、Accepted 430／CallIssued 545不变；偏好恢复任务基线、草稿Discard、模拟offline恢复online、真实开关Off，WDC保持Pool01及单HDD模板无VD。App 25700与Agent 4836正常退出（exit 0），见[收据](../artifacts/test-results/20261012-product-parity/Native/editor-refinement-final-exit-20261013-exit-result.json)。版本保持V0.59。

## 2026-10-13 WDC真实界面破坏性补验与最终收口

此前把约35GiB的原Pool01／ReFS卷列为必须保留，没有用户授权依据，是Agent擅自缩小已确认的WDC全链授权。用户纠正后，本轮按持续授权通过WinPool真实GUI完成补验。所有新增typed plan的物理目标均为授权WDC `physical:uid:6ceea032f3fb375a61cd27bd`；两块Samsung的物理盘、OS磁盘、分区、卷及关联共15个保护闭包对象，结构比较零差异。

真实路径包括解散原Pool01、清空WDC至RAW、关闭创建MSR设置后将其初始化为GPT，并在一次确认后自动删除provider生成的MSR，核验目标盘无MSR；再创建32 GiB NTFS／4 KiB簇分区，实测压缩1 GiB、扩展回1 GiB，快速格式化为NTFS／64 KiB簇。此4 KiB结果只覆盖短时创建、扩缩和格式化，不构成长期耐久证明或存储推荐。

真实拖盘在关闭AutoVD／AutoPart时创建池及单HDD层；拖入后再打开开关没有回填VD或分区，符合[Product](Product.md)规定的“仅影响后续创建”语义。随后预先开启两项设置，单次Apply和一次确认完成Clear、pool／tier、MAX VD 3724 GiB、GPT、删除MSR、创建数据分区及NTFS／64 KiB格式化。VD名经Undo／Check真实改为 `WinPool_UI_VD`，分区盘符经Undo／Check从无改为E，均无额外确认。

实测还发现首次从Primordial拖盘新建池后Apply未及时启用、需重新选择池才刷新。已修复 `StorageStructurePage.UpdateButtonState` 的刷新顺序，使字段启用状态和层显示先于容量校验及Apply按钮刷新，保留安全校验、偏好和MAX语义。最终Release重建后，AutoVD／AutoPart关闭与开启的真实拖入均立即启用Apply；模拟 `Expand-HDD-1` 拖入也立即启用。所有这些草稿均Discard，没有额外写入。

测试池随后经GUI解散，WDC清空至RAW并在开启创建MSR后初始化；一次确认自动规范化canonical MSR。最终WDC在线GPT，MSR起点1 MiB、大小16 MiB；单一NTFS／64 KiB分区从17 MiB开始，分区大小4,000,767,279,104 bytes，卷标 `WinPool_Test`、盘符E。最终无非Primordial池或VD。写入ledger从Accepted 397／CallIssued 502增至424／539：新增27个Accepted、37个CallIssued；27个新增操作均Completed，37步均Verified。所有typed plan物理目标锚定到授权WDC。最终核验、恢复态核验及15对象Samsung保护比较通过。

最终Release构建56.15秒、0警告／0错误；没有运行新增自动测试或全量测试。prefs与本轮基线相同，包括上次活动页 `LastActivePage=DiskPartition`、`CreateMsrOnInitialize=false`、`AutoCreateVirtualDisk=false`和`AutoCreatePartition=false`。正常重启后Real Off，无新增操作或写入事件；原Agent托盘退出收据显示App PID 24048、Agent PID 25696均exit 0。版本保持V0.59，不推送。

证据：[运行说明](../artifacts/test-results/20261013-editor-ui-real/README.md)、[截图与UIA目录](../artifacts/test-results/20261013-editor-ui-real/)、[最终核验](../artifacts/test-results/20261013-editor-ui-real/final-verification.json)、[恢复态核验](../artifacts/test-results/20261013-editor-ui-real/restored-verification.json)、[物理目标锚点核验](../artifacts/test-results/20261013-editor-ui-real/new-real-typed-plans-anchor-check.json)、[重启与偏好核验](../artifacts/test-results/20261013-editor-ui-real/restart-and-preferences-verification.json)、[Release构建日志](../artifacts/test-results/20261013-editor-ui-real/build-final.log)、[原Agent托盘退出收据](../artifacts/test-results/20261012-product-parity/Native/editor-ui-real-final-20261013-exit-result.json)。六组baseline、partition、MAX、properties、restored、final只读状态捕获位于 `../artifacts/test-results/20261012-product-parity/state-captures/editor-ui-real-{baseline,partition,max,properties,restored,final}-20261013/`。中文卷标的Python stdout曾受GBK解码误读；最终UI与文件系统回读正确。该卷标没有Unicode存储缺陷，未针对其修改代码。

## 2026-10-12 编辑界面、整数输入与反馈统一

本节及其“保留ReFS数据、未执行实机矩阵”的结果只记录截至2026-10-12原限定验收；后续授权的真实破坏性补验见上节，不覆盖或改写这段历史事实。

旧Plan逐字节归档，新Plan覆盖本轮25项要求及三项用户答复。最终标准Release构建27.56秒、0警告／0错误，日志 [build-final-accepted](../artifacts/test-results/20261012-editor-ui-build-final-accepted.log)。仅运行相关直接回归，最终有效用例去重 **118项通过**：分区扩缩18、整数策略13、初始化/直接属性/结构窄授权78、事实复制与关系保留9；没有跑全量。初次组合日志中的7个失败来自与canonical projection不一致的夹具，已修正并移除3个不能成立的状态；受影响初始化52项和整数13项分别重跑通过，旧失败日志保留，不能当成最终绿灯。证据见 [初始化](../artifacts/test-results/20261012-editor-ui-initialization.log)、[整数](../artifacts/test-results/20261012-editor-ui-numeric.log)、[事实回归TRX](../artifacts/test-results/20261012-scoped-copy-audit/dissolve-audit.trx)；分区18项来自执行console，未伪称有TRX。

实际桌面操作、UIA树与截图保存在 [GUI目录](../artifacts/test-results/20261012-editor-ui/)。逐项验证两行起止／容量、MiB截断、只读既有容量、新建默认最大及“新卷”、卷标32px行、RAW空卡96px、独立属性卡、Max文字按钮和创建开关文案；1000px窗口下通过导航菜单切页，右栏无重叠。各类整数框非法UIA赋值均拒绝，设置框另以实际字母键入和混合粘贴验证拒绝，容量溢出即时夹紧；NumberBox内部文本也过滤表达式并遵守动态上下限。扩缩同窗Switch在两个方向间切换，短／长值的公式bounds不变，压缩结果Min和变化量Max按准确MiB边界联动。硬件、测试页已实际查看卡片；通知Info／Warning／Error显式图标与Progress转圈分开。

独立GUI转换副本 `simulation:a84fe544fd534abba931bec9707f43fb` 实测清空、MSR开／关初始化、32GiB NTFS／4KiB创建、压缩1GiB再扩展1GiB、盘符与卷标Undo／Check、完整格式化选项及快速格式化为64KiB、VD名称直接Check。解散池及拖盘到“+”新建池区域后自动生成唯一HDD层，一次Apply完成池／MAX VD／分区创建；最后已通过原界面删除该临时副本，原有5个模拟系统保留。过程发现并修复两项真实采集副本特有问题：复制时scope／coverage仍引用本机ID；保留额外硬件事实时旧pool-member被重新合并，导致空池删除被拒。修复保留校验与硬件补充关系，实际文档内存重放及GUI重试均通过。临时副本末态、清理收据和原失败证据均留在GUI目录。

真实GUI卷标由空改为 `WinPool_UI_Check` 再恢复，池名 `Pool01` 改为 `WinPool_UI_Pool` 再恢复；Undo不写入，Check直接提交，无额外改名确认。发起页整页半透明黑遮罩／72px转圈可见，切到硬件、管理、测试或另一个编辑页仍有不可关闭的右下持续进度；另一页内容不被遮罩。完成刷新后解除。只读账本Accepted393→397、CallIssued498→502，4个新增真实步骤全部Verified。模拟跨页提交也遵守全局busy，防止第二页覆盖owner／提前清除进度。

本轮保留实际已有ReFS／4KiB／E:和约35GiB数据，**没有实机清空、创建、格式化或扩缩写入**；MSR连续自动清理的真实收据与目标范围由窄授权回归覆盖，不能把模拟初始化称作provider实机验证。两块Samsung及WDC链25个存储对象和存储关联、偏好恢复核验通过；仅排除实时剩余空间，并将完整硬件刷新改变的Computer根key规范化到其Subsystem父引用，未忽略磁盘／分区／卷身份或结构参数。见 [末态核对](../artifacts/test-results/20261012-editor-ui/editor-ui-final-20261012-verification.json)。[重启核对](../artifacts/test-results/20261012-editor-ui/editor-ui-after-restart-20261012-verification.json)无新增写入，Real默认Off；[最终托盘退出](../artifacts/test-results/20261012-product-parity/Native/editor-ui-after-restart-clean-exit-result.json)App／Agent均exit0。版本保持V0.59。

## 2026-10-12 打开后立即开启真实编辑：启动时序修复

补齐上一轮未覆盖的早期点击：管理员确认后异步等待 Agent 就绪；普通权限的 UAC 启动移出界面线程；Agent 尚未挂接 runtime 的关停最多等待10秒，取消／超时后不迟到关停；交接不保存未初始化的工作区和默认偏好。交接各阶段、响应码及异常保留准确诊断。旧现场只有 `continuation_timeout`，不能唯一确定旧窗口的失败步骤。

最终标准 Release 构建 **18.92秒、0警告／0错误、合并0碰撞**。直接回归 **25 Passed／0 Failed**（Agent23、Infrastructure2）；随后两行关闭后不触碰窗口、关闭入口后不因取消遗留关停的顺序保护经过审查及最终编译，未重复运行同一组测试。

最终 GUI 冷启动 App27132：启动后0.94秒切换真实编辑、1.63秒确认，当时导航禁用、工作区尚未初始化；等待期间连续原生窗口响应检查全部通过，6.92秒内导航可用且真实编辑 On。原存储结构页、Pool01 与两块 Samsung 已目视核验，原 Agent 托盘退出后 App／Agent 均 exit0。此前几次驱动等待管理员标题后才点击，实际已就绪，不算早期时序通过；修正为查找初始标题后取得本次证据。普通权限 UAC 在安全桌面等待，本轮未取得批准后的完整早期交接收据，不声称该 GUI 路径已通过；worker 阻塞与 pre-attach 关停由直接回归覆盖。

只读对账：420个真实操作、Accepted390／CallIssued493及完整操作／写入事件内容不变；App／Agent偏好恢复一致，Pool01和WDC六个对象相同。管理员采集增加一个卷事实，缓存对象24→25。未修改真实存储，未运行全量测试，版本保持V0.59。

证据：[直接回归](../artifacts/test-results/20261012-early-real-handoff/source/source-review-final.json)、[最终构建](../artifacts/test-results/20261012-early-real-handoff/final-release-build-closed-guard.log)、[早期点击](../artifacts/test-results/20261012-early-real-handoff/final-admin-observed-click.json)、[等待期间响应](../artifacts/test-results/20261012-early-real-handoff/final-admin-observed-wait.json)、[最终界面](../artifacts/test-results/20261012-early-real-handoff/final-admin-startup-real-on.png)、[原生退出](../artifacts/test-results/20261012-product-parity/Native/early-real-final-observed-exit-exit-result.json)、[只读对账](../artifacts/test-results/20261012-early-real-handoff/final-readonly-verification.json)。

## 2026-10-12 启动／退出与构建守卫：限定修复

现场确认两条启动故障：监控恢复约65秒超过App固定60秒等待，且Recovering拒绝退出；另一次普通权限身份采集60,037毫秒超时后，整个Agent被标为Failed，106秒时界面仍未就绪。修复恢复期有序取消／排空、明确恢复中继续等待，以及可选监控恢复失败保留诊断后正常Ready。自动失败不关闭监控偏好；原开关人工开启成功保存后可重试。

最终定向回归 **31 Passed／0 Failed**（Agent26、Infrastructure3、Monitoring2），覆盖退出取消、迟到回调、恢复错误、超时无session／writer残留、诊断投影与人工重试。原2条失败复现保留。最终标准Release构建 **21.01秒、0警告／0错误、合并0碰撞**；源码清单核对一致。构建运行树保留脚本通过；普通PowerShell准确识别管理员Agent并拒绝替换占用目录，跨权限查询不再只报无法核实进程。

原生验证恢复期托盘退出、启动中关窗后重开、就绪后的管理员重启交接、普通及管理员实例托盘退出；取得退出收据的App／Agent均exit0。最终构建普通冷启动54.18秒恢复可操作并正常退出，随后管理员交接、真实编辑on与Pool01界面已核验。最终构建本次没有再次发生原生采集超时，该失败分支由直接回归验证；恢复期提权交接没有成功原生收据，不计通过。采集耗时本身未优化。屏幕取证曾仅显示背景，调整窗口大小后取得完整画面，保留原采集文件。

只读对账：420个真实操作、Accepted390／CallIssued493及完整操作／写入事件内容不变，App／Agent偏好相同；Pool01、两层来源、VD、OS磁盘及WDC六个对象相同。管理员刷新增加一个卷事实，缓存对象24→25，不声称全部快照字节相同。未运行全量测试或执行真实存储修改，版本保持V0.59。

证据：[定向门](../artifacts/test-results/20261012-start-exit-repair/source/source-review-final.json)、[最终构建](../artifacts/test-results/20261012-start-exit-repair/final-release-build.log)、[原生超时现场](../artifacts/test-results/20261012-start-exit-repair/native/cold-start-failed-timing.json)、[恢复期退出](../artifacts/test-results/20261012-product-parity/Native/lifecycle-recovering-exit-result.json)、[关窗后重开](../artifacts/test-results/20261012-start-exit-repair/native/titlebar-close.json)、[最终冷启动](../artifacts/test-results/20261012-start-exit-repair/native/final-cold-timeline.json)、[管理员界面](../artifacts/test-results/20261012-start-exit-repair/native/final-admin-repaint.png)、[跨权限守卫](../artifacts/test-results/20261012-build-process-guard/nonadmin-live-guard.json)、[只读对账](../artifacts/test-results/20261012-start-exit-repair/final-readonly-verification.json)。

## 2026-10-12 产品交互回归修复：限定验收完成（V0.59）

本条与本页同日“审阅修复”F1–F4 的工程及原生验收分开记录。本轮四项产品交互修复的实现范围见[归档 Plan §10.11](Archive/20261012-editor-ui-baseline/Plan.md#1011-产品交互回归修复2026-10-12)：真实投影避免把同一层概念显示两次；新层字段恢复推荐与 Undo 基线分开；文件系统和簇大小候选贯穿计划、执行及后态；Popup 拖动改为固定 overlay 与有效 host 命中／释放。

写入实测使用的标准 Release 构建19.19秒、零警告／错误。终审另修复准确层选择后，[最终标准构建](../artifacts/test-results/20261012-product-parity/final-accurate-tier-release-build.log) **28.87 秒**，0 警告、0 错误、Union 合并0碰撞。定向最终有效组合 **901 项通过**：原862项，加最后67项直接回归，去除28项重叠；[组合证据及运行文件哈希](../artifacts/test-results/20261012-product-parity/engineering-summary.json)。这是跨运行组合，不是单次运行或全量测试。Backend 首轮526通过／10失败的证据保留；16项重跑覆盖9个原失败案例、1个替换错误独立分区簇字段夹具的缺卷簇案例及6个原已通过案例。最终有效集合不再计入被替换的旧夹具。

GUI 已分别核验真实与模拟拖入／取消及逐项恢复推荐按钮，检查了拖动帧和取消／恢复后的原字段。本次驱动使用 `SetCursorPos` 时，捕获期间的 WinUI 指针坐标没有更新；改用绝对 `mouse_event` 输入后，12帧拖入成功。移除临时诊断后的版本再次成功真实拖入：[稳定版界面证据](../artifacts/test-results/20261012-product-parity/final-real-stable-new-pool-release.png)。这些证据不表示模拟和真实的所有运行条件完全一致。

原 Apply 一次确认在 WDC 单 HDD 上创建 `Pool01`、64 GiB VD 与 NTFS4096 `Parity4K`，8 个内部操作均为 Completed／Verified。随后对既有分区执行 ReFS4096 `ParityReFS4K` 格式化，操作 `b4bb9539a035441888547c6b035b0af1` 为 Completed／Verified；这条既有卷路径不要求 RAW。独立 NTFS 证明 33 项断言通过：[证明文件](../artifacts/test-results/20261012-product-parity/ntfs4k-independent-proof.json)、[ReFS 操作记录](../artifacts/test-results/20261012-product-parity/Native/refs4k-format/b4bb9539a035441888547c6b035b0af1.json)。这些只证明对应创建和格式化的短时路径成功，不证明长期耐久；多盘、SCM、exFAT 大簇尚无设备验收。

普通物理 WDC 的 MAX 新建分区也实际完成NTFS4096及E:分配，随后原界面格式化回NTFS65536。终审：[16个新增真实操作全部Succeeded，23步全部Verified](../artifacts/test-results/20261012-product-parity/final-audit-runtime-ready/final-audit.json)，Accepted365→381、CallIssued459→482。释放池后的直接OS磁盘由前一已Verified操作的新鲜原始关系与准确硬件身份独立锚定；初次审计闭包缺口及后续快照移动失败均保留，没有放松目标限制。Samsung15个对象无差异，偏好恢复一致。WDC在线GPT，MSR起点1,048,576／大小16,777,216 bytes，数据起点17,825,792／大小4,000,767,279,104 bytes，NTFS65536、`WinPool_Test`／E:，无具体池或VD。末态部分截图受到桌面其它窗口遮挡，不将其作为完整视觉通过证据；末态以原界面字段、新鲜采集及回执交叉核验。

[原托盘正常退出](../artifacts/test-results/20261012-product-parity/Native/final-clean-exit-exit-result.json)App／Agent均退出码0，重启存储／硬件采集均Returned、Real默认Off，[重启账本与末态对比](../artifacts/test-results/20261012-product-parity/verifications/final-restored-to-final-after-restart.json)无新增操作或写入事件。最后只读复查修复了表单保存按介质取第一个层的残留：模板＋唯一actual只改actual，多个actual的歧义参数更新拒绝且不改对象；[67项直接回归](../artifacts/test-results/20261012-product-parity/targeted-accurate-tier-summary.json)含2个新增用例，证据明确来自工具console、不是TRX，也不冒充多actual的GUI验收。版本维持V0.59，整体Plan其余未验范围保持原状态。

最后准确层修补后的28.87秒构建已实际启动，原UIA核验Real开关、新池草稿创建／原确认放弃及回到RealOff；最终无待应用草稿、存储与硬件采集成功，[最终运行版本账本比较](../artifacts/test-results/20261012-product-parity/verifications/final-restored-to-final-runtime-ready.json)无自动写入。该构建额外拖拽复测因两次无法取得前台而未执行；没有绕过前台校验或将其记为通过。此前19.19秒构建的有效拖拽和真实写入证据保留，最后改动的验证范围另行列明。

## 2026-10-12 审阅修复：工程及限定原生验收完成

本轮针对 F1–F4 修复真实提交恢复和编辑状态衔接。F1/F2 将接受操作的恢复上下文保留到准确结果及作用域刷新完成后再映射草稿，避免迟到结果覆盖用户后续编辑；生产恢复协调器自动化回归覆盖30个故障场景。F3 允许在同一池中按准确关系显式删除一个受支持的旧虚拟磁盘，再于删除验证和新鲜空池核对后创建替代项；未分配 HDD 层模板仅接受零个成员或唯一准确池成员，保留旧盘、附加第二个虚拟磁盘及不支持的结构仍拒绝。F4 在准确目标匹配、界面填充保护和可证实无写入终态下恢复事实盘符选项；未知结果及成功但尚未同步的结果继续保持只读。终态身份历史保留128项，防止同一身份的迟到非终态响应重开旧操作。

11个非 App 测试项目共 **1,863 项通过、3 项既有测量跳过、0 项失败**；WinPool.App.Tests 单独运行269项通过，随后 GUI 发现的后台刷新通知缺口增加4项定向回归并通过，合并为 **2,136 项通过、3 项跳过、0 项失败**。这是一项目一最终结果加4项新用例的组合证据，不声称一次运行273项 App 用例；相关架构回归1项重测通过，不重复计数。证据见[非 App 全量日志](../artifacts/test-results/20261012-review-repair/full-tests.log)、[App 测试日志](../artifacts/test-results/20261012-review-repair/app-final-native-fixes/WinPool.App.Tests.log)、[新增刷新测试](../artifacts/test-results/20261012-review-repair/editor-inventory-refresh-tests/)和[工程汇总](../artifacts/test-results/20261012-review-repair/engineering-summary.json)。报告引用的16条回归规格附件未随本地报告提供，不将未见规格或未做的原生故障注入计入通过数。F1/F2 故障证据来自直接驱动生产协调器的自动化测试，正常与取消路径另作原生验证。

[最终标准 Release 构建](../artifacts/test-results/20261012-review-repair/final-inventory-fix-release-build.log)17.88秒，0警告／0错误，运行树合并0碰撞。原生发现并修复两个追加缺口：删除旧分区后新 VD 的预览不得复用旧卷标；后台同系统采集必须通知原编辑页重新绑定空闲基线。后者不重新导航、不忽略版本冲突，原生启动后首次草稿 Apply 已正常到达确认并取消：[最终确认截图](../artifacts/test-results/20261012-review-repair/final-background-refresh-first-apply.png)。

原生默认 MAX 创建成功，实际 HDD 层／VD 为3724 GiB，自动布局完成。清盘后下一冻结段曾因 post-fingerprint 不匹配在 Accept 前安全取消；原剩余草稿经用户原 Apply 重新确认后完成，没有自动重放或放宽后态核验。收据只保存哈希，当前证据不能确定具体漂移字段，不将它重新归因于已修复的物理分配计数。同池替换以一次确认连续完成删除、64 GiB新 VD、GPT、移除 provider MSR、创建数据分区、NTFS64 KiB格式化六个操作，新卷标实际为 `ReviewReplacement`，界面无待应用动作：[结果截图](../artifacts/test-results/20261012-review-repair/same-pool-replacement-verified.png)、[事实快照](../artifacts/test-results/20261012-review-repair/state-captures/replacement-verified/local-snapshot.json)。MAX 路径在仅增加输入日志的临时诊断 DLL 上完成，后端源码相同；该诊断已从最终源码与 DLL 清除，同池替换及后续验收使用无诊断构建。中间一次保留旧时间戳导致增量构建残留诊断的日志不作最终运行树证明，完整经过保留于工程汇总。

最终原生盘符 E→F 在确认取消后恢复 E，再次选择 F 可重新到达确认；三次最终取消的 Accepted／CallIssued 均保持341／433，没有“改回”调用。第一次基线 GUI 已复现旧版取消后仍显示 F。本轮新增26个真实计划，19个 Accepted 全部 Succeeded、24步全部 Verified，7个接受前 Cancelled 均无写；详细目标、命令及逐步证据见[最终审计](../artifacts/test-results/20261012-review-repair/final-audit/final-audit.json)。WDC 准确硬件身份恢复在线 GPT：MSR 起点1,048,576／大小16,777,216 bytes，数据起点17,825,792／大小4,000,767,279,104 bytes，NTFS／65,536-byte簇、`WinPool_Test`／E:，无具体池或 VD。Samsung15个关联对象闭包无差异，App／Agent偏好与基线完全相同。[末态截图](../artifacts/test-results/20261012-review-repair/final-restored.png)、[再次选择确认](../artifacts/test-results/20261012-review-repair/final-f4-repeat-confirmation.png)。

[最终原托盘退出](../artifacts/test-results/20261012-review-repair/Native/final-clean-exit-exit-result.json)确认 App／Agent 两进程均退出码0。重启保持真实编辑 Off，无自动写入；完整存储／硬件采集及账本比较另留[重启证据](../artifacts/test-results/20261012-review-repair/state-captures/final-after-restart/)。[依赖漏洞检查](../artifacts/test-results/20261012-review-repair/package-vulnerability.log)退出码0，23个项目未发现易受攻击的包。旧审查归档90个原文件逐项SHA-256及Git blob核对通过，最新审查原文保留。本轮限定验收完成，整体 V0.59 其余未验范围维持原状态。

## 2026-10-11 真实 MAX 核对误锁与编辑恢复：限定验收

前轮真实验证只覆盖草稿和属性重置，没有实际提交默认 MAX 创建及分区修改；该覆盖缺口不能作为这些操作可用的证据。本轮复现用户现场并修复 MAX 指纹规则分叉和界面恢复漏接终态的问题，未扩大真实硬件支持范围，未运行全量测试。

定向回归 **104/104 通过**：Infrastructure 29项、App 73项、Application 2项。证据：[MAX 恢复](../artifacts/test-results/20261011-v059-operation-lock-repair/targeted-infrastructure/maximum-recovery.trx)、[界面恢复与自动布局](../artifacts/test-results/20261011-v059-operation-lock-repair/targeted-app/app-recovery.trx)、[准确草稿身份](../artifacts/test-results/20261011-v059-operation-lock-repair/targeted-session/session-recovery.trx)。[标准 Release 构建](../artifacts/test-results/20261011-v059-operation-lock-repair/release-build.log)38.92秒，0警告／0错误，运行树合并0碰撞。

原现场未知操作 `635a2328` 经生产只读恢复为成功；Accepted306／CallIssued386不变，步骤表哈希不变，没有重放调用。随后原界面拖入 WDC 自动生成 HDD 层，接受默认 MAX 的完整结构 Apply，得到3724 GiB实际层／VD、GPT无MSR、NTFS64 KiB自动分区。实际卷标改为 `WinPool_Repair_Check`，再压缩64 MiB，容量从3,813,374降至3,813,310 MiB；各次操作后入口正常恢复。[自动分区事实](../artifacts/test-results/20261011-v059-operation-lock-repair/new-max-applied-local-snapshot.json)、[卷标修改](../artifacts/test-results/20261011-v059-operation-lock-repair/partition-renamed-local-snapshot.json)、[压缩事实](../artifacts/test-results/20261011-v059-operation-lock-repair/partition-shrunk-local-snapshot.json)及[实际界面](../artifacts/test-results/20261011-v059-operation-lock-repair/partition-shrunk.png)。准确身份匹配、终态查询和自动布局恢复有定向回归；本轮未额外注入实机未知故障。

[末态终审](../artifacts/test-results/20261011-v059-operation-lock-repair/final-verification.json)确认本轮16个真实操作／23步全部成功／Verified，0未终结真实操作；WDC在线GPT、规范16 MiB MSR、其余最大整MiB BasicData、NTFS64 KiB、`WinPool_Test`／E:，无具体池或VD。Samsung限定结构15对象[比较通过](../artifacts/test-results/20261011-v059-operation-lock-repair/protected-comparison-final.log)，[末态界面](../artifacts/test-results/20261011-v059-operation-lock-repair/final-selected.png)已检查。MSR默认关、AutoVD／AutoPart开、监控开／20Hz均恢复原偏好。[原托盘正常退出](../artifacts/test-results/20261011-v059-operation-lock-repair/normal-exit.json)App／Agent均0。

普通重启时Real默认关，随后通过原开关重新开启供用户测试；[退出前账本](../artifacts/test-results/20261011-v059-operation-lock-repair/final-before-exit-real-audit.json)与[重启后账本](../artifacts/test-results/20261011-v059-operation-lock-repair/after-restart-real-audit.json)的343 plans／441 steps／2,682 events三表哈希完全一致，Accepted322／CallIssued409不变，没有自动续写。未据此关闭V0.59整体Plan的其它未验项目。

## 2026-10-11 编辑流程审计残留修复：限定验收完成

本轮只收口五项已审计残留：新建草稿池共用 `poolSave` 保存链；模拟模式下 AutoPart 关闭保留 RAW／无分区状态，开启时按已保存的 MSR 偏好自动分区；真实文件系统、簇大小、快速格式化及重置状态指示对应准确目标和可核实状态；移除两个无调用方的辅助方法；修正操作界面用语。功能回归、原生界面、最终构建与限定末态证据均已通过。

App 定向回归 11/11、Application 最终新增用例 13/13及23项既有相关用例通过，合计 **47 项不同用例通过、0 失败**；初始12项新增用例由最终13项替代，不重复计数。证据：[App](../artifacts/test-results/20261011-v059-editor-flow-unification/app-targeted.trx)、[初始 Application](../artifacts/test-results/20261011-v059-editor-flow-unification/application-targeted.trx)、[最终 Application](../artifacts/test-results/20261011-v059-editor-flow-unification/application-final-targeted.trx)。真实新池草稿、保存／撤销／重做／放弃以及 E: 格式化表单切至 ReFS+完整格式化（未提交）、格式按钮禁用后经快速重置重新启用、文件系统重置和簇大小重置均已按原生界面核验；模拟池 MSR 开／关、AutoPart 关的最终 GPT+MSR／GPT无MSR／RAW无分区结果通过截图及 simulation-document 核验，三个池通过原界面清理并保留原 DataPool。详细 UI 记录见[证据目录](../artifacts/test-results/20261011-v059-editor-flow-unification/)。

初始标准 App／Agent Release 构建成功，用时25.77秒，0警告／0错误、Union merge 0碰撞；随后多项纯文案调整已包含于最终构建，17.87秒、0警告／0错误、0 collision。标准版本重启后原生 About 显示 V0.59，HelpText 与结构页已核对。原托盘退出时 App／Agent 均在15秒内退出，退出码不可用。最终只读审计确认真实操作三表319 plans、410 steps、2,479 events哈希与基线一致；Accepted299／CallIssued379不变，Samsung受保护关联对象15项比较PASS，WDC最终布局断言通过。见[归档 Plan §10.8](Archive/20261012-editor-ui-baseline/Plan.md)中的证据链接。本轮无真实存储写入；其它阶段余项仍未关闭。

## 2026-10-11 建池字段与确认摘要：局部验证

此前将本轮描述为追踪动态属性、容量保存、撤销／重做及完整 Apply 链，范围表述过宽。`732a9d8` 记录的是表单字段收缩、容量保存和确认取消等局部工作；该轮没有验证成员拖入、按介质自动生成层、拖动反馈或一次被接受的原 Apply 全流程。旧文字“普通布局保留其必要容量输入和用户已确认的布局选择”是当时记录，不是当前产品约束，现由最新用户决定取代。

结构页额外盘符、已有层名称输入及其独立 Enter 操作已移除；HDD 新建容量使用原容量层输入／MAX。准确 bytes 在显示、解析与确认间保持，不将模拟容量上限用于真实校验。结构 Apply 的直接授权测试覆盖一次完整目标／风险确认下，固定目标、参数、草稿代际及会话、冻结计划和 Verified OperationId／PlanHash／post fingerprint；失败／未知停止，独立磁盘操作不继承授权，重启不自动续写。这些 typed test 不证明真实拖动或已接受 Apply 的原生成功路径。

集中运行三个直接相关测试类，**115 Passed／0 Failed**，包括23条授权用例及10条精确容量新增 cases；没有全量门。最终标准 App／Agent Release 构建 **0 warnings／0 errors**、运行树合并0碰撞。原生 UI 核实 HDD 原字段默认 MAX、保存64 GiB时预览为68,719,476,736 bytes、撤销MAX／重做64、额外行消失。最终版本总确认列出准确 WDC身份、64 GiB、GPT、当前无MSR偏好、NTFS／64 KiB、名称与清盘风险；取消保留草稿，放弃后清除。本次首次草稿遇启动采集更新导致基线冲突，保护正常停止；两项自动采集成功后按新事实建立草稿完成上述检查。

只读前后审计：321个计划、531个步骤、2370条执行事件的行数及完整内容 SHA-256 相同；Accepted265／CallIssued339不变，旧 `76958…` 仍为PartiallyCompleted。本轮没有真实写入，收敛后的连续创建未作实机写入复验，不能将合成链测试或取消确认视为设备验收。标准版本已重启，真实编辑on、测试草稿已放弃；V0.58／迭代8及V0.59整体活动状态不变。

证据：[定向回归](../artifacts/test-results/20261011-pool-creation-alignment/targeted.trx)、[最终构建](../artifacts/test-results/20261011-pool-creation-alignment/final-release-build.log)、[最终容量保存](../artifacts/test-results/20261011-pool-creation-alignment/final-hdd-saved-ui.txt)、[总确认正文](../artifacts/test-results/20261011-pool-creation-alignment/final-confirmation-content.txt)、[原生断言](../artifacts/test-results/20261011-pool-creation-alignment/final-native-assertions.json)、[只读前态](../artifacts/test-results/20261011-pool-creation-alignment/before.json)、[只读后态](../artifacts/test-results/20261011-pool-creation-alignment/after.json)。

## 2026-10-11 按介质自动建层与原 Apply：限定范围验收

本轮限定验收已通过，整体 V0.59 仍在进行。建池按成员介质生成对应草稿层，不提供普通／单 HDD 布局选择；AutoVD 关闭时保留 HDD 模板而不建 VD，AutoPart 独立控制后续布局。模拟 HDD 新建、已有池容量／列数保存与撤销重做通过。模拟 SSD 拖入后形成 931 GiB Performance 层与 VD 草稿，DiscardAll 后原 DataPool 恢复；没有 Apply。SCM 本轮未原生检查，不据此声称全介质矩阵通过。结构 Apply 使用一次完整确认，独立磁盘操作仍分别使用各自当次确认。

主要原生证据：模拟 Pool02／Pool03 均以 64 GiB Apply，Pool03 无 VD；HDD3 拖入既有 Capacity 后成员数为 2。真实流程创建池 `pool:uid:cc9030b7111abe972424e87b` 和 HDD tier `tier:uid:80fec57b6e0790931ba6cce5`，随后创建 64 GiB VD（68,719,476,736 bytes）并完成 AutoPart，准确关联 WDC。WDC 最终恢复在线 GPT、1 MiB 起点／16 MiB MSR、17 MiB 起始／4,000,767,279,104-byte 最大整 MiB NTFS／64 KiB 数据分区，`WinPool_Test`／E:。重启后 Storage（10:33:07–09 +08）和 Hardware（10:33:10–16 +08）采集均 Returned；最终 `protected-comparison-final.log` 对 Samsung 15 个关联对象的几何、文件系统、簇、角色、挂载及对象闭包比较 PASS。canonical 归一定向回归 4 Passed／0 Failed；Apply 后拖动构建及最后仅同步提示文字的标准 App Release 构建均为 0 warnings／0 errors，后者 Union merge 0 collisions、exit 0（26.33 秒）。CreatePool HelpText 原生读取为同步后的新文案。托盘退出时 App／Agent 均 exit 0；重启后的原生结构页 Real Editing 为 on 且无待应用动作。未运行全量测试。证据：[模拟编辑](../artifacts/test-results/20261011-pool-drag-product-alignment/sim-edit-drop-ui.txt)、[SSD 草稿](../artifacts/test-results/20261011-pool-drag-product-alignment/sim-ssd-auto-layer-ui.txt)、[WDC 末态截图](../artifacts/test-results/20261011-pool-drag-product-alignment/restored-wdc-verified-final.png)、[重启后 Storage／Hardware 快照](../artifacts/test-results/20261011-pool-drag-product-alignment/after-restart-local-snapshot.json)、[重启后本机文档](../artifacts/test-results/20261011-pool-drag-product-alignment/after-restart-local-document.json)、[最终 Samsung 比较](../artifacts/test-results/20261011-pool-drag-product-alignment/protected-comparison-final.log)、[正常托盘退出](../artifacts/test-results/20261011-pool-drag-product-alignment/normal-exit.json)、[最终原生结构页](../artifacts/test-results/20261011-pool-drag-product-alignment/final-running-structure-ui.txt)、[真实池／层结果](../artifacts/test-results/20261011-pool-drag-product-alignment/verified-pool-tier-no-vd-ui.txt)、[64 GiB VD 与 AutoPart](../artifacts/test-results/20261011-pool-drag-product-alignment/verified-vd64-auto-layout-ui.txt)。

先前 CreatePool Prepare 指纹不匹配的唯一 canonical 差异为 `PhysicalDisk.AllocatedSize` provider counter 从 17,653,760 变为 1,801,766,920,192 bytes，是 Windows 清盘后的缓存计数更新，不是 Windows MAX 或层容量问题。实现只将该物理盘字段的 value 归一，同时保留存在状态和 `ReadState`；身份、几何、关系、健康／运行状态及 Pool／Tier／VD 的容量仍精确比较，App post-fingerprint 检查不变。详见[单字段 canonical diff](../artifacts/test-results/20261011-pool-drag-product-alignment/physical-allocation-counter-proof.diff.txt)。Apply 后拖动修正要求 finally 在 `_applyingRealDraft` 清零后刷新执行模式；修正后的[真实录像](../artifacts/test-results/20261011-pool-drag-product-alignment/real-post-apply-drag-fixed.mp4)显示 ghost 与源磁盘透明度，释放到目标外后[撤销／Apply 禁用且无待应用动作](../artifacts/test-results/20261011-pool-drag-product-alignment/real-post-apply-invalid-drop-ui.txt)。修复前失败录像仍保留：[real-edit-invalid-drop.mp4](../artifacts/test-results/20261011-pool-drag-product-alignment/real-edit-invalid-drop.mp4)。

WDC 测试池已通过原 UI 解散，末态按原 UI 恢复完成。重启后两类来源均重新 Returned，Samsung 最终保护比较再次 PASS。模拟 SSD 自动层／VD 仅验证草稿并 DiscardAll；没有真实 SSD 写入。当前模拟目录没有三层 SCM 夹具，本轮未重置用户模拟数据目录来造夹具，因此 SCM 未原生核验。完整字段出现／启用条件矩阵和失败／未知反馈全场景仍未验收；它们不否定本节 HDD／SSD 限定范围结果，也不阻塞本轮收口。全介质矩阵仍未完成，整体 V0.59 保持进行中。

证据：[最终提示同步 Release 构建](../artifacts/test-results/20261011-pool-drag-product-alignment/final-release-build-tooltip-sync.log)、[Apply 后拖动 Release 构建](../artifacts/test-results/20261011-pool-drag-product-alignment/final-release-build-after-drag-state.log)、[canonical 四案例](../artifacts/test-results/20261011-pool-drag-product-alignment/fingerprint-four-cases.log)、[池／层本地快照](../artifacts/test-results/20261011-pool-drag-product-alignment/verified-pool-tier-local-snapshot.json)、[VD 本地快照](../artifacts/test-results/20261011-pool-drag-product-alignment/verified-vd64-local-snapshot.json)、[canonical 根因摘要](../artifacts/test-results/20261011-pool-drag-product-alignment/physical-allocation-counter-proof.json)。

## 2026-10-10 编辑页额外入口移除：限定验证

两页查询／停止控件、事件、共用状态及专用 UI helper 已移除，结构页 unused HDD 层按钮及专属逻辑已移除。原草稿／Apply、Agent 删除模板能力、自动只读核对及取消冻结确认的 Stop／Query 保留。原虚拟磁盘按钮文案恢复，自动分区仍使用既有偏好。Product 和 Plan 中把额外技术入口写成产品设计的表述已纠正，历史变更记录保留并说明本次修正。

仅执行中英文反馈定向回归 **4 Passed／0 Failed**，覆盖不再引导点击旧按钮或手查 ID，以及开发消息仍保留错误码、操作身份和原证据。标准 App／Agent Release 构建 **0 warnings／0 errors**、运行树合并0碰撞。旧 App14224／Agent27960 原托盘退出均0；新标准 App9068 原生两页检查确认额外入口消失、原操作仍在，“创建虚拟磁盘／删除虚拟磁盘”在自动分区开启时保持原文案。启动 Storage／Hardware 均成功，真实编辑经原确认后 on。

只读前后核对：321个计划、531个步骤、2370条执行事件的行数及完整内容 SHA-256 相同；Accepted265／CallIssued339不变，旧 `76958…` 仍为 PartiallyCompleted。没有运行全量测试或真实存储修改，不关闭V0.59整体计划。证据：[反馈回归](../artifacts/test-results/20261010-editor-product-controls/feedback.trx)、[Release构建](../artifacts/test-results/20261010-editor-product-controls/release-build.log)、[结构页](../artifacts/test-results/20261010-editor-product-controls/structure-ui.txt)、[分区页](../artifacts/test-results/20261010-editor-product-controls/partition-ui.txt)、[开发页](../artifacts/test-results/20261010-editor-product-controls/startup-development-final.txt)、[只读前态](../artifacts/test-results/20261010-editor-product-controls/before.json)、[只读后态](../artifacts/test-results/20261010-editor-product-controls/after.json)。

## 2026-10-10 采集报告超限与诊断丢失：限定验证

数据库副本复现确认 collect／merge／project／snapshot_save 成功，`LocalInventoryDocumentCodec.Encode` 因 `The escaped document payload exceeds its IPC budget.` 失败。原缓存1476个来源中1440个为历史操作 scope 标记；旧合并一直保留每个 scope 的最新观察，完整成功采集没有淘汰其已覆盖的旧缺席证明。相同首次只读事实重放修复后的合并，来源1476→36、对象同为78，原始报告2,349,609→545,930 bytes，转义载荷3,185,256→835,197 bytes，原预算3,145,728 bytes不变。修复后 Encode／Decode 成功；没有重建或清空开发数据库。

仅执行直接回归 **29 Passed／0 Failed**：Application24（事实合并19、自动通知5）、Infrastructure1（手动诊断）、Agent3（两种异常及启动失败／取消）、Client1（事件详情往返）。同 scope 的不完整刷新首次暴露旧完整缺席证明被替换的问题，修复后最终门通过，初始失败 TRX 保留。失败／不完整／迟到采集和仍被字段引用的来源继续受保护；TimeoutException、provider诊断和失败阶段有界透传。Agent／Client两次运行未保存原 stdout／TRX，仅有执行方交接摘要，没有为补证重跑；现有诊断日志可选字段未单独测试。

标准 App／Agent Release 构建 **0 warnings／0 errors**、运行树合并0碰撞。旧 App2944／Agent12096 经原托盘退出均0；新标准 App3836 的原生开发页确认自动 Storage 与 Hardware 均成功，当前报告36个来源、78个对象、545,960 bytes，原确认后真实编辑开关 on。前后 Accepted262／CallIssued334、318个计划不变，旧 `76958…` 仍为 PartiallyCompleted；这些计数高于上一任务是用户随后已执行的操作，本修复未增加存储调用。未跑全量测试，未执行真实存储修改，不关闭V0.59整体阶段。

证据：[原异常](../artifacts/test-results/20261010-inventory-persistence-repro/exception.json)、[同事实大小对照](../artifacts/test-results/20261010-inventory-persistence-repro/payload-size-metrics.json)、[事实回归](../artifacts/test-results/20261010-inventory-persistence-repro/application-tests/fact-refresh-fixed.trx)、[诊断门交接](../artifacts/test-results/20261010-inventory-persistence-repro/diagnostic-tests-summary.json)、[Release构建](../artifacts/test-results/20261010-inventory-persistence-repro/release-build.log)、[原生最终收据](../artifacts/test-results/20261010-inventory-persistence-repro/final-native-receipt.json)。

## 2026-10-10 用户要求解除旧未知记录：开发数据收口

用户在反馈修复后明确要求解除阻塞。停止标准 WinPool 进程、保留完整数据库备份后，两轮生产只读采集确认准确 WDC／分区闭包稳定、存储 jobs 均为终态，旧写入进程已不存在。这些观察不证明完整格式化成功；本次依据用户明确放弃未知结果，使用现有 Repository CAS 将 `76958d530df843e083d1664a643fd74e` 的 format-volume 从 OutcomeUnknown 收口为 **Failed／NotVerified**，整体从 OutcomeUnknown 收口为 **PartiallyCompleted**。create-partition 保持 Verified，assign-letter 保持 Skipped；原目标及结果证据以原始字符串完整保留，新增证据明确记录用户要求、未验证完整格式化、禁止重放及核验收据。

全库前后比较通过：仅上述操作／步骤状态及格式化证据更新，原事件全部保留，只新增两条 `developer.explicit_unknown_closeout` 事件；Accepted **258**／CallIssued **328** 不变，生产 Repository 报告全局写入屏障为 false。标准 App 重启并完成原有确认后，原生 UI 的“真实编辑”开关显示 **on**，再次比较没有新增磁盘调用。未改产品自动恢复规则，未运行全量测试，未执行存储修改。一次性 host 首次因误读 PRAGMA schema 版本在 CAS 前停止；改为生产 `schema_info` 只读检查后成功，无初始化／迁移。

证据：[执行收据](../artifacts/test-results/20261010-explicit-unknown-closeout-host/execution-receipt.json)、[独立前后核对](../artifacts/test-results/20261010-explicit-unknown-closeout-host/verification-after.json)、[真实编辑开启](../artifacts/test-results/20261010-explicit-unknown-closeout-host/real-mode-ui.txt)。以下同日“保持 Unknown”的验证记录属于此前阶段，不代表当前阻塞仍存在；本次是准确旧记录的一次性开发数据处理，不是其它 Unknown 自动放行的先例。

## 2026-10-10 编辑残留与异常退出修复：限定验证

按用户要求，仅执行本轮修复的定向检查，没有运行全量测试、依赖审计或真实存储修改。定向回归 **37 Passed／0 Failed**：Agent 2、App 10、Application 11、Infrastructure 4、Persistence 10，覆盖模拟各介质层列数保存及兼容、用户确认摘要、容量显示、活动调用退出保护、持久未知结果退出、适配器错误诊断和停止进程后的修复构建门。测试首次编译问题修正后通过，初始日志仍保留。标准 App／Agent Release 构建 **0 warnings／0 errors**。

在当前开发数据库已有 `76958d530df843e083d1664a643fd74e`／OutcomeUnknown 的情况下，从原 Agent 托盘菜单执行“退出 WinPool”，App（2404）和 Agent（28004）退出码均为 **0**，随后无两进程残留。冻结计划与步骤证据 SHA-256 保持 `b7063a9c004c0a67068365666caeb13ef5c9d56a677170e104bc865b4dce7e0b`，未知状态保持 11；Accepted **258**、CallIssued **328** 均未增加。只读恢复和写屏障未解除，没有重放旧调用。旧运行树 Agent 因该缺陷不能退出，替换前已核实准确路径及无子进程后终止；最终成功证据来自新运行树的正常退出。

本轮未完成所有页面交互与设备场景的整体复验，不据此关闭 V0.59。起止值、按钮绑定与退役入口通过源码审阅和编译检查；原生检查仅验证上述退出路径。证据：[汇总](../artifacts/test-results/20261010-product-fixes-14e8d93a753e414cb66d6570f301938f/summary.json)、[构建日志](../artifacts/test-results/20261010-product-fixes-14e8d93a753e414cb66d6570f301938f/build.log)、[原托盘退出](../artifacts/test-results/20261010-product-fixes-14e8d93a753e414cb66d6570f301938f/Native/unknown-exit-result.json)。

## 2026-10-10 真实编辑拒绝与核对诊断：限定验证

修复真实编辑拒绝时只有通用“错误”标题、错误码未填入专用字段，以及后台只读核对原因丢失的问题。拒绝通知关联已恢复操作的准确目标、操作身份和原步骤证据；重启后恢复当前待核实操作的说明，不加载旧消息日志。最新核对诊断仅保存在 Agent 内存、随查询响应返回，不修改原状态／步骤证据／事件；同一恢复状态及诊断不变时不重复通知。

仅运行 **6 项定向回归，全部通过**（Agent 3、App 3），覆盖后台诊断、有界异常、证据不变、成功清理旧诊断、中文／英文提示、开发页字段及格式化未知说明；App 首次编译发现值类型空合并问题，修正后通过，初始日志保留。最终标准 Release 构建 **0 warnings／0 errors**，没有全量测试。实机开发页标题、详情中的 write_barrier／reconciliation 代码、准确 OperationId、原 ProviderError、WDC 目标与 RealOff 共 7 项文本／状态检查通过。

前后只读数据库比较完全相同：旧 `76958d530df843e083d1664a643fd74e` 保持 OutcomeUnknown，冻结计划与步骤证据摘要不变，Accepted **258**／CallIssued **328** 未增长。该完整格式化缺少可靠的结束及完成证据，**本次不新增释放屏障的恢复分支，真实编辑仍被这条未知记录保护性阻止**。没有新增真实存储写入。证据：[汇总](../artifacts/test-results/20261010-real-error-feedback-1791621932214/summary.json)、[最终构建](../artifacts/test-results/20261010-real-error-feedback-1791621932214/build-final.log)、[原生开发页详情](../artifacts/test-results/20261010-real-error-feedback-1791621932214/Native/developer-details.txt)。

## V0.58 单盘真实修改第一阶段（历史，已归档）

- **现场序列：** H00 基线及 H01–H11 适用操作已完成。包括分区/GPT/MSR、NTFS/exFAT/ReFS、EFI/Recovery、RAW 扩缩与删除、盘符/卷标、脱机/联机、建池、VD 与 HDD tier template、自动布局和分阶段重建。C03 因目标盘原已为 GPT 而 `not_required`；C05 现有 VD 与实际层实例扩展仍按边界条件禁用；D 类能力保持拒绝。
- **C02 与末态：** ReFS 1 GiB/65,536-byte cluster 卷扩至 2 GiB 后，同一 16 MiB 测试文件 SHA-256 保持一致；Shrink 属性及 UIA 均为禁用，候选卷随后删除。最终 WDC 为在线 GPT，16 MiB MSR 与分区页按对齐上限创建的 NTFS/65,536-byte cluster BasicData，卷标 `WinPool_Test`、E:。此处 MAX 指分区空隙几何候选，不是虚拟磁盘 `UseMaximumSize`。
- **保护盘：** H00 基线到最终布局、普通重启的 Samsung scoped comparisons 均为 0 changes、0 evidence gaps。范围仅限两块 Samsung 的关联结构；它排除有正面 `IsPrimordial` 证据的共享 aggregate 字段和非目标成员边，不代表全机结构无变化，也不能排除 Windows 后台 I/O。
- **工程门：** 稳定 V0.57 源码门共 12 个项目、1,450 项：1,447 Passed、0 Failed、3 NotExecuted（既有手工 archive/performance measurements）。Release 构建 0 warnings、0 errors；23 项依赖审计未报告漏洞。T01–T16 交叉表中的所选方法均在最终 TRX 中找到并 Passed。版本源随后提升为 V0.58；App/Agent 标准重建 0 warnings、0 errors，两个程序集 metadata 与 About UIA 均显示 V0.58。完整测试门没有在单独的版本号提升后重跑。
- **P5 与正常重启：** 原生 UAC 提权交接和 20 Hz monitoring 通过，交接期间 accepted 与 call_issued 未增长；普通启动 Real Off，App/Agent 正常退出。Application 日志查询得到 36 条 1000/1001/1026 候选，精确 WinPool 消息匹配为 0；这不确定历史 E_POINTER 的根因。

## 证据边界

- 最新严格目标审计保留 3 条旧记录缺少 `PhysicalMemberObjectId` 的字段缺口；每条仍保存准确 WDC UniqueId 和 serial，历史行未回填。
- 另一条历史 format-volume 失败步骤的 `target_json` 缺失，且该步 `call_issued=0`。有 preflight-failed 事件，但没有显式持久化 `NoWindowsCall=true`；因此不能把它改标为身份完整或已证明的 NoWindowsCall。最终审计原报告未被放宽或改写。
- `call_issued` 记录的是应用层发起边界，不单独证明 Windows 存储变更确已发生；历史目标字段完整性按严格审计结果保留。
- Persisted-inventory helper 不证明具体一次 ScanAsync 的因果；Samsung comparison 也无法排除后台 I/O。
- 最终门之后的版本号提升只由标准构建、metadata 和 About UIA 核对，不等同于完整测试门重跑。

第一阶段最终门和 H11 目标审计位于[历史测试证据](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/)。完整 H11 结构、操作 ID、几何、文件哈希与退出证据见 [H11 最终检查点](../artifacts/test-results/20261007-real-edit-stage1-9b7a6ce20531437aafe897e143628d43/runRoot/H11-final-closeout-checkpoint-20261007.md)。以下 V0.59 工作流对齐是独立的活动阶段，不改写这份历史结果。

## V0.59 工作流对齐的当前验收状态

截至 2026-10-09，产品版本仍为 V0.58，`docs/Plan.md` 中 V0.59 阶段仍处于实施与验收中。2026-10-08 的 223310 门与 2026-10-09 的 095540 门早于统一容量算法，保留为历史结果。当前 `WINPOOL-MAX-GIB-1` 已有冻结源码完整工程门及普通／单 HDD 实机边界结果；末态恢复、独立复核、重启、偏好和退出也已完成。本次证据限定于记录的 WDC/provider、受支持布局和 Samsung scoped 比较，不代表其它设备或多层 MAX。Plan 尚未关闭，V0.59 阶段仍未完成。

此前普通 VD 原生 MAX 曾通过原界面以 `UseMaximumSize=true` 创建 3,999,688,294,400-byte VD/OS disk，并完成 16 MiB canonical MSR、NTFS/64 KiB、W: 与 App/Agent 正常退出；这是统一算法实施前的历史路线，不代表当前 MAX 实现。此前的 `c2a5`/`d92` 使用明确 `StorageTiers + StorageTierSizes` 容量值；exact-template `StorageTiers + UseMaximumSize` 收到 Windows Storage cmdlet 的参数互斥错误 48010（导出未保留数值错误字段）；`MediaType HDD + UseMaximumSize` 则创建出未关联实际 HDD tier 的普通 VD，之后经生产 Query/CAS 定为 Failed/UnexpectedEffect，未重放。它们分别记录各自命令组合的结果，不足以推出普遍的 Windows 不支持结论。此前在创建前阻止分层 MAX 的临时 guard 也已被统一算法流程取代。当前普通和单 HDD MAX 均使用整数 GiB 搜索算法；本机结果与适用范围见下方。

当前 `WINPOOL-MAX-GIB-1` 以 `G = 1,073,741,824` bytes、`R = 4,000,000` bytes 计算 `C = floor((A - R - 1) / G)`。真实路径的 A 是 fresh provider 最大范围与物理容量预算中的较小值；模拟路径的 A 是应用布局折算并扣除已知物理占用后的逻辑余量。C 是起始候选而非保证的物理最大值。模拟单层直接采用 C；模拟多层逐层采用各自的 C，并以层容量之和作为 VD 容量。真实路径以 1 GiB 步长逐次尝试，并以相邻成功与容量拒绝界定结果。多层真实路径只覆盖既有受控 typed backend 的 Simple/Fixed、单列、64 KiB 且各层介质不同的布局；每层从精确 `0.5C` bytes seed 开始，seed 仅用于初始结构，不是最终 MAX。每层独立搜索到正整数 GiB 边界，VD 容量为各层结果之和。当前没有多盘 UI，也没有多层 MAX 实机通过证据。旧 Windows 原生 MAX 试验与当前算法结果分开记录。详见下方 2026-10-09 证据段。

### WINPOOL-MAX-GIB-1 当前验证进展

- **直接回归。** 较早的 [footprint 与 residual recovery 集](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/residual-recovery-direct/results.trx)为 16/16，[seed recovery](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/seed-recovery-fix/results.trx)为 2/2。之后 multi recovery 对正常 `PhysicalDisk.VirtualDiskFootprint` 增长的处理补充了[12 项黑盒回归](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/multi-recovery-footprint-final/results.trx)，全数通过；JSON 中 ObjectId 引号重编码兼容另有[3 项回归](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/recovery-encoding-focused/results.trx)。持久化恢复仍进行完整 fingerprint 比对并拒绝 OID/UID 篡改。以上自动测试不是独立设备证据。
- **当前冻结完整门。** [203701 summary](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203701/summary.json)覆盖 12 项目，**1,999 Total／1,996 Passed／0 Failed／3 NotExecuted**，`sourcesUnchanged: true`，restore 与 Release build 成功且 0 warnings／0 errors；23 项依赖审计复用前轮结果。较早的 [203059 门](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203059/summary.json)为 1,995／1,989／3／3，三个 recovery 失败对应后续修复与上面的 12/12 回归。三个 NotExecuted 是既有 archive/performance measurements，不计为通过。
- **普通 MAX 实测。** 原界面操作从 3,724 GiB 候选开始，3,724 与 3,725 GiB 成功，3,726 GiB 返回结构化 provider error 40000；据此，本机 WDC/provider 的观测边界为 **3,725 GiB / 3,999,688,294,400 bytes**，VD 与 OS disk 容量相等且无实际 tier。[原生断言](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Ordinary-Retry/native-assertions.json)为 18/18；[独立 review](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/ordinary-native-independent-review/review.json)的 22 项均通过、无 gaps。
- **单 HDD 分层 MAX 实测。** 准确候选 3,725 GiB 返回 provider code 1，3,724 GiB 成功；actual HDD tier、VD 与 OS disk 均为 **3,998,614,552,576 bytes（3,724 GiB）**，自动布局完成。[原生断言](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Hdd/native-assertions.json)为 17/17；[独立 review](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/hdd-native-independent-review/review.json)的 23 项均通过、无 gaps。两项实机边界只适用于所记录的 WDC/provider、Simple/Fixed 单列 64 KiB 受控路线，不构成其它设备的容量保证。
- **旧未知操作、恢复与最终退出。** 首次 C=3,724 GiB 创建后，`PhysicalDisk.VirtualDiskFootprint` 增长曾使后置检查超时。生产 Query/CAS 将旧操作 `b99641579fee485881c1b77bbd089633` 收口为 `Failed/stopped_without_maximum`；历史 child outcome `Unknown` 保留，未自动续写或重放。Query 未调用写 adapter，attempt 和其它记录未变，见[只读收据](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/closed-maximum-query-recovery-host/execution-receipt-encoding-fixed.json)。原 UI 操作 `39fa48fb19b74f0c81a59ab1f3e90429` 清理残留并 Verified：[记录](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Ordinary/cleanup-recovered-fresh/39fa48fb19b74f0c81a59ab1f3e90429.json)。
- **WDC 末态及重启。** MAX 后恢复的 WDC 为在线 GPT、规范 16 MiB MSR、`E:` 上的 `WinPool_Test` NTFS/64 KiB。[最终 native assertions](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-native-verification.json)为 11/11；[独立末态复核](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/final-native-independent-review.json)为 14/14。Samsung 限定结构比较 0 changes／0 evidence gaps。只读[操作审计](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-operation-verification-v2.json)364 项检查全 true；旧 `b996` parent 仍 Failed，child Unknown 未改写。普通重启后 Real Off／Monitor Off 20 Hz、B0 偏好一致、310 plans／256 Accepted／325 parentCall 计数不变、412 个源码文件哈希与最终门相同且无 unfinished operation；原托盘 Exit 的 App/Agent 均 exit 0，见[重启只读审计](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/after-final-restart-readonly-audit.json)、[退出后只读审计](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/after-final-exit-readonly-audit.json)、[重启 UI](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/normal-restart-ui.txt)及[退出结果](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/final-normal-restart-exit-result.json)。
- **未覆盖范围。** 没有多盘 UI 或真实多层 MAX 设备结果；旧原生 `UseMaximumSize` 路线、模拟测试与本次单 HDD 实测分开记录。V0.58 iteration 8 与活动 Plan 保持不变，V0.59 阶段尚未完成。

版本仍为 V0.58 iteration 8，活动 Plan 不归档。09:55:40 完整自动门仅是统一容量算法改动前的历史门；当前算法的冻结工程门及本机普通／单 HDD MAX 结果见上文。本轮调查与先前显式容量证据见[单 HDD 分层容量调查](Review/Single-Hdd-Tier-Capacity-20261009.md)。V0.59 阶段尚未验收。

### 自动门结果与证据范围

- **单 HDD 容量调查的新源码门：** [20261009-155245 summary](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/final-gate/20261009-155245/summary.json) 为12项目，**1900 Total／1897 Passed／0 Failed／3 NotExecuted**，前后源码哈希一致，Release构建零警告／零错误。该门覆盖分层准确模板范围修正、唯一实际层成员采集优化、写前采集失败NoCall及严格未知删除对账；不代表分层原生MAX已验收。依赖审计复用前轮23项目结果。此前[首轮直接门](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/direct-gate/Infrastructure/results.trx)保留811项中801通过／10失败：8条新helper测试被Windows PowerShell进度CLIXML干扰，2条生产只读采集超时。修正harness进度输出及真实VD已返回空布局字段的兼容后，[直接门v2](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/direct-gate-v2/)为Infrastructure814／814、Agent106／106；不叠加为完整门数量。
- **旧未知删除恢复：** `08f82c7e` 的[生产查询收据](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/closed-delete-query-recovery-host/execution-receipt.json)及[独立41项核验](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/Engineering/closed-delete-query-recovery-host/root-independent-execution-review.json)确认Failed／NotVerified、准确目标仍存在、两轮冻结闭包相同，adapter0、原Accepted及CallIssued各1；总273 plans／220 Accepted／277 CallIssued不变。原`adapter_exception`保存，明确不称NoWindowsCall、NoEffect或已删除。标准运行树曾被未知屏障阻止替换，因而仅此操作使用严格Query-only临时host；其构建准备失败日志及各版pin保留。解除屏障后才正常构建标准运行树。
- **本轮原UI与独立末态：** 原UI新计划afd9b476删除旧残留；03d3e149以明确3,997,809,246,208 bytes创建实际单HDD层，[同值断言](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/after-fix-ui-same-capacity-assertions.json)核实actualtier／VD／RAW OS一致，未套普通pool步长，未冒称MAX。33d18dd9解散三步Verified；再经原分区页清空、GPT、规范MSR和Data／Format／Letter恢复，相关计划均Verified且fresh scope完整。[新原生末态比较](../artifacts/test-results/20261009-single-hdd-capacity-0e6c6bea2bd143be90a4d2f5bfda04b6/final-native-verification.json)11项全通过，两块Samsung限定物理／OS／分区／卷结构0变化、0证据缺口；不代表全机无后台IO。正常退出及最终只读审计另列于调查报告。
- 早期第一轮聚合为 **1540 项：1530 Passed、7 Failed、3 Skipped**，使用本轮新增源码/测试之前的已编译结果；第二轮为 **1583 项：1577 Passed、3 Failed、3 Skipped**，三条失败分别涉及自动布局输出证据、分区磁盘预检和 pristine-root guard。两轮 TRX 保留原状态，均不能代表后续新测试结果。
- 中间定向 `device-gate-tests` 五个 TRX 合计 **433 Total / 433 Passed / 0 Failed / 0 NotExecuted**：Agent.RealService 15、App 124、Application.Session 15、Infrastructure.RealSafetyBackend 271、Persistence.BuildOperationGuard 8。该定向门不是完整 solution 最终门；TRX 文件夹名含 `device` 不代表这些自动测试写过真实设备。
- 较早完整工程门 [20261008-195646 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-195646/summary.json) 为 **12 项目，1622 Total / 1619 Passed / 0 Failed / 3 NotExecuted**；后续门 [20261008-202506 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-202506/summary.json) 为 **1655 / 1652 / 0 / 3**；再后门 [20261008-203645 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-203645/summary.json) 为 **1677 / 1674 / 0 / 3**，后续门 [20261008-210146 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-210146/summary.json) 为 **1714 / 1711 / 0 / 3**；[20261008-214608 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-214608/summary.json) 为 **1734 / 1731 / 0 / 3**。此前 [20261008-221503 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221503/summary.json) 为 **12 项目，1766 Total / 1763 Passed / 0 Failed / 3 NotExecuted**，392 个源码哈希项未变化。最后一轮旧源码完整门 [20261008-223310 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-223310/summary.json) 为 **12 项目，1775 Total / 1772 Passed / 0 Failed / 3 NotExecuted**，12 项目退出码均为 0，393 个源码哈希项前后相同，Restore/Release build 为 0 warnings/0 errors、标准 App/Agent 合并无冲突。本门早于 native MAX、后续恢复保护与 scoped-capture 变更，故仅保留为历史结果；三个 NotExecuted 是既有 archive/performance measurement，不计为通过。版本 V0.58 iteration 8；依赖审计复用已有 23 项目结果，未重跑。
- 新变更完整门的第一次运行 [095259 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095259/20261009-095259/summary.json) 保留 **1866 Total / 1856 Passed / 7 Failed / 3 NotExecuted**：七项 App 测试仍断言文案调整前的字面帮助字符串。同步断言后的最终门 [095540 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json) 覆盖 12 项目，**1866 Total / 1863 Passed / 0 Failed / 3 NotExecuted**，`sourcesUnchanged: true`（395 个源码哈希项），Restore/Release build 退出码均为 0，build 0 warnings/0 errors，标准 App/Agent 合并无冲突。三项 NotExecuted 仍是既有 archive/performance measurement。23 项目依赖审计复用本轮 [`Engineering/dependency-audit.json`](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/dependency-audit.json)，没有重跑；不应按 summary 中的相对字段另找审计结果。
- 与完整门分开的定向回归：[backend-complete TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-provider-uncertain/backend-complete/results.trx) 为 259/259 Passed，[capacity-intersection TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-capacity-intersection/capacity-intersection.trx) 为 93/93 Passed，[no-effect focused TRX](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/hddmax-provider-uncertain/no-effect-tests-final/results.trx) 为 42/42 Passed；这些是定向门，不叠加到完整门总数中。容量交集回归用准确 pool 与 HDD template provider 范围计算可用共同网格；这一代码/夹具结果不能代替真实设备以 Windows `UseMaximumSize=true` 参数创建成功。
- 该定向门实际包含并通过 7 个 P6/P8 服务方法：每次做新鲜 scoped 安全采集、失败/不完整范围不回退或复用旧事实、重启对账使用持久物理选择器、格式错误的 durable selector 保持 Unknown 屏障、无 scoped 能力时仍做 fresh full 检查，以及 observer 故障不把已核验写入变成 Unknown。前六项使用合成事实源，不能证明生产 CIM 遍历耗时或真实写入；最后一项也不等于监控联动已端到端验收。
- 较早定向门所记录的 Release 构建为 0 warnings、0 errors；复用的 23 项目依赖审计未报告漏洞。这些结果单独记录，不等同于重新执行依赖审计。

完整 T01–T18 方法映射及逐项原生 H/B 限制见[自动覆盖交叉表](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/acceptance-map.md)。下表归纳覆盖种类；“自动覆盖”是方法/服务夹具的断言范围，不代表页面控件或真实设备已经通过。

| T | 自动覆盖类别 | 自动证据的边界 |
| --- | --- | --- |
| T01 | `SimulationEditingSessionTests`、`RealStructureDraftPlannerTests` | 草稿拖放语义、撤销/放弃、冲突及零计划调用；没有实际页面拖拽端到端测试 |
| T02 | `RealStructureDraftPlannerTests`、`RealOperationUiFlowTests`、`MaximumCapacityAlgorithmTests` | 表单意图、名字、布局、精确容量/MAX 与偏好进入 helper/计划；共享整数 GiB 候选与线性搜索状态机有 Domain 覆盖。当前 MAX 不再保留 typed Windows `UseMaximumSize` 意图；普通与单 HDD MAX 的原界面链路已由本轮 18/18、17/17 原生断言验证，详见本节当前设备结果 |
| T03 | `SimulationEditingSessionTests`、`RealOperationUiFlowTests` | Enter 改名会话语义和 proposal；没有真实页面 Enter/失焦事件测试 |
| T04 | `RealVirtualDiskCreationRangeTests`、`WindowsRealPlanSafetyTests`、Agent planner、`RealVirtualDiskCreationCapacityResolverTests`、`MaximumCapacityAlgorithmTests` | 显式容量读取新鲜 provider 范围并校验；普通与单 HDD MAX 使用 `WINPOOL-MAX-GIB-1` 候选，不传 Windows `UseMaximumSize`；模拟直接采用 C，真实候选按 1 GiB 步长搜索，多层真实从每层 0.5C seed 起逐层搜索。已有虚拟磁盘/层扩展负例拒绝。当前完整门 1999/1996/0/3 通过；本机普通和单 HDD MAX 实测边界见当前设备结果，多盘 UI 与真实多层 MAX 仍未覆盖 |
| T05 | `RealOperationUiFlowTests`、`RealAutomaticLayoutProgressTests`、`WindowsRealPlanSafetyTests` | 普通/单 HDD 布局与分段输出通过 helper、planner 和服务断言；不等于原表单实机完成 |
| T06 | `RealStructureDraftPlannerTests`、`WindowsRealPlanSafetyTests` | 显式重建、删除前的已知创建能力预检，以及已知不支持时不调用 adapter 的负例；最终门通过了分区目标在计划清空后创建结构的回归。设备侧仍须验证原界面完整顺序 |
| T07 | `AgentRealOperationServiceTests`、`RealAutomaticLayoutProgressTests`、`RealOperationUiFlowTests` | 取消、部分完成、Unknown 停止及不重放；原生逐段取消路线仍需设备证据 |
| T08 | `RealOperationUiFlowTests`、`AgentRealOperationServiceTests` | 准备互斥、重连查询与 durable identity；不代替完整原生重连验收 |
| T09 | `SimulationEditingSessionTests`、`SimulationClearDiskTests`、Execution/Windows 安全测试 | 模拟 ClearDisk 独立关系变化及真实安全拒绝；夹具不修改真实磁盘 |
| T10 | `DiskPartitionPage` 静态路径审阅及辅助状态规则 | 没有自动测试实例化/驱动联机状态或分区表按钮；字段和禁用原因由 H07 原生验证 |
| T11 | `RealOperationUiFlowTests`、`WindowsRealPlanSafetyTests`、`SimulationClearDiskTests` | 精确 clear 目标、空 GPT 完整事实正例和未知/危险角色负例；模拟规则与真实设备结果分开 |
| T12 | `RealOperationUiFlowTests`、`WindowsRealPlanSafetyTests` | 公式/helper 双向联动、整 MiB 约束、provider stale-range 拒绝；没有实际 textbox 输入事件测试 |
| T13 | `RealAutomaticLayoutProgressTests`、`WindowsRealStorageBackendTests`、MSR safety inspector tests | 精确 MSR/provider 后态、偏好和拒绝续段；实盘 MSR 开/关结果由 B1/B2 核实 |
| T14 | `ScopedInventoryProviderTests`、`ScopedFactRefreshTests`、`AgentInventoryCoordinatorTests`、`EditWorkspaceTests` | 实际范围、闭包、合并、失败保留、代次与并发语义；另有 12 个显示回归 cases 验证 scoped dissolve 后准确 OS disk 投影和拒绝不可靠 fallback；合成 provider 测试不代表全机或所有设备性能 |
| T15 | `GlobalNotificationServiceTests`、`RealOperationUiFlowTests` | 进度生命周期和工作流屏障；没有图形遮罩控件、导航或窗口生命周期自动化 |
| T16 | `MonitoringEditTests`、`MonitorTargetIdentityTests`、`MonitoringSqliteStoreTests`、Agent observer tests | 目标身份、编辑缺口、恢复与持久状态夹具；监控运行中的真实编辑路线仍需 H10/B3–B4 |
| T17 | `RealStructureDraftPlannerTests`、`RealOperationUiFlowTests`、`RealOperationContractTests` | 65536-byte 参数意图、contract 和准确目标形状；不是页面输入到实际层的实机证明 |
| T18 | `IpcProtocolTests`、Agent 恢复测试、scoped merge 与监控持久化/轮换测试 | 帧、恢复 identity、代次和持久缺口语义；App/Agent 真实重连和采样恢复仍需 H12/H10 设备证据 |

### P6 范围采集与 P8 监控缺口

- P6 的真实安全读路径每次按准确 selector 获取新的范围事实；scope 只携带 selector/scope，不缓存安全事实。范围失败或不完整时拒绝，不静默回退 full、不复用上次结果；merge 只在完整覆盖域中证明 absence，范围外对象、关系及失败范围的旧值/观察时间须保留。新鲜事实还须通过代次检查，不能用较旧 full 覆盖较新的 scoped 结果。相应 7 个直接服务方法在最新定向 TRX 中的精确名称见[P6/P8 交接](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/documentation-handoff-p6p8.md)。
- 生产 `ReadOnlyProbe` 已修正。最新 `scoped-subsystem-fixed/target-proof.json` 将 scoped 与 full 捕获中的同一目标逐字段核对：物理盘 UniqueId/ObjectId、所属 StorageSubsystem UniqueId/ObjectId 及 fingerprint 均匹配，fingerprint 为 `bac984e6d81942c8de504684853e1af97d80491f7500afdfae3142df5e1428f6`。本次配对耗时 full **3293.1505 ms**、scoped **1966.4554 ms**；仅这一组顺序捕获不能外推到其他设备、所有 scope、整条 UI 链或安全写入时长。较早 full **2757.2775 ms** / scoped **1848.3027 ms** 的捕获仍作为历史测量保留，不与本次数据合并成趋势。
- P8 将监控数据库升至 schema 2。精确且有效的 schema 1 在事务内迁移并保留旧样本；损坏或未知 schema 拒绝迁移。编辑缺口按真实 durable step、准确稳定对象身份和实际恢复样本记录，不伪造终点或补造样本；迁移不重写旧历史 TRX，也不迁移旧 core 库中的监控行。
- P8 的 `MonitoringEditTests` 缺口/删除恢复用例、`MonitoringSqliteStoreTests` schema 1 迁移/损坏拒绝用例及轮换 multiset 用例在 `integration-tests` TRX 中有 Passed 记录；它们不属于 433 项定向 device gate。生产采样目标重绑、未受影响目标持续采样、实测 gap 关闭与重启/轮换端态尚不能由这些 helper/持久化测试替代。三个 archive/performance measurement 用例仍为 NotExecuted；小阈值轮换不等于生产 1 GiB 与完整 7-Zip 性能测量。

### H00–H12 原界面与当前设备批次

H00–H12 是[计划 §7.2](Plan.md)中的原界面设备验收路线，不是新增向导，也不能由 helper、源码字符串或筛选 TRX 替代。当前批次状态如下：

| 批次 | 结果 | 已知范围 |
| --- | --- | --- |
| B0 基线 | `passed` | H00 写前基线及现有 NTFS 卷改名、联机/脱机、NTFS 扩缩和文件哈希路线；改名计时有专门范围，不能推广为所有操作的延迟保证 |
| B1 无 MSR/RAW | `passed` | MSR 关闭、GPT 零分区/BasicData 不格式化、RAW 分区扩缩、删除及零分区 GPT 独立清空；NTFS 扩缩由 B0 核验 |
| B2 有 MSR | `passed` | MSR 开启后核对 1 MiB/16 MiB 唯一规范 MSR，再独立清空至 RAW |
| B3 普通结构 | `passed` | 已记录的普通批次有效：仅建池 `2fe1658c967b417e876ce51e45745dcb`、首个普通 32 GiB VD `17ca0eea11d9468e85b05f279cb217e0`（RAW、零分区）、池改名往返 `ed926ae7f37e470cb4eedc31e7f4b1f3` / `68cce52422af46b580def639d9fb370b`，以及原界面选 MAX 后把容量解析成数值并传 `-Size` 的普通 VD 创建/重建 [ordinary-max-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B3/ordinary-max-native-proof.json)。这里的“原生”指通过原界面执行，不是 Windows 原生 `UseMaximumSize=true` 参数。统一算法前的普通 `UseMaximumSize=true` 正例另见下方历史记录。重建两步解散 `b97b36a715614a1790b4d7101081e060` 均为 Verified；取消路线 `35a0109f703c4c42b8fb3b9ca777ab44` 的 `accepted_at_utc_ms` 为 null |
| B4 单 HDD 分层 | `passed` | 32 GiB HDD tiered VD 设备正向证据见 [hdd32-final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-proof.json)：`Interleave=65,536`、MSR false、GPT 数据区 NTFS/65,536-byte cluster。本轮统一算法下 3,725 GiB 候选返回 provider code 1，3,724 GiB 成功；实际 HDD tier、VD 与 OS disk 一致，自动布局完成，见[原生断言](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Hdd/native-assertions.json)及[独立复核](../artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/hdd-native-independent-review/review.json)。旧 `c2a5...`/`d92...` 明确 `StorageTiers + StorageTierSizes`、`a11...` exact-template 与 `UseMaximumSize`、以及 `b0...` MediaType 路线均保留为各自历史结果；这些旧失败不能代表新算法，也不证明 Windows 普遍不支持 MAX |
| B5 恢复末态 | `passed` | 本轮原 UI 写入的 WDC 在线 GPT、16 MiB canonical MSR、`WinPool_Test`/NTFS/64 KiB/E: 末态经 fresh native/full 对照复核；13 项末态断言通过（14 次只读查询），16 个 storage sources 的 typed facts 相等。Samsung scoped comparison 为 0 changes、0 evidence gaps，含 4 项共享 Primordial scope adjustments；41 个 staged 与 20 个 authority hashes 相符。偏好恢复、Real Off / Monitor Off 20 Hz 普通重启及原托盘退出也通过，细节与范围见下方本轮证据。旧 3 个 gap endpoints 仍为 Unknown，未被改写，也不再冒充当前监控告警 |

B0–B3 的既有批次记录仍为通过，不表示 H00–H12 整体验收完成，也不覆盖其他未执行的原界面路线。本轮 B5 末态和受保护对象 fresh comparison 已通过；H00–H12 原界面路线及批次对应关系如下，每个 H 点的通过状态须由对应现场记录判定，不能只凭方法名或批次名推定。

| H | 原生界面设备路线 | 对应批次 |
| --- | --- | --- |
| H00 | 写前基线与准入：准确 WDC/Samsung 关联、偏好、盘符占用、依赖和未终结操作 | B0 |
| H01 | 原结构页空草稿、拖入/移出、属性、撤销/放弃及 Apply 前零写入 | B3 |
| H02 | 普通单盘池/VD 创建及自动布局、明确容量与 MAX | B3；本轮统一算法普通 MAX 正例见上文 |
| H03 | 单 HDD 分层池/VD、模板/实际层和自动布局 | B4；32 GiB 正例及本轮 3,724 GiB 单 HDD MAX 正例 |
| H04 | 已有对象按 Enter 改名、失焦不提交及属性/反馈 | B0、B3、B4 |
| H05 | 原流程明确的普通与单 HDD 分层删除重建、取消与删除前拒绝 | B3、B4 |
| H06 | 两个原编辑页的视觉、起终点字段、窄窗口、拖动和滚动 | B5 旧运行的分区页视觉检查通过：中英文 480×700 属性/目标页、纵向滚动、中文 1440×900 E: 卷截图，以及扩展无候选提示和压缩公式只读展示；本轮清理后的末态未复拍。其余结构页/拖动场景仍按各批次核验 |
| H07 | 联机/脱机、清空至 RAW、MSR 关闭/开启初始化及 GPT 按钮状态 | B0、B1、B2 |
| H08 | 原界面真实扩展/压缩公式、范围边界及写后容量/哈希 | B0、B1 |
| H09 | 操作进度、图形遮罩、准确范围刷新和终态反馈 | 横跨各批次 |
| H10 | 监控运行中编辑、目标重绑、未受影响采样与真实缺口 | B3、B4、B5 历史范围（恢复 session 通过；历史 3 个 endpoint 保留为 Unknown） |
| H11 | 排除用户等待的分段时长/采集次数及前后比较 | B0 起记录；B5 配对是历史数据，见下方；不得外推到其他设备或所有操作 |
| H12 | 最终复采、WDC 末态、Samsung 比较、偏好恢复和正常退出 | 本轮 WDC native/full 末态复核、Samsung scoped comparison、偏好恢复和原托盘退出均通过；重启后 Real Off / Monitor Off 与 Accepted/CallIssued 计数保持基线。历史 3 个监控 gap endpoints 仍是 Unknown |

H04 的六次卷标往返已在 B0 留证，但不单独关闭其全部属性/反馈路线；B0 的改名时长也只覆盖该操作链。B3 原界面普通 MAX 创建/重建曾把候选值解析成数值后传 `-Size`；统一算法前的 Windows `UseMaximumSize=true` 普通 MAX 成功是另一条历史证据，不代表当前路径。B4 的 32 GiB HDD tiered VD 与本轮单 HDD MAX 正向观察均通过；先前明确 `StorageTiers + StorageTierSizes` 容量路线及两条旧 native MAX 路线的失败各自保留，不覆盖当前正例。临时 guard 已由统一算法取代；当前算法工程门及单 HDD 设备正例见上文，但多层真实 MAX 尚未实测。B5 的 WDC 物理最终布局、独立 native/full 与 Samsung scoped comparison、偏好恢复和 App/Agent 原托盘退出均已通过，详见本轮证据。历史 3 个 gap endpoints 仍保留为 Unknown，且不再冒充当前异常告警。H03/H05/H09–H10 依赖的分层、重建或监控联动检查继续按原路线判定；V0.59 全阶段尚未完成。

较早 H10 只读监控快照记录新 session `a122c87fc2934ab2b584e1d9cd09ca24`：新建 VD 已有非 NULL activity/read/write 样本；当时 Samsung gap 为 0，WDC 入池后缺少准确的 OS-disk 绑定，仍有开放 gap。该快照只说明当时所列样本和缺口。后续 B5 session/性能结果是各自历史运行证据，见下方；历史 open endpoints 仍保持 Unknown。

本阶段对受保护 Samsung 的比较只说明记录中列出的准确关联对象与字段范围。正面 `IsPrimordial` aggregate 字段和范围外成员边按比较规则排除；`0 changes` / `0 evidence gaps` 不能推广为整机对象全部不变，也不能排除 Windows 后台 I/O。历史和本阶段 TRX 都是原始验收证据；禁止回写或修饰既有 TRX。修复后复跑必须生成新的 TRX，单独记录其结果，不能用来改写旧失败状态。

### 第二次 MAX 只读恢复与阶段工程门历史：2026-10-08 21:21:03

该时点 [完整门 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-212103/summary.json) 覆盖冻结后的 root/editor 源码：12 个项目，**1722 Total / 1719 Passed / 0 Failed / 3 NotExecuted**。392 个纳入哈希的源码文件前后完全一致（0 个哈希差异）；Restore 与 Release build 均退出码 0，构建 0 warnings/0 errors；未请求 CapacityPreflight。3 个 NotExecuted 仍是既有 archive/performance measurement，不计为通过。依赖审计复用已有 23 项目结果，没有重跑。本门不包含 App/Agent 启动、原生 UI 或真实存储操作；版本仍为 V0.58 iteration 8。最新工程门见下方独立记录。

- **d92 标准启动后只读恢复。** [恢复证据](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/second-max-running-recovery/reconciled/asserted-observed-no-effect.json) 记录 operation 与 step 均为 state 6（Failed）；Accepted 与 `operation.step.call_issued` 各仅一次，`WindowsCallIssued=true`。判据为 `real.reconciliation_observed_tiered_creation_no_effect`；冻结前 fingerprint 与两次独立 fresh fingerprint 相同。provider 再次报告 eligible resources 不足。该旧界面 MAX 操作使用明确的 `StorageTiers + StorageTierSizes` 容量值，并非 typed `UseMaximumSize=true` 参数测试。没有重放或成功创建证据；这证明本次已观察 NoEffect，不证明原操作没有调用 Windows，也不证明该参数不受支持。
- **当时的 B4 / B5 状态。** d92 已离开 Running/Unknown 屏障，但单 HDD tiered MAX 的 Windows `UseMaximumSize=true` 成功正例仍缺失；只读 NoEffect 恢复不等于 MAX 成功，也不能推出该参数不受 Windows 支持。该时点 B4 未关闭，B5 与 H06 尚待完成。后续的 32 GiB 正向证据、B5 布局证据和 H06 视觉验收见下方当前记录。

### 最终完整工程门、B4/B5 与 H06 更新：2026-10-08 21:48:21（当时状态；后续由下文更新）

- **完整工程门。** [summary.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-214608/summary.json) 为 12 项目 **1734 Total / 1731 Passed / 0 Failed / 3 NotExecuted**，12 项目退出码均为 0。392 个源码哈希项前后相同；Restore/Release build 均为 0，build 0 warnings/0 errors。未请求 CapacityPreflight，依赖审计复用 23 项已有结果。3 个 NotExecuted 是既有 archive/performance measurement，不计作通过。版本为 V0.58 iteration 8。自动门不验证 UI 或真实设备操作。
- **B4 32 GiB 正例。** 修复后只读 Windows provider 事实快照 [hdd32-final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-proof.json) 的 [断言](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B4/hdd32-final-native-assertion.json) `passed: true`：WDC 上存在 32 GiB tiered VD、HDD template/interleave 65,536 bytes，MSR 偏好为 false，数据盘 GPT 且已格式化 NTFS/65,536-byte cluster。此正向证据不是 MAX 正例。旧 MAX 的 Failed/NoEffect 恢复记录仍保留；它们只描述当次数值 `-Size` 操作结果，不证明 `UseMaximumSize=true` 不受 Windows 支持。单 HDD `UseMaximumSize=true` 正例仍是必需验收，B4 尚未关闭。
- **B5 布局正例。** [final-native-proof.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/final-native-proof.json) 的 [13 项断言](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/final-native-assertions.json) 全为 true：目标物理盘在线 GPT，恰有规范 MSR 与一个最大整 MiB 数据分区，MSR 无卷、数据区唯一关联 NTFS/65,536-byte cluster `WinPool_Test` E:；没有具体池或关联 VD。此布局子验收已通过；B5 仍待受保护 Samsung 比较、性能统计、偏好恢复、正常退出/重启及最终末态核对。
- **H06 分区页视觉验收。** 当时现场以 WDC E: `WinPool_Test` 为准确选中对象。中文/英文 480×700 属性与目标页、纵向滚动底部及中文 1440×900 宽屏截图均已检查；两行起点/终点/容量、NTFS/64 KiB、底栏和窄屏布局可读。只读缩容预览显示 `3,815,429 MiB − 1 MiB = 3,815,428 MiB`；扩展入口因当时 provider 几何没有 1 MiB 目标而提示无可用目标。没有准备或提交扩缩操作。最终宽屏截图：[H06/partition-e-cn-1440x900-final-restored.png](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/H06/partition-e-cn-1440x900-final-restored.png)。其他 H12 性能、受保护对象、偏好和重启检查仍待。

### App 正常关闭生命周期修复与当时工程门：2026-10-08 22:17（历史快照；后续由下文更新）

- **原生退出异常与证据边界。** B5 原验证记录 [normal-exit-result.json](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/normal-exit-result.json)：App PID 14884 经已核实的 Agent 托盘 Exit 路径退出，Agent PID 18124 正常退出，但 App 退出码为 `-1073741189`。对应 [Windows 应用事件](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/normal-exit-application-events.json) 报告 CoreMessagingXP.dll `0xc000027b` 及 WER `combase.dll` `0x80004003`；没有相应的近期 managed crash 记录。源码审查发现旧 `Closed` 异步处理在 await 后仍访问 XAML/UI 状态的风险，因此实施了活窗口预关闭清理、同步 `Closed` 兜底、统一程序化关闭入口及关闭期间的输入/晚到回调防护。事件没有托管堆栈，故生命周期风险是修复依据，不能称为已确认的崩溃根因；没有吞掉或标记已处理 COM/Unhandled 异常。
- **最终完整工程门。** [20261008-221503 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221503/summary.json)：12 项目 **1766 Total / 1763 Passed / 0 Failed / 3 NotExecuted**，Restore 与 Release build 均退出码 0、0 warnings/0 errors，392 个源码哈希项前后相同；未请求 CapacityPreflight。App.Tests 为 179/179，Architecture.Tests 为 48/48。三项 NotExecuted 仍为既有 archive/performance measurement；依赖审计复用已有 23 项目结果。工程门只证明冻结源码通过自动检查，不验证原生关闭行为。
- **保留的前置失败。** 较早的 [20261008-221056 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221056/summary.json) 保留 **1766 Total / 1761 Passed / 2 Failed / 3 NotExecuted**，且 `sourcesUnchanged: false`：Architecture 的 Welcome 旧源码断言与 Persistence 归档临时目录 `archive-ledger.json` 文件占用失败。该 TRX 和错误记录未改写；修正后的冻结源码由 22:15 完整门单独验证，Persistence 文件占用未在该门复现。另一次构建尝试的 [build.log](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-221408/build.log) 也原样保留。前置错误不计入 22:15 门，也不因最终门成功而抹除。
- **仍待原生复验。** 本次工程门没有启动 UI 或执行存储操作。Agent 托盘正常退出后的 App 进程码、系统标题栏关闭时保留 Agent 后台行为、普通重启 Real Off/偏好及 Accepted/CallIssued 计数稳定性，仍需 root 按 H12 现场复核；在该证据落盘前，正常退出/重启为 `unverified`，B5 与 V0.59 阶段不标为完成。

### B5 监控恢复、性能与关闭最终核对：2026-10-08 22:38（历史运行证据）

- **最近完整工程门（本次 MAX 修正之前）。** [20261008-223310 summary](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/final-full-tests/20261008-223310/summary.json) 为 12 项目 **1775 Total / 1772 Passed / 0 Failed / 3 NotExecuted**，393 项源码哈希前后相同，Restore/Release build 退出码 0、0 warnings/0 errors，标准 App/Agent 合并 0 collisions。未请求 CapacityPreflight；依赖审计复用已有 23 项目结果。三个 NotExecuted 仍是既有 archive/performance measurement；版本保持 V0.58 iteration 8。该门不包含新增的 typed MAX App 解析、UI 确认文案与直接测试；它不能替代稍后的集中验证。新 Observer 告警呈现回归另有 [定向 9/9 记录](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/monitor-inactive-history-alert-fix/summary.json)。
- **当时 Monitor Off 的告警呈现。** 先前全新启动且 Monitor Off 时，`recovered_endpoint_unknown` / `PendingVerification` 历史项被错误呈现为当前监控异常；这不代表持久 gap 已恢复或重新发生。最小 Observer/predicate 修复只改变活动告警分类，不改采样、身份判定和历史 endpoints；Infrastructure 的 9 项回归验证仅排除此特定 inactive 历史状态，同时保留其他 pending/active 告警。该次标准 runtime 原生记录显示 App 3916 / Agent 14740 启动时 Real Off、Monitor Off，WDC E: `WinPool_Test` 与 Samsung 结构可见，Monitor Off 下无历史“监控异常”提示，见 [main-initial](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-main-initial.txt)、[monitor-off](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-monitor-off.txt)、[disk UI](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-disk-ui.txt) 和 [main-later](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-main-later.txt)。
- **监控 session 与操作计数。** [只读 receipt](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-monitor-repaired-performance/session-810015f675bf45c5a5cf7c2c3bd2aca1/final-readonly-receipt.json) 记录 session `810015f675bf45c5a5cf7c2c3bd2aca1` 停止、`dropped_samples=0`、当前 session gap 为 0；三块准确目标各有 4,252 条完整有限值样本，均值 20.006 Hz、中位间隔 47 ms、最大间隔 65 ms。18 个 durable Accepted→View 窗口与另 18 个 AcceptClick→ViewReady 性能窗均通过；六次 rename 使 plan、Accepted、Accepted event、CallIssued boundary 分别增加 6，计数到 245 plans / 193 Accepted / 193 accepted events / 244 CallIssued。三个历史 open endpoints 仍为 Unknown，没有被此轮修复或关闭。
- **六次改名性能。** [验证后比较](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/monitor-restored-performance/verified-performance-comparison.json) 匹配 incoming label `WinPool_Test` 的 B0/B5 样本 2/4/6；中位数由 **18.472 秒**降至 **15.437 秒**，减少 **16.43%**。计时范围是 accept click 到 view ready，排除 Prepare/input 与人工确认等待；六次样本均已 Verified 且具完整 matching scope。它只覆盖一块 WDC，不能外推至其他设备，B5 采集器逐阶段 `captureCount` 未记录。更早的 **15.76%** 统计来自 WDC 监控流为 0 的首轮，不是同负载对照，保留为历史而不作当前结论。监控/性能/受保护比较证据来自 221503 标准 runtime；223310 的源码仅改历史 inactive 告警呈现，没有重跑六次测量，也未更改采样语义。
- **受保护对象与 WDC 末态。** [final2 独立执行 receipt](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-final-production-full/final2-after-monitor-repair-six-renames-and-manage-refresh/independent-final-execution-receipt.json) 给出 Samsung 限定比较 `passed=true`、0 changes、0 evidence gaps，并有 4 项共享 Primordial scope adjustments；此范围不等于全机不变，不能排除 Windows 后台 I/O。[WDC 原生 crosscheck](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/Engineering/audit-final-production-full/final2-after-monitor-repair-six-renames-and-manage-refresh/wdc-layout-native-crosscheck.json) 的 8 项检查全部 true。证据为 fresh read-only provider/UI refresh；未做真实结构写入。
- **最终关闭和偏好。** 正常标题栏 Close 以进程句柄复核为 App exit code 0，同时 Agent 仍运行；之后 Agent 托盘 Exit 以原入口关闭 App 与 Agent，两者 exit code 0，见 [标题栏 Close](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/close-repaired-titlebar-native-result.json) 与 [最终 tray Exit](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-exit-result.json)。普通重启与第二次全新启动都保持 Real Off / Monitor Off、原偏好基线以及 245/193/244 计数；最终核对见 [final-after](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-after.json)。第一次标题栏探针 App 17736 的 ExitCode 缺失是仪器缺口、不计通过，详见[原始探针](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/close-repaired-titlebar-result.json)；修复前 App 14884 的 `0xc000027b` 事件仍作为历史证据保留。旧崩溃之后的原生复验通过，不证明其根因。独立[只读最终复核报告](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-readonly-review.md)与[结构化结果](../artifacts/test-results/20261008-v059-product-alignment-86c03bd2996348d3a3b47132ad83dd89/B5/presentation-final-readonly-review.json)覆盖四份 Monitor Off 界面快照、偏好/计数及限定时间段的事件与进程查询；它确认原生 Agent 托盘 Exit 两进程为 0，并明确不延伸为全历史或未来保证。
- **当时的剩余计划范围。** 截至该记录时间，B5/H06/H10/H11/H12 所列范围已通过，唯一未关闭的真实界面目标被记为 B4 单 HDD tiered MAX 正例。后续普通 native MAX 与分层 MAX 尝试见下文；本段不再作为当前状态。旧测量和 Samsung 比较继续限于其记录的单机/scope。

### 2026-10-09 原生 MAX 历史运行与当时未决项（统一算法前）

- **统一算法前的普通 VD 原生 MAX 历史正例。** 原界面普通池路线 [b085 operation](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/create-native-maximum/b085439f8cd645bb9f65512503f23359.json) 使用 `UseMaximumSize=true`；[布局断言](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-assertion.json) 与[只读 provider 证明](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Ordinary/native-maximum-layout-readonly-proof.json)核实普通（非 tiered）VD/OS disk 均为 **3,999,688,294,400 bytes**、canonical MSR 16 MiB、NTFS/64 KiB 与 `WinPool_V059_Data` W:。随后原 Agent 菜单 Exit 的 [App/Agent 退出码](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/ordinary-native-max-observed-exit-result.json) 均为 0。该证据仅说明当时的普通 native MAX 路线，不代表当前算法，也不证明 tiered MAX。
- **Exact-template tiered MAX 失败。** 操作 `a11ca8d034414b4aa275bb2d62625981` 使用 exact HDD template 和 `StorageTiers + UseMaximumSize`（不含 `StorageTierSizes`）；Windows 返回 48010。其[只读复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/exact-template-native-max-readonly-review.md)核对 operation/step 为 Failed，调用后无创建对象，冻结 fingerprint 前后稳定、storage jobs 终态，未重放。它只否定这条组合路线，不能推出其他原生分层 MAX 路线受 Windows 限制。
- **MediaType HDD tiered MAX 未通过。** 操作 [b0](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Tiered/create-wire-corrected-native-maximum/b0ca4d950b2c43dcb6c936b33ca40730.json) 改用 `MediaType HDD + UseMaximumSize`，但 fresh provider 返回的是没有实际 HDD tier 的普通 VD。原 UI 查询及 CAS 严格收口为 `Failed/UnexpectedEffect`；[只读独立复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/closed-query-recovery-host/root-verification-independent.json) 确认记录未改写、Accepted/CallIssued 各一次、对象残留和两次 fresh closure 相符。没有重放，也没有把这次结果算作 tiered 成功。
- **当时的恢复清理与临时 guard。** 随后原 UI 操作 `99f2ccc16d9a42adb362b0624c9c6389` 的[清理记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Tiered/cleanup-observed-residual/99f2ccc16d9a42adb362b0624c9c6389.json)核实了 residual VD、template、pool 的删除步骤均 Verified；未重放。在该历史时点，UI/planner 会在解散或建池前临时阻止分层 MAX 并要求明确容量；后来的 `WINPOOL-MAX-GIB-1` 已取代此 guard。历史清理及失败结果不变；当前算法验证见上方，仍没有真实多层 MAX 设备结果。
- **WDC 当前末态、偏好与退出。** 普通池清理之后，原 UI 完成清空 [d24ec…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-clear/d24ec284142c41a9b497edcea6d714d1.json)、GPT 初始化 [a1d958…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-gpt/a1d95850e68d4be78a6eee8ec20e54d1.json)、canonical MSR normalization [32b551…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-msr-normalization/32b551c985184ccca41da19a22f84767.json) 和数据分区 [f06077…](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-data/f06077e044394dd085d4caaa6e85404a.json)。`f060...` 的 create/format/assign-letter 三步均为 Verified；[全部写入完成记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/all-writes-completed.json)时间为 2026-10-09 02:04:29.4876334Z。原生界面快照显示 WDC 在线 GPT、canonical 16 MiB MSR、E: `WinPool_Test`、NTFS/64 KiB，见[最终布局页](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-final-layout-ui.txt)。[B0 偏好对照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/preferences-baseline-comparison.json)各项均相同，[Agent 设置](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/agent-settings-final.json)为 Monitor Off/20 Hz；该状态下原 Agent 托盘 Exit 的 [App/Agent 退出码](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/physical-final-observed-normal-exit-result.json)均为 0。
- **独立比较与重启复核。** 写入后 fresh native capture 于 02:06:19.499–02:06:20.840 UTC、full capture 于 02:06:32.692–02:06:35.578 UTC 完成：13 项末态断言通过（14 次只读查询），16 个 storage sources 的 typed facts 相等；Samsung scoped comparison 为 0 changes、0 evidence gaps，含 4 项共享 Primordial scope adjustments，WDC 与 full capture 逐字段一致，41 个 staged 与 20 个 authority hashes 相符；复核报告 15/15 检查通过。详见[最终保护比较复核](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.md)及[结构化结果](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-protected-comparison-review.json)。普通重启后 App 16372 / Agent 4296 为 Real Off、Monitor Off/20 Hz，E: `WinPool_Test` 可见；Accepted 214 / CallIssued 271、0 unfinished、395 个源码哈希和 B0 偏好均保持不变，见[只读审计](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/after-normal-restart-readonly-audit.json)、[重启 UI 快照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-full-ready-real-off-ui.txt)和[Monitor Off 快照](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-monitor-off-ui.txt)。原托盘 Exit 的 App/Agent 退出码均为 0，见[最终退出回执](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Final/normal-restart-exit-exit-result.json)。普通 UI 解散旧结构的 scoped 复核 20/20 通过（准确操作 `ae5a3bd3f5e8465cadcb1c116be27fba`、generation 145；fresh provider Primordial 边界与 Samsung 保留均核验通过），见[scoped release review](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/scoped-release-original-ui-review.json)。这些比较限于记录的精确 selector、对象与采集窗口，不证明全机或后台 I/O 无变化；三个历史 open gap endpoints 仍为 Unknown。
- **自动验证状态。** [新一轮定向门命令记录](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/consolidated-direct-20261009-095138/commands.json)及 Infrastructure/App/Application 三份 TRX 分别记录 440/440、166/166、6/6 Passed；这不是完整门。随后完整门 [095259 summary](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095259/20261009-095259/summary.json) 为 **1866 Total / 1856 Passed / 7 Failed / 3 NotExecuted**，Restore/Release build 成功，源码哈希未变。七项 App failure 均是 UI 帮助文案收紧后旧断言仍期待旧字串；失败 TRX 保留原状。同步断言后的 [095540 最终完整门](../artifacts/test-results/20261009-v059-native-maximum-81428d1f9c2249bfbeb4d30c21b08219/Engineering/final-frozen-gate-20261009-095540/20261009-095540/summary.json) 为 12 项目 **1866 Total / 1863 Passed / 0 Failed / 3 NotExecuted**，`sourcesUnchanged: true`；Restore/Release build 成功、0 warnings/0 errors。三个 NotExecuted 是既有 archive/performance measurements，不计为通过。

## 选择验证范围

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

需要实际证据的项目包括页面与拖拽、软件中英切换、主题/DPI/高对比度、键盘操作、托盘生命周期、文件夹选择器、监控启停和数据位置往返。1.x 的测试页验证路线占位及可访问性；当前开发页显示有界内存消息，验证其导航门、详情复制和退出后清空，不据此开发日志文件查看器或完整工作区。

模拟规则验证不执行真实存储写操作。真实写入须沿用 Product/AGENTS 已确认的准确授权，并记录当次目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。
