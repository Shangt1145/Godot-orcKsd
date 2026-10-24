# 0.0.0beta7.hotfix.3（2026-10-24）

修复烟幕生命周期：进入前线或主动攻击时移除烟幕；前线单位不能重新获得烟幕；取消操作不会误清，撤回支援线也不会自动恢复。规则由引擎执行，单机和房主服务端共用，双方投影读取真实词条状态。

## 补丁

- 来源：`0.0.0beta7.hotfix.2`，构建 `06B41440A9753910E5B8D567280AE0F203E9E6525C94A7063DBADA30A74C7D74`。
- 目标：`0.0.0beta7.hotfix.3`，构建 `E86AF958D1AFB9F314C94F1914538DC030C4605514F88D1BCAB1E41F1F5DC435`。
- 文件：`patches/OrC-KSD-0.0.0beta7.hotfix.3-from-06B41440A975-to-E86AF958D1AF.orcupdate`。
- 大小：12,168,257 字节，约 11.6 MiB。

关闭双方游戏，使用“更新游戏.exe”应用补丁。补丁仅适用于上面的第二版热修复构建，联机双方均须升级。完整包与校验和分别位于 `full/` 和 `SHA256.txt`。

## 验证与边界

- 引擎全量 1360/1360；新增推进、攻击、取消、直接加入前线和重新赋予烟幕回归。原先假设前线或攻击后仍保留烟幕的旧夹具已按新规则修正。
- UI 全量 571/571；远端关键词变化测试确认烟幕移除后触发关键词反馈，不产生虚构伤害。门禁首次测试主机异常退出，随后完整重跑通过，没有跳过用例。
- 原生 UI 门禁通过；独立完整包和实际升级后的 EXE 由发行脚本检查，结果日志保存在本发行目录 `verification/`。
- 定向双端回归使用固定烟幕坦克牌检查支援线保留、前线移除、双方投影和界面标记一致；为固定夹具允许重复卡，正常房间仍使用严格卡组校验。
- 实际升级后的 EXE 使用随包服务端，在 Vulkan Forward+ 图形模式通过 `ROOM_SMOKESCREEN_VERIFY_OK support-keeps frontline-clears host-and-guest-projections UI-keyword-cleared`；普通严格卡组房间的部署、移动和攻击演出回归也通过。

实现与详细规则见 `docs/acceptance/SMOKESCREEN_LIFECYCLE_20261024.md`；没有将“攻击时移除”扩展为“以后永久禁止新效果重新赋予烟幕”。烟幕与隐蔽是独立机制。

本轮已在 GitHub 发布：<https://github.com/Shangt1145/Godot-orcKsd/releases/tag/v0.0.0beta7.hotfix.3>
（含增量补丁与 `SHA256.txt`；完整包 1.2 GB 未上传，走本地发行目录）。
仓库已打 tag `v0.0.0beta7.hotfix.3`。本机双端回归不等于跨机器、完整卡池长局验收。
