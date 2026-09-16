# WinPool 验证与验收

本文件规定验证选择与结果含义。有活动阶段时，具体用例和进度归 `docs/Plan.md`。当前 V0.53 自动门为全套 613/613 测试、完整 Release 构建 0 警告/0 错误、依赖审计无已知漏洞包。隔离原生 WinUI 已验证开发者模式默认关闭、三页同时显示/隐藏、硬件位于管理之前及设置跨完全重启保留；管理页和硬件页已验证项名自动宽度及上限，硬件页还验证了横向滚动时项名保持显示、自动且有上限的设备列、分离的横纵滚动条、整页纵向滚动、设备列选择后自动居中与文本选择、无详情弹窗。标题栏已在硬件页连续往返切换本机、参考模拟、标准两层池、超多磁盘服务器和其它与网络共 7 次，进程保持存活和响应。网络来源整组刷新后由旧版 46 个对象收敛为 7 个，GPU 从混合来源的 7 列收敛为 4 个 DXGI Adapter，Monitor 从混合来源的 4 列收敛为 2 个 DXGI Output。D3DKMT 间接显示识别在本机刷新后仍保留 4 个 GPU 对象，其中 Intel 核显只有 1 列，另一列正确显示为 GameViewer Virtual Display Adapter，且不再借用 Intel 驱动和 PCI 位置。12 个内置模拟已在实际运行时重建为 Windows 形态来源事实，并核对系统版本、系统卷分配单元、物理磁盘数值类型、扇区单位和硬件页存储摘要。硬件报告、统一存储摘要与本机只读补充采集的有限原生结果见[硬件报告实施核对](Archive/20260915-hardware-report/实施核对.md)；当前机器只覆盖 96 DPI，192 DPI 为 `unverified`。更早 V0.52 统一事实与模拟编辑证据见 [V0.52 实施核对](Archive/V0.52/实施核对.md)。文档位置和语言规则归 [Development](Development.md)，真实操作边界归 [Product](Product.md)。

## 选择验证范围

2026-09-16 统一层模拟池/模拟层：以 `6de94cd` 加本轮工作区改动运行全套 Release，11 个测试项目合计 613/613（0 失败、0 跳过）；主代理逐份解析 `artifacts/unified-projection-test-v5/full-release-complete/*.trx` 核对。完整 Release 构建 0 警告/0 错误，运行树为 `artifacts/unified-projection-release-v5/runtime`。回归覆盖热备/退役优先且来源关系保留、未知归属、用途/成员变更后重新派生、孤立联合体导航、假对象无容量、真实层容量缺失/零、来源往返不含假对象，以及服务入口拒绝假对象编辑。同期其它任务归档设计文档，旧架构路径与版本措辞断言据实际状态同步后重跑全套。

执行命令为 `dotnet restore WinPool.slnx`、`dotnet build WinPool.slnx --configuration Release --no-restore -m:1 -nodeReuse:false`（覆盖 `WinPoolLocalTreeRoot` 至 v5 的 `trees/Release`，覆盖合并命令以延后合并）、`dotnet test WinPool.slnx --configuration Release --no-build --no-restore --results-directory artifacts/unified-projection-test-v5/full-release-complete --logger "trx;LogFilePrefix=full-release" -m:1 -nodeReuse:false`。随后用既有合并脚本生成 v5 runtime，288 个共享文件、293 个 App 独有文件、7 个 Agent 独有文件，0 冲突。`dotnet list WinPool.slnx package --vulnerable --include-transitive` 的 22 个项目均无已知漏洞包。实现提交为 `9e15f54`，文档归档断言同步为 `98f69b7`。

有限原生 WinUI 使用独立 Debug `artifacts/unified-projection-build-v3/runtime/Data`，未改用户数据根：中文三个模拟层、两个模拟池均可在管理页选中，假对象类型、空容量、禁用修改命令正确；热备层和其它磁盘池的原始字段弹窗显示“无原始来源”。真实层 928 GiB 与成员 931.32 GiB 分别显示。英文未划层显示 `Unallocated layer / Synthetic tier`，原始字段为 `No original source.`。截图为 `artifacts/unified-tiers-qa.png`、`unified-pools-qa.png`、`unified-no-source-qa.png`、`unified-english-qa.png`。完整主题/DPI/拖放验收为 `unverified`，未执行真实存储写入；不宣称生命周期退出通过，QA App 关闭后仅结束了隔离 QA Agent。

2026-09-16 采集状态通知：全套 Release 603/603（0 失败、0 跳过），完整 Release 构建 0 警告/0 错误；隔离产物为 `artifacts/InventoryNotifications`。通知工厂覆盖自动两种用途的开始/成功/失败和手动事件去重；观察者验证历史/缓存无采集成功通知、应用成功后才通知、损坏报告不通知成功；Agent 与真实命名管道覆盖开始/终态顺序和自动来源标记。原生弹出效果及真实硬件运行仍为 `unverified`，未替换或退出用户正在运行的旧版本。

2026-09-16 历史优先与两阶段采集修复：全套 Release 598/598（0 失败、0 跳过）、完整 Release 构建 0 警告/0 错误。构建使用独立 `artifacts/trees/InventoryRefresh` 树并合并至 `artifacts/InventoryRefresh`，未覆盖正在运行的 Release Agent。回归实际执行只读历史读取、无数据库不创建、旧/未来 schema 拒绝、历史先于连接呈现、Agent 存储→完整采集（含已有历史）、失败继续/保留、取消、晚连接/重连、手动采集用途，以及真实命名管道两阶段事件传输。旧的“有缓存不扫描”断言已移除；WinUI 页面目视、真实硬件两阶段采集与多平台验收本轮均为 `unverified`。

- 纯文档任务：检查内容一致性、当前链接、归档完整性及 Git 范围；不运行代码、原生、设备或视觉测试。
- 普通修复/小功能：运行能验证风险的直接相关检查，不自动扩成全套验收。低影响、可逆修改不为凑数量添加测试。
- 执行已确认 Plan：其中明确要求的回归和自动门属于任务本身，无须重复申请；阶段收口运行 Plan 指定范围。
- 完整人工、平台、设备和发布验收：仅在用户明确要求或已确认计划明确包含时进行。完成代码不自动表示完整人工验收开始或结束。
- 发现失败先判断是实现缺陷还是已失效的旧约定；不能删除有效安全断言来让测试通过，也不能让旧断言恢复用户已经否定的行为。

结果只能使用 `passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过、缺少环境和未运行均不能报 passed；源码推断与实测证据要分开。

## 文档与架构检查

- 当前内部文档为中文单一权威，仅根 README 成对维护；历史副本原样保留，不要求归档一律配对或翻译。
- 有活动阶段时只有一个 `docs/Plan.md`。用户要求保留的待执行计划可继续留在该文件中，但须明确标记“未激活”，不得当作当前实施授权。监控数据库轮换计划已按后续决定转为当前激活，实施尚未开始。Design 不被枚举为待执行计划；标题或旧正文中的命令式措辞不改变其状态。
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

## 自动门

正式阶段如 Plan 要求，在 WinPool 仓库根运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

所有自动测试使用隔离数据，不修改真实存储结构。记录实际命令、提交基线和结果；警告逐项说明，未解决失败不能作为完成。输出按 Development 的运行树规则隔离，不覆盖用户正在运行的程序。

App 和 Agent 独立产出、SHA-256 并集合并与碰撞失败机制继续验证；运行时查找与目录布局必须一致。命名管道身份/ACL、SQLite 所有权、原子提交及只读边界仍受直接回归保护。

## 人工与设备证据

WinPool 是原生多进程应用。浏览器 DOM 测试不替代 WinUI、托盘、原生选择器和设备检查。

需要实际证据的项目包括页面与拖拽、软件中英切换、主题/DPI/高对比度、键盘操作、托盘生命周期、文件夹选择器、监控启停和数据位置往返。1.x 的测试与开发页只验证简短路线占位及可访问性，不据此开发完整工作区。

V0.52 不用真实写操作验证模拟规则。未来允许真实写入的阶段，仍须按 Product/AGENTS 取得准确授权并记录目标与结果；自动测试和 CI 不触碰真实结构。缺少设备或目标平台时明确未验证，不制造通过记录。

自动门证明工程行为，不能批准视觉意图或物理设备行为。用户接受阶段也不能把未执行用例改写为 passed。例外记录原因、范围、批准者和风险，保持简短可追溯。

## V0.52 已执行验证

统一事实和编辑链路的最终结果为 546/546 自动测试、Release 构建 0 警告/0 错误、依赖审计未发现已知漏洞包。新增字段状态、来源冲突/备用、部分刷新关系、逻辑盘分类、旧报告退出、单一请求契约和友好待办标题均有实际服务回归。

本轮有限原生实测涵盖硬件空/失败来源、管理来源详情、Enter、MAX、拖放、命令展开/复制、结构提交/放弃、用途/脱机与 App/Agent 完全退出重载，并检查中英文、主题、高对比度和 96/192 DPI 字段渲染。发现的放弃后延迟事件误报修改已修复并复测。实测范围、机器缓存条件和副屏限制见 [实施核对](Archive/V0.52/实施核对.md)，不能扩写为全平台或长期可靠性验证。

2026-09-15 的硬件报告执行在新 HEAD 再次通过 546/546、Release 构建与依赖审计。新增纯投影测试覆盖十段顺序、Memory 总体/模组、多设备、负显示坐标和存储摘要的未知/零/新失败；现场测试确认 DXGI 与网络原生来源进入统一事实。隔离 WinUI 通过中文深色、英文浅色、刷新/取消状态、系统切换、窄窗横向滚动、键盘详情语义、导出成功与保存取消。当前双屏均为 96 DPI，192 DPI 保持 `unverified`；详细读数和本地证据位置见[硬件报告实施核对](Archive/20260915-hardware-report/实施核对.md)。
