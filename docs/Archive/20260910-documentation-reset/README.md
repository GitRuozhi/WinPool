# 2026-09-10 文档重构原件

状态：原文件已被当前中文权威和 V0.48 Plan 替代，保存供追溯，不作为执行要求。

代码与原件基线：`39f0d4eb3c141b169632976e598754b299a5a997`，文档调整前已推送远端。`original/` 保留原仓库相对路径，18 份文件移动时逐一核对 SHA-256，未修改正文。

- [原 Agent 规则](original/AGENTS.md)及其中文副本；原 README 双语。
- [原 Product](original/docs/Product.md)、[原 Development](original/docs/Development.md)、[原 Quality](original/docs/Quality.md)及其中文副本。
- [原 V0.47 控件 Plan](original/docs/Plan.md)及其中文副本。
- [原产品决策记录](original/docs/Storage-Structure-Product-Decisions.md)及其中文副本。
- [完整旧 CHANGELOG 英文原件](original/docs/CHANGELOG.md)和[中文原件](original/docs/CHANGELOG.zh-CN.md)。
- [原归档索引](original/docs/Archive/README.md)及其中文副本。

原件内链接保留原仓库语境；指向本快照以外的链接可能无法从这里直接访问。需要原上下文时，在源提交查看对应仓库路径；本目录不是完整源码副本，不用补拷贝整个旧 Archive。当前导航从[上级索引](../README.md)进入。

## 对原 V0.47 状态的后加说明

旧 Plan 自述“未开始实施”已经落后于代码。到基线提交，控件与编辑逻辑已有多次实现和修正；2026-09-10 审查确认提交序列、分区单独修改、未知结果传播、容量和对象关联等缺陷。旧 Plan 被替代不等于实现未发生，也不等于阶段所有项目已验收通过。未取得的人工/原生证据保持未验证。

旧产品记录中单盘 Mirror 放宽等决定被用户新的 Windows 合法性要求覆盖；已采纳的产品边界移入当前 Product。正文保留原样，不能根据旧文本或旧测试重新放行。

两份大型前瞻方案不是失效历史，现移至 `docs/Design`，未纳入 V0.48，可在实施前继续调整。
