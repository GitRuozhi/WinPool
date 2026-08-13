# WinPool V0.38B 补充修正归档

## 状态

- implemented
- automatic gates passed
- native/manual cases partially verified

## 修复内容

1. 本机系统默认选中后，比较表跨系统 objectId 不再导致崩溃。
2. Agent 托盘退出时，Main App 注册项超时后会从 process registry 清理。
3. 启动阶段 Agent 连接失败和工作区初始化失败通知去重。
4. Agent 控制服务改为线程池启动，避免 WinForms 同步上下文死锁导致 Agent 不退出。

## 验证

- Release build：0 warning，0 error。
- Release tests：全部通过。
- 依赖漏洞审计：通过。
- 托盘 Exit WinPool 后，WinPool.App 和 WinPool.Agent 均退出，endpoint 被移除。
