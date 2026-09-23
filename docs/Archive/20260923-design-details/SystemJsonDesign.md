# 存储系统 JSON 文件设计

状态：2026-09-23 当前系统导入与导出的具体文件契约。产品能力见 [Product](Product.md)，模拟编辑规则见 [SimulationEditingDesign](SimulationEditingDesign.md)。

## 文件与入口

管理页的“导出系统”调用 `DesktopExportService.ExportAsync(StorageSystemDocument)`。保存对话框只提供 `JSON (*.json)`，默认扩展名为 `.json`，建议文件名由 `WinPool-`、清理非法文件名字符后的系统显示名和本地时间组成。用户取消选择器时返回空结果，不写文件、不报告成功。写入完成后才返回实际路径供页面显示结果。硬件页的只读快照导出也使用 `.json`；监控 CSV 是不同用途，不改成 JSON。

系统文件是 UTF-8、缩进排版的标准 JSON 文本，使用 `System.Text.Json` 和字符串枚举。顶层字段固定为 `Product`、`SchemaVersion`、`ExportedAt`、`System`；`Product` 为 `WinPool`，`SchemaVersion` 对应 `StorageSystemDocument.CurrentSchemaVersion`，`System` 是完整存储系统文档。扩展名不承载格式或版本信息，不另设二进制容器、压缩包或私有 `.winpool` 输出。结构随文档模型版本演进，不能把文件名当作校验依据。

```json
{
  "Product": "WinPool",
  "SchemaVersion": 3,
  "ExportedAt": "2026-09-23T12:00:00+08:00",
  "System": { "SchemaVersion": 3, "DisplayName": "example" }
}
```

示例只展示外壳与关键字段，不是可导入的完整系统。实际 `System` 还包含身份、来源事实和快照；不得凭此示例构造缺失对象的导入文件。

## 导入与校验

打开对话框接受新 `.json` 和旧 `.winpool` 扩展名；两者均按同一个 JSON 外壳解析，不为旧扩展名保留另一套写入格式。读取前拒绝超过 64 MiB 的文件。解析为 JSON 后先检查外壳与 `System` 对象、文档 ID/显示名、`Jobs` 及 `SourceFacts` 必需集合、集合条目和对象字段集合的形状，再反序列化；缺失或空条目应给明确格式错误，不能让模型构造过程抛出空引用异常。随后核对 `Product`、外层和内层 schema 版本、计算机稳定 ID，以及各存储对象稳定 ID 非空且跨类别不重复。校验失败不将文档放入目录。成功导入转为可编辑模拟系统；文件中的本机身份不能使导入结果获得真实写入权限。

## 人工核对

1. 选择模拟系统导出，保存对话框只显示 JSON 类型；保存后用普通文本编辑器打开，确认顶层外壳及缩进内容。
2. 以刚导出的 `.json` 导入，核对系统身份、对象及关系；旧 `.winpool` JSON 文件只做兼容读入。
3. 取消保存或打开选择器时，无成功反馈或新文档；超大、版本不符、重复 ID 文件明确拒绝。
4. 导出和导入操作不触发真实存储结构修改；校验只针对文件与模拟文档。
