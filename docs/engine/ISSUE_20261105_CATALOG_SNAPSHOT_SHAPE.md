# 目录快照不带 builtInOps，而 DTO 要求它非空

- 日期：2026-11-05
- 引擎检出：`192c04b`（R40 之后）
- 状态：**已提交** → https://github.com/hitman-OLD6/OrC-KSD/issues/5
- 发现于：接 27 条契约 check 向量时（`HandlerCatalog.FromSnapshot`）

## 1. 一句话

契约包随包的 `catalog.snapshot.json` **没有 `builtInOps`**，而 `HandlerCatalogDto` 把它声明成
**非空**——**一份 schema 合法的快照，装不进引擎自己的 DTO**。

## 2. 三个事实

1. **随包快照没有这个字段**：`catalog.snapshot.json` 顶层键只有
   `schemaVersion`／`catalogRevision`／`scannedFaces`／`readables`／`nodes`（30 个节点）。
2. **序列化器从不写它**：`HandlerDslCatalogJson.Serialize`（`:27`）逐段写那五样，没有 `builtInOps`；
   `Deserialize` 也不读它。所以这不是「文件旧了」——**引擎能产出的任何快照都不带它**。
3. **DTO 要求它非空**：`HandlerCatalogDto(…, IReadOnlyList<HandlerBuiltinOpDto> BuiltInOps)`；
   而 `catalog.schema.json` 里它只在 `properties` 内、**不在 `required`**。

## 3. 实际后果

「一个目录」变成三种形状：

| 形状 | 谁产 | `builtInOps` | `stop`／`interrupt` |
| --- | --- | --- | --- |
| `HandlerDslCatalogJson.Serialize` 输出 | 序列化器 | ✗ | ✗（构造时重新合成） |
| `catalog.snapshot.json`／向量 `catalog` 段 | 契约包 | ✗ | ✗ |
| `HandlerCatalogDto` | `GetCatalog()` | ✓ | ✓ |

我们第一版想走「快照 → `HandlerCatalogDto` → 复用 `FromEngine`」，`BuiltInOps` 得到 null，
`dto.BuiltInOps.ToDictionary(...)` 直接 NRE，只好另写一份解析器。

更实质的：**只读契约包的前端拿不到内建 op 的能力分区**。`choice` 是 preview 的那段时间里，
这样的前端会把一个运行期必抛的 op 显示成可用——正是 R36 加这张表想解决的问题。

## 4. 顺带的文档漂移

- `FRONTEND-GUIDE.md:782`：`catalog.snapshot.json`「27 个节点」，实际 **30 个**。
- `vectors/README.md`：「19 条 check 向量」，实际 **27 条**。

## 5. 建议

**A（推荐）**：`Serialize` 写 `builtInOps`（10 条，alpha 也写——这张表很短），`Deserialize` 读它、
缺省时退回「全部 alpha」。这样契约包里的 mock 就是完整目录，前端不必为分区再问运行期。

**B**：若坚持快照不带分区，把 DTO 字段改成可空并在注释里写明「快照可能省略」。
现在的非空签名等于给每个「从快照建目录」的客户端埋一个 NRE。

**C**：至少把 §4 的两处数字对齐。

## 6. 我们的规避

`HandlerCatalog.FromSnapshot(JsonNode)`：内建 op 取本地表（10 条，alpha）、`stop`／`interrupt`
从本地镜像补、扫描节点按快照建、`builtInOps` 若快照显式带了才以它为准。
27 条 check 向量现已与后端结论逐字一致。
