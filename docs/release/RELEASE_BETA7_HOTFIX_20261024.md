# 0.0.0beta7.hotfix（2026-10-24）

修复真人联机时一方确认换牌后反复弹出换牌流程，以及单位拖牌无法获得有效部署落点的问题。

- 确认换牌后立即保持等待状态，投影刷新不再打断退牌动画或再次打开面板；服务端按座位报告是否仍需换牌。
- 服务端部署空隙改为 UI 契约要求的编号、位置和左右邻牌结构，客户端与随包服务端同时更新。
- 发行全量门禁发现语料编译继承前次请求卡池的顺序依赖，已补充可稳定复现的回归并在语料请求入口初始化卡池；不更新原始语料基准哈希。

## 补丁与使用

- 来源：已发布 `0.0.0-beta.7`，构建 `8597230D4C78CD23E0E0E4D09BE5B4351DF211FBDF2330F40EF11E7FAF9FD996`。
- 目标：`0.0.0beta7.hotfix`，构建 `C47D33C441C073B4AB52371674BE1544481AF25E98DDAB9AAFCDD1EDE84DA0CE`。
- 补丁：`dist/releases/0.0.0beta7.hotfix/patches/OrC-KSD-0.0.0beta7.hotfix-from-8597230D4C78-to-C47D33C441C0.orcupdate`，101,037,166 字节（96.4 MiB）。
- 关闭游戏，用 beta.7 的“更新游戏.exe”选择游戏文件夹和上述补丁。联机双方都应升级，房主使用补丁携带的新服务端。
- 补丁只接受上面列出的 beta.7 构建；其他版本使用完整包，不能强行套用此补丁。

补丁改变 6 个文件：`KardsUi.exe`、`KardsUi.pck`、客户端的 `Kards.Ui.dll` 与 `Kards.Ui.OrcBridge.dll`、服务端的 `Orc.Mcp.dll` 与 `Orc.Mcp.pdb`。

## 验证

- UI 全量单测 566/566；原生 `UI_VERIFY_OK` 通过。
- 前轮 Orc.Mcp 全量单测 121/121，含换牌座位状态及部署空隙结构回归。
- 完整包独立启动 `EXPORT_VERIFY_OK` 与工坊 `WORKSHOP_VERIFY_OK` 通过。
- 实际离线更新 `GODOT_UPDATER_VERIFY_OK`；更新后逐文件一致性 `PATCH_EXACT_FILES_OK`，玩家自有文件校验保持一致；升级后的 EXE 再次通过 `EXPORT_VERIFY_OK`。
- 在实际升级后的 EXE 上，清除开发服务端路径覆盖，使用随包服务端复测“替换一张”和“保留全部”：两种场景均通过 `ROOM_REMOTE_VERIFY_OK ... actual-drag-deploy`，覆盖等待另一位真人、投影在退牌期间到达、进入对局、真实输入事件拖牌与场上部署结果。
- 本轮仍是本机两条真人连接验证，没有跨机器组网或完整长局验收。

最终校验和见发行目录 `SHA256.txt`；发行日志及升级后联机回归日志归档在其 `verification/` 目录。仅导出到本地，没有上传或推送。
