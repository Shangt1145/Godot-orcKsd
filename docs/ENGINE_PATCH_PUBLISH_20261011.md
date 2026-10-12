# 引擎 A.20 补丁发布

日期：2026-10-11。用户要求：修改过 OrC-KSD 引擎则推送 GitHub。

## 结果

- 实际远程为 [Shangt1145/OrC-KSD](https://github.com/Shangt1145/OrC-KSD)。请求中 `Shangt145/OrC-KSD` 的公开 API 返回 404；沿用此前指定及当前 `origin` 的仓库。
- 已正常推送至 `main`，没有强推或覆盖其他工作区。
- 提交：[7540b432c940e56283643adaa122cac6ee3cb14a](https://github.com/Shangt1145/OrC-KSD/commit/7540b432c940e56283643adaa122cac6ee3cb14a)，父提交 `8b0ae98158394cae264b4ed80dfa10fb506e7351`。
- 标题：`feat!: add action input admission and formation gap deployment`。
- 18 文件，258 行新增、44 行删除。推送后读取远程主分支，确认与本地 HEAD 相同；引擎工作区干净。
- Git 未配置提交身份，本次以命令级 `Codex <codex@localhost>` 工具身份提交，未设置全局或仓库用户配置。

## 范围与迁移

发布的是已完成并验证的 A.20 引擎扩展，A.21／A.22 的 Godot 菜单代码不在该提交中。

- `action.input.allowed` 通过 `Match.InteractionStateReader` 获取前端值状态，查询与提交均复验；前端只能收窄，不能扩大原生规则合法性。没有读者时保留无界面行为。
- `PlayManager.CanPlay` 提供纯入口可用性查询；最终提交仍复验规则与效果选择。
- 部署改为含总部的阵型插入空隙，稳定 `FormationGap` 绑定占用快照；陈旧或外线空隙在副作用前拒绝。原生插入更新单位与总部位置。
- 原有直接 `Slot` 部署重载保留；**单位预打出选择器候选已从空槽 `Slot` 引用变为 `FormationGap` 引用**，消费端必须按引擎提供的空隙身份提交，不自行映射存储槽。
- 迁移 4 个原生测试文件的部署候选和判定器元数据断言。

## 发布前检查

- 与保存的 `docs/engine-patches/6d2-action-portals.patch` 一致，反向 `git apply --check` 通过。
- 远端主分支在提交前仍为基础提交，未出现需合并的更新。
- `dotnet test OrcEngine.sln --no-restore`：1099 / 1099 通过。
- 客户端本轮 258 / 258 测试、完整 UI 回归及两种窗口的菜单专项已通过。
- 暂存路径精确核对为已审核的 18 文件，暂存 diff 空白检查通过。

测试证据：`artifacts/engine-publish-tests.log`、`artifacts/engine-publish/native-engine-publish.trx`。完整 A.20 行为验收和范围见 [STAGE_6D2_ACCEPTANCE.md](STAGE_6D2_ACCEPTANCE.md)，当前消费基线见 [ENGINE_INTEGRATION_BASELINE.md](ENGINE_INTEGRATION_BASELINE.md)。保存补丁仍以旧基础提交生成，用于历史恢复；拉取已包含 `7540b43` 的版本后无需再应用。
