# WinPool V0.38 陈旧 Agent 端点恢复执行计划

## 0. 状态、目标与基线

- **计划状态**：implemented / automatic gates passed / native-manual unverified
- **创建日期**：2026-08-13
- **目标版本**：V0.38
- **基线版本**：V0.37
- **目标分支**：`main`
- **用户授权**：尚未批准执行
- **计划性质**：V0.37 后续缺陷收口，不新增功能，不扩大产品范围。
- **核心目标**：修复 App 读取陈旧 `agent-endpoint.json` 时误判 Agent 存活，导致主程序连接超时、Agent 未启动、本机采集为空、外部工具检测卡住的问题。

本计划不授权真实磁盘、分区、卷、Storage Pool、Storage Tier、Virtual Disk 的创建、删除、格式化、初始化、扩容、修复或移除。

## 1. 根因

当前 `NamedPipeAgentConnection.ReadLiveEndpointAsync` 只检查端点中的 `ProcessId` 是否在系统中存活：

```csharp
return endpoint is not null && IsProcessLive(endpoint.ProcessId)
    ? endpoint
    : null;
```

`IsProcessLive` 只调用 `Process.GetProcessById(processId)` 并检查 `HasExited`。

Windows 会复用 PID。现场证据显示：

- 旧 Agent 已退出。
- `agent-endpoint.json` 仍保留，`ProcessId` 为 `1620`。
- PID `1620` 已被 `svchost.exe` 复用。
- 客户端认为端点仍有效，尝试连接旧 named pipe。
- 连接超时，主程序报告 `The operation has timed out.`
- App 没有走到 `launcher.EnsureStartedAsync` 分支，所以不会启动新的 Agent。

因此后续页面表现为：

- 本机系统为 `0 存储池 / 0 物理磁盘 / 0 B`。
- 外部工具一直显示“正在检测…”。
- 开发页 `刷新诊断` 不可用。
- `last-crash.txt` 记录 named-pipe 连接超时。

## 2. 修复目标

V0.38 只修这个恢复缺陷：

1. 端点“进程存活”不能只看 PID。
2. 必须同时验证进程身份和启动时间。
3. 陈旧端点必须被当作不存在。
4. 随后走现有 `launcher.EnsureStartedAsync` + `WaitForLiveEndpointAsync` 路径启动新 Agent。
5. 不改变 IPC protocol，不改变 SQLite schema，不改变 App/Agent/TestWorker/Broker 架构。

## 3. 实现方式

### 3.1 引入可测试的 Agent 进程身份验证

在 `WinPool.Agent.Client` 增加内部接口：

```csharp
internal interface IAgentProcessLiveness
{
    bool IsExpectedAgentProcess(AgentEndpoint endpoint);
}
```

默认实现使用 Windows `Process`：

```csharp
internal sealed class WindowsAgentProcessLiveness : IAgentProcessLiveness
{
    public bool IsExpectedAgentProcess(AgentEndpoint endpoint)
    {
        try
        {
            using var process = Process.GetProcessById(endpoint.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            if (!string.Equals(
                    process.ProcessName,
                    "WinPool.Agent",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var startedAt = process.StartTime.ToUniversalTime();
            var tolerance = TimeSpan.FromSeconds(5);
            return Math.Abs(
                (startedAt - endpoint.StartedAtUtc).TotalSeconds) <= tolerance.TotalSeconds;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
```

如果测试环境不能读取 `Process.StartTime` 或进程名，可通过注入的 liveness 实现隔离。

### 3.2 修改端点读取

`ReadLiveEndpointAsync` 使用 liveness 接口，而不是只调用 `IsProcessLive`：

```csharp
return endpoint is not null && _agentProcessLiveness.IsExpectedAgentProcess(endpoint)
    ? endpoint
    : null;
```

当端点被判定为陈旧时，返回 `null`，现有 `ConnectAsync` 会启动新 Agent 并等待新端点。

### 3.3 保持连接身份校验

连接阶段继续保留现有 handshake 检查：

- `reply.AgentProcessId == endpoint.ProcessId`
- `reply.AgentSessionId == endpoint.AgentSessionId`
- event server PID 与 endpoint PID 一致

本轮不放松这些检查。

## 4. 测试要求

新增或修改 `NamedPipeAgentConnectionTests`，覆盖：

- endpoint 中 PID 已退出时返回 `null` 并启动新 Agent。
- endpoint 中 PID 存活但进程名不是 `WinPool.Agent` 时返回 `null`。
- endpoint 中 PID 存活、进程名正确但启动时间不匹配时返回 `null`。
- endpoint 正常时保持原连接路径成功。
- 连续多次遇到陈旧 endpoint 时不会永久轮询或崩溃。
- 正常 agent 启动和 handshake 行为不回归。

测试使用临时 endpoint 文件和 fake liveness/launcher，不访问真实系统 Agent。

## 5. 手工验证

必须模拟并验证现场问题：

1. 停止 WinPool.Agent。
2. 保留 `%LocalAppData%\WinPool\agent-endpoint.json`。
3. 将 endpoint 中 `ProcessId` 修改为某个已经存在但不属于 WinPool 的 PID。
4. 启动 `WinPool.App.exe`。
5. 观察 App 是否自动启动新 Agent。
6. 观察“管理”页本机信息是否不再显示 `0 存储池 / 0 物理磁盘 / 0 B`。
7. 观察设置页外部工具是否不再永久显示“正在检测…”。
8. 观察开发页“刷新诊断”是否恢复可用。

所有未执行项保持 `unverified`。

## 6. 自动质量门

从 `Program\WinPool` 根目录执行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

要求：

- 全部 deterministic tests 通过。
- Release build 0 error。
- warnings 为 0 或有明确批准说明。
- 漏洞审计通过。

## 7. 明确不做什么

- 不升级 IPC protocol。
- 不升级 SQLite schema。
- 不新增 App 页面或功能。
- 不重构 Agent/TestWorker/Broker。
- 不修改真实存储 mutation 边界。
- 不删除用户数据或旧 endpoint 文件；运行时代码只决定忽略陈旧 endpoint。
- 不推送、打 tag、创建 Release，除非用户另行明确批准。

## 8. 验收标准

V0.38 完成条件：

- 陈旧 endpoint 能被稳定识别。
- 新 Agent 能通过现有启动路径恢复。
- 管理页本机采集不再因陈旧 endpoint 显示空数据。
- 设置页外部工具检测不再永久卡住。
- 开发页刷新诊断可用。
- 新增回归测试通过。
- Release 自动门通过。
- IPC protocol 保持 `3`，SQLite schema 保持 `12`。

## 9. 执行状态

本文件目前只是待批准计划。用户批准前不修改代码、不执行测试、不更新版本、不提交 Git。

## 10. 实施记录

2026-08-13 已获用户批准并完成实施：

- `NamedPipeAgentConnection` 已增加 `IAgentProcessLiveness`。
- 默认实现会验证进程名和启动时间。
- 陈旧 endpoint 会被视为不存在，并走现有 Agent launcher 恢复路径。
- 新增 stale endpoint 回归测试。
- Release build 通过，0 warning，0 error。
- Release tests 通过 520/520，0 skipped。
- 依赖漏洞审计通过。
- 版本推进至 V0.38。
- 原生 UI、托盘、UAC、设备、外部工具和数据位置手工场景仍保持 `unverified`。
