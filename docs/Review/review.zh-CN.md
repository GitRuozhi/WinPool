# WinPool 第二轮静态复审

审查日期：2026-10-12  
仓库：GitRuozhi/WinPool  
上轮基线：`de9641cdd20d2d340e3ceafe223eb249f80b71bb`  
本轮目标：`1cb9cb0968acccc2e7321ebb41cd921b0b4f2bed`  
比较结果：目标领先基线 7 个提交；结束前重新读取 main，仍为上述目标提交。

## 结论

有实质修复，不能把这一轮修改评价为仅改文案或增加保护判断。但真实编辑的结果、界面事实和剩余草稿还没有完整衔接。本轮提出四项需要处理的代码问题：一项 P1 恢复流程阻断，以及三项 P2 恢复/交互一致性问题。另保留一项状态机防乱序缺口，不把它当作已在当前 UI 复现的并发故障。

这里的“本轮新发现”不等于“本轮提交新引入”。没有完成逐提交二分定位，不作缺陷引入时间的断言。

建议先修恢复上下文和延迟映射，再修原交互的取消/规划一致性。无需为此恢复已移除的查询、停止、准备按钮，也不应推翻已确认的 MAX 算法或直接清空数据库屏障。

## 审查方式和证据边界

已通过 GitHub 连接读取固定提交的源码、比较结果、相关提交差异、Quality 和工作约定，追踪两个编辑页、共享提交、工作区恢复、草稿映射、授权、MAX 指纹和事实合并的相关路径。检查了 App 测试工程配置以及新增恢复用例。

这是人工源码/控制流静态审查，不是全仓逐行证明。没有 .NET SDK；未执行原工程编译、Roslyn 分析、xUnit、WinUI 或实际 Windows 存储操作。终端获取仓库受网络 DNS 限制，因此以 GitHub 连接读取为依据。没有修改远端分支、创建 PR 或修改用户磁盘。

Quality 中的 104 项定向回归、16 个真实操作/23 步及设备末态是仓库记录，不是本次独立运行结果。其详细产物位于忽略提交的 artifacts/test-results，本次未取得原始 TRX、数据库或全部原生证据。文档也明确限定了验证范围，没有声称完成全部故障注入或全量验收。

附带 regression-spec.zh-CN.md 的 16 条是待实现/执行的回归规格，不是已通过的测试，也不是可直接应用的代码补丁。

## 一、已确认的改善

### 1. 显示状态先经过身份过滤

WorkspaceRealOperations.ObserveRealOperation 现在先检查 RealOperationSubmission.Observe 的返回值，只有接受的结果才赋给 LastRealOperationStatus。修复了上轮“被状态类拒绝的响应仍覆盖显示状态”的问题。

依据：`src/WinPool.App/ViewModels/WorkspaceRealOperations.cs`，ObserveRealOperation。

### 2. 恢复会查询保留的准确 ID，而不是只依赖未完成列表

新 RealOperationRecovery.RefreshAsync 先查当前保留的 OperationId/PlanHash，再列举可恢复操作；发现新身份时再查一次。工作区还增加了只读轮询、退出取消、终态后相关事实刷新及页面恢复回调。编辑会话新增准确未知操作 ID/hash，自动布局新增 ReconcileTerminal。

这修复了“操作已在 Agent 终结、从 unfinished list 消失，App 因只看列表而永远不知道”的一类问题。现有针对辅助类的测试直接覆盖了该行为。

依据：RealOperationRecovery.cs、WorkspaceRealOperations.cs、SimulationEditingSession.cs、RealAutomaticLayoutProgress.cs、RealOperationUiFlowTests.cs。

### 3. 真实流程回到原有产品交互

结构 Apply 使用一次完整目标/风险确认；后续内部计划仍核对冻结哈希、命令、物理成员、会话及已验证输出。额外查询/停止入口和旧 StopSession 被移除。Apply finally 现在清除执行标志后调用 RefreshExecutionMode，重新建立拓扑交互，而非仅恢复按钮。

这些是有意义的产品与实现调整。本轮建议仍采用这一产品方向，不新增技术步骤给用户。

依据：RealStructureApplyAuthorization.cs、EditorPageBase.cs、StorageStructurePage.xaml.cs 的 ApplyAll_Click。

### 4. MAX 恢复和来源合并不再各走一套旧逻辑

MaximumCapacityRecovery 的收据指纹复算调用 WindowsRealStorageTopology.CanonicalComponent，并显式处理已知历史编码/物理分配计数差异。WinPoolFactRefresh.Merge 使用更新的成功完整来源淘汰被覆盖的旧 scope 标记，并继续保留对象/字段引用和没有被完整覆盖的证明。

这是源码层面的改善判断，不是对全部设备、全部历史收据或无上限长期运行的独立认证。

依据：WindowsRealStorageBackend.MaximumCapacityRecovery.cs、WindowsRealStorageTopology.cs、WinPoolSystem.cs。

### 5. 真实未知结果的模拟提示已修正

EditorPageBase.PublishOperationResult 对 source == real 分支明确显示未核实/禁写，不再提示刷新模拟状态。

## 二、上轮问题复核

| 上轮项 | 本轮结论 | 依据和限定 |
| --- | --- | --- |
| R1 迟到旧响应重新锁住；显示被拒绝的状态 | 部分修复 | Workspace 先过滤后显示已修；SubmissionState 没有终态历史/版本判定。旧手动并发查询入口已移除，不能照搬上轮 UI 触发描述。 |
| R2 刷新与未知恢复被草稿门挡住 | 部分修复 | 显式未知结果的准确恢复已补齐；传输中断没有登记草稿上下文，见 F1；成功待刷新的延迟映射见 F2。 |
| R3 同池删旧 VD 后建新 VD 前后资格不一致 | 仍存在 | 页面依据 working，而 planner 仍依据 before 中是否有 VD。 |
| R4 取消盘符修改不恢复选择 | 仍存在 | await SubmitRealAsync 后直接返回，未使用结果恢复控件。 |
| R5 真实未知结果提示刷新模拟状态 | 已修复（源码） | real 分支已有独立提示。 |

## 三、问题明细

### F1 — P1：接收操作后的传输中断没有登记草稿恢复上下文

**确定性：**条件成立时由当前控制流直接推出；未做 WinUI 原生故障注入。  
**影响：**后端可以成功、工作区屏障可以释放，但同页草稿停在基线冲突，不能继续原有剩余目标。不是已证实的数据损坏或自动重复写盘。

**位置：**
- EditorPageBase.cs：SubmitRealAsync、SubmitReservedRealAsync 的 Query 异常/轮询结束分支。
- RealStructureEditorFlow.cs：Stage、HasMatchingUnknownRealStructureDraft。
- WorkspaceRealOperations.cs：RecoverRealOperationsCoreAsync。
- StorageStructurePage.xaml.cs：OnRealInventoryRefreshed、OnRecoveredRealOperation。

**条件与控制流：**
1. 结构 Apply 的一个单步创建已被接受，尚未收到任何 Query 进度，或最后进度仍为 Running 且无已验证步骤。
2. Query 抛出被捕获的 IOException 等异常，或达到轮询上限。共享提交函数返回 false；LastRealOperationResponse 仍是 null 或 Running。
3. Stage 只在 durable.RequiresReconciliation 时登记 outcomeUnknownDraftRefreshes；只在成功/已有 Verified 且刷新失败时登记 pendingDraftRefreshes。上述场景两者都没有登记。
4. 新工作区轮询保留的 ID，最终得到 Succeeded 并成功刷新真实事实。
5. 页面恢复入口先执行 OnRealInventoryRefreshed。旧草稿还含临时对象，HasUncommittedChanges 为 true，于是 MarkBaselineConflict。
6. 随后的 ReconcileUnknownRealStructureDraft 因缺少 OutcomeUnknown 标志及字典上下文而拒绝恢复；OnRecoveredRealOperation 返回“没有匹配的未知草稿”，工作区将其当作恢复允许，释放全局屏障。
7. 页面本地 HasBaselineConflict 仍为 true，CanEditDraft 为 false。用户不能续接原草稿，只能丢弃后按现场重新编辑。

**建议：**在准确 frozen OperationId/hash 建立后、Accept 前后边界明确的位置登记该阶段的草稿上下文，而不是等收到服务端 OutcomeUnknown 才登记。区分“调用结果未观察到”“服务端报告未知”“已验证但未刷新”等状态，但都要能找到同一提案、绑定代际和输出映射。自动恢复只读核对完成后，先安全映射该阶段，再报告 editorRecovered；相关草稿未核对完成不能返回成功。

不要为了保存草稿把后端状态伪造成 OutcomeUnknown，也不要自动重放旧 Accept。恢复只能带来新事实和剩余目标，继续执行必须遵守当前整批确认的授权生命周期。

**必补回归：**RV01、RV02、RV03、RV07。

### F2 — P2：成功待刷新的延迟映射覆盖用户后来修改的参数

**确定性：**在 scoped refresh 未应用新文档就失败、页面未重新绑定的场景下，由当前参数保存和闭包写回路径推出；尚未原生复现。  
**影响：**新卷标等尚未执行的输入被旧意图替换，确认内容可能退回旧值。不声称绕过确认或已经用错误参数写盘。

**位置：**
- RealStructureEditorFlow.cs：VD 创建 Stage 的 map；pendingDraftRefreshes 的恢复分支。
- EditorPageBase.cs：终态 Observe 在 RefreshRealInventoryAsync 之前。
- StorageStructurePage.xaml.cs：CanEditDraft、SelectedCreationIntent、CaptureSelectedIntent、ApplyAll_Click、UpdateFormStatesCore。
- SimulationEditingSession.cs：BindingGeneration、Restore、Bind、Discard。

**条件与控制流：**
1. VD 创建成功，成功终态先释放 RealOperationSubmission 的屏障。
2. 相关刷新在 ApplyLocalInventory 前发生 I/O 失败。Stage 保存 deferred map；页面仍显示旧的 draft VD，没有登记本地 OutcomeUnknown，也没有 pending-refresh 编辑门。
3. Apply finally 结束后允许编辑。用户将尚未创建的卷标从 A 改为 B，并保存。普通保存/Undo/Redo 不改变 BindingGeneration。
4. 再次 Apply 时先 CaptureSelectedIntent，B 进入 PoolIntents。
5. pending.Map 执行创建时捕获的旧 intent：

```csharp
session.PoolIntents[poolId] = intent with
{
    VerifiedVirtualDiskId = vdId,
    PendingAutomaticLayout = intent.AutoCreatePartition
};
```

6. 这不是只补充输出 ID，而是用旧整份记录替换新记录。B 被写回 A。随后 RebaseRemainingDraft 清除撤销记录；新授权从回写后的旧参数生成。

**建议：**“同步已经完成的结果”期间不得开放会被旧映射覆盖的编辑；或者使用明确的草稿修订号及字段级合并，只写入已验证输出身份和阶段状态，对新旧参数冲突作显式处理。BindingGeneration 目前只证明同一绑定生命周期，不能证明期间没有修改。不要简单把闭包保存为“以后再 Invoke”的任意动作，而应存储可核对的阶段结果及映射数据。

**必补回归：**RV04、RV05、RV06。主正例选择已存在且为空的受支持池，创建首个 VD、AutoPart 开启，避免把“新池映射”和“VD 映射”混为一谈。

### F3 — P2：同池显式删除后新建仍被 before 判断误挡

**确定性：**源码直接确认，上轮 R3 保留。

**位置：**StorageStructurePage.xaml.cs 的 CreateVdisk_Click/DeleteVdisk_Click；RealStructureDraftPlanner.Build。

页面在 working snapshot 中删除旧 VD 后允许插入 draft VD。规划器已经计算 removedVds，但下面的拒绝条件不使用它：

```csharp
draftVds.Length > 0 && !newPool &&
before.VirtualDisks.Any(vd => vd.PoolStableId == pool.StableId)
```

即使旧 VD 不在目标草稿中，也仍在 before 中，因而拒绝。多一个共同模型并没有消除 UI 与规划器的资格分叉。

**建议：**不要求扩大硬件支持范围。若当前产品支持同池显式替换，就按目标关系和明确删除集合判断，并在旧对象删除验证、池为空的事实刷新后才准备创建。若当前阶段要求先单独应用删除，就提前禁用创建并解释顺序，不能让用户形成无法应用的草稿。旧 VD 保留、第二块 VD、未支持层结构仍须拒绝。

**必补回归：**RV10。

### F4 — P2：取消真实盘符修改后选择值不回到事实值

**确定性：**源码直接确认，上轮 R4 保留；未做原生截图。

**位置：**DiskPartitionPage.DriveLetterBox_SelectionChanged；EditorPageBase.SubmitReservedRealAsync 的取消返回。

实际 E: → 选择 F: → 在确认中取消。事件处理器 await SubmitRealAsync 后不检查结果即返回；取消成功不触发写后刷新；页面活动变化只更新按钮。因此选择框仍停在 F:，实际事实仍为 E:。再次选择已经选中的 F 又不构成正常的选项变更。新增只读轮询不是这个场景的修复：取消终态已释放屏障，而轮询以 IsBlocked 为启动条件。

**建议：**把取消/明确拒绝且无写入、结果未知、写入成功但视图未同步区分开。对明确无写入的取消/拒绝，在确认目标仍为当前选中对象后，在 _filling 保护下恢复实际盘符；未知结果不得伪装成已经恢复的实际值，继续禁写并只读核对。不要用一次额外写盘“改回 E:”解决界面回退。

**必补回归：**RV11、RV12。

## 四、保留但不夸大的风险：迟到响应重新接入

RealOperationSubmissionState 的以下输入序列仍会重新锁住：TrackPrepared(A) → Observe(A,Succeeded,false) → Observe(A,Running,false)。终态清空 ID/hash，没有记录已终结身份或状态版本。若 B 此时仅预留尚未绑定，旧 A 也可能被接入该空位。

但本轮已经移除旧“查看结果/停止”并发 UI 入口，恢复增加了单任务与忙碌检查。本次没有证明当前生产 UI 一定产生这个乱序。因此仅列为状态机防乱序缺口，不重复上轮的确定 UI 并发触发或 P1 描述。

测试应保留新发现未知/在途操作的合法接入，只拒绝同一已终结身份的过期状态。优先使用持久状态版本/事件序号，或有界的已终结身份记录；不能一概禁止空闲状态接入在途操作。

必补回归：RV13、RV14。

## 五、现有测试为何还会漏掉这些组合

WinPool.App.Tests.csproj 引用 Application/Execution，并链接多个 Services 文件，包括新增 RealOperationRecovery 和 RealStructureApplyAuthorization。它没有引用实际 App 工程，也没有把 EditorPageBase、RealStructureEditorFlow、WorkspaceRealOperations 或两页代码编进这个测试项目。

例如新增 RecoveryRetainsExactTerminalWhileRelatedInventoryRefreshNeedsRetry，先把 helper 回调改成只保存 awaitingRefresh，下一次直接 state.Observe，再断言屏障释放。它验证“consumer 可以延迟应用终态”，但没有真正运行工作区 scoped capture、页面回调、草稿字典和新建对象映射；不能证明 F1/F2 的组合可靠。

这些测试仍有价值。建议在现有 App/Application 边界内提取能被直接驱动的恢复协调逻辑，或增加真正的页面级测试宿主。不要在测试里重写一套简化 consumer 逻辑后，把它的通过当作生产 consumer 的验证。

不据此声称全仓没有 UI 测试或设备验收；Quality 明确有相关记录，只是证据范围不同。

## 六、修复顺序与验收标准

第一步处理 F1：登记准确阶段上下文，贯通传输中断到只读恢复，再到剩余草稿。随后处理 F2：在成功结果未同步时冻结冲突输入或安全合并，禁止旧 intent 整体覆盖新输入。两项可共用结果同步状态，但不必新建通用引擎、DSL 或产品页面。

第二步处理 F3/F4：UI 与规划器共享当前资格结论，取消恢复控件而不写盘。

同时补 RV13/14 的乱序防护；保留已修复的授权、已知完整来源覆盖、MAX 指纹和 Apply 后拖动回归。

验收的重点不再只是“成功建一次池”。应证明：已接受后失联、没有终态响应、成功但刷新失败、恢复过程中再次失败、取消和明确拒绝之后，用户的目标、显示事实和禁写状态仍一致。所有恢复测试都断言没有额外 Accept、没有重复写调用，也没有自动扩大已确认范围。

## 来源定位

固定提交源码入口和阅读范围见 source-manifest.json。关键源码均通过 GitHub 连接读取；manifest 的 blob_sha 是 GitHub 返回的对象哈希，不是本地重新计算的源码哈希。
