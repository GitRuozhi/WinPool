# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是 Windows 存储系统桌面应用，用于查看存储拓扑、监控设备和编辑模拟存储系统。

当前实现版本为 **V0.55**。真实存储结构修改尚未开放。

设置中的开发者模式默认关闭；启用后显示硬件、测试、开发三页，硬件排在管理之前。只读硬件页按十个大类呈现基本硬件报告，支持设备属性详情、本机完整刷新和完整系统 JSON 导出。当前格式为文档 3 / 核心 SQLite 17 / 监控 SQLite 1 / IPC 11，保存来源事实；旧格式明确拒绝，不迁移或删除旧数据。

启动时优先用完整性校验后的只读本地状态显示上次页面和系统，再由 Agent 复核；缓存不可用时显示加载状态，避免先呈现默认系统。管理页和硬件页先显示上次本机数据。Agent 启动后自动先采集存储系统、再采集完整硬件，分别回报更新；两页刷新按钮仍可手动触发对应采集。自动采集和手动刷新均在窗口内显示采集中、成功或失败提示；采集失败保留上次数据。读取历史不作为一次采集成功提示。

## 当前可用功能

- 通过只读采集查看本机存储，检查池、层、磁盘、分区及相关信息。
- 查看 Computer、System、Mainboard、CPU、Memory、VirtualMemory、Storage、GPU、Monitor、Network 十段只读硬件报告及原始来源详情。
- 在存储结构编辑和磁盘分区编辑两页修改模拟系统。
- 在 1 MiB 网格上创建模拟 GPT 分区；创建容量使用整数 MiB，默认填入对齐后的最大容量，并提供 MAX 按钮。右侧同一个操作按钮随选择切换“新建分区／格式化分区”。
- 以 JSON 导出存储系统。分区页的快速格式化与完整格式化是互斥的模拟方式，均不写入真实磁盘。
- 监控受支持设备，持续显示会话运行时长；异常统一进入消息卡片和本次运行消息记录。
- 新监控记录使用独立数据库，达到 1 GiB 轮换并以随附 7-Zip 归档；设置支持指定自定义 7Z 路径。
- 使用中英文界面、主题与键盘导航。

监控归档不自动删除。软件不提供监控历史查看，CSV 仅导出当前活动监控库中可用的记录。轮换期间落盘短暂停顿，以有界内存承接采样；崩溃或缓冲耗尽仍可能产生需要提示的记录缺口。

模拟结果不能证明 Windows 能够执行该配置，详见[当前限制与变更记录](docs/CHANGELOG.md)。

普通模拟数据分区可按目标总容量扩展或压缩，目标需按 1 MiB 对齐；系统分区仍禁止删除和格式化。扩展支持 NTFS/ReFS/RAW，压缩支持 NTFS/RAW。恢复代码及自动回归已通过，原生扩缩操作验证留待后续。

“测试”页保留路线说明；开发页左上“消息列表”以单行条目显示最近 200 条本次运行消息，双击条目在页面中央打开可复制的详情，点击对话框外关闭。三个区域之间可拖拽调整，右上和下方暂留空，下方提示人工智能入口正在开发。该页仅在开发者模式下可见。消息只保存在内存，退出 App 后清空；软件不查看故障日志文件内容，不记录完整操作历史。完整测试与开发工作区计划在 2.0 推出。

即时消息统一使用右下角固定宽度、高度随内容变化的简洁卡片，退出时向右移动，独立重复消息不合并。普通消息点击消失，错误消息点击打开消息对话框；两者都会自动消失，错误保留时间较长。详细记录在开发页查看。

界面和模拟系统的具体行为见[设计核对表](docs/DesignTables/README.md)。

## 系统要求与运行

最低受支持系统为 **Windows 10 22H2 x64**，主要平台为 Windows 11 24H2、25H2 x64。具体存储功能取决于 Windows 版本/版本类型及存储提供程序。

当前交付为无打包、自包含的 x64 便携目录。保留完整目录并运行 `WinPool.App.exe`，配套 Agent 在用户托盘中运行。数据默认存放于 `%LocalAppData%\WinPool`，也支持显式选择程序旁可写的 `Data` 目录。替换程序文件前退出 WinPool。

目前没有已发布的 MSIX 安装包或 Microsoft Store 页面。

## 从源码构建

在 Windows、PowerShell 和 `global.json` 指定的 SDK 环境中运行：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

构建前确认已有 WinPool 进程未占用输出目录。开发入口为 [AGENTS](AGENTS.md)，内部开发文档统一使用中文；构建和数据所有权详见[开发约定](docs/Development.md)。

## 研究背景

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

即：64K 交错配合 64K NTFS 簇是当前测试建议。受现有存储硬件价格及作者实际预算限制，无法配置第二套完整测试平台，Windows 11 尚未获得等价测试。这些研究结果不代表全部 Windows 配置都受支持或具有同等可靠性证据。

## 权利

本仓库未对 WinPool 自有代码授予任何许可证，保留所有权利。第三方组件遵循各自许可证；随附 7-Zip 组件的[许可证](assets/ThirdParty/7zip/26.03/License.txt)及[来源说明](assets/ThirdParty/7zip/26.03/NOTICE.txt)单独提供。
