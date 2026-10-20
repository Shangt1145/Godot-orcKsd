# Beta.1 卡包、机制补齐与 Windows 游戏导出

创建卡组前多选卡包。扩展包是同一卡包的附加内容，自动包含，不出现单独的扩展包选择项。当前默认选择项为“太空战争”和“内置卡包”；DIY、导入卡通过完整编译后按其来源显示。旧卡组的扩展包 ID 自动归并至父卡包。

当前可用池 **240 种**：参考卡 24 种、原版卡 216 种。原版由之前 99 种增加 117 种，分布于 13 个内容集合；所有 216 种均有官方中文卡图。原始快照共 1646 条，包含衍生牌等记录；216 不代表原版全量实现，语法解析完整也不等于实战机制全部实现。缺失机制保留为待实现工作，不以剔除卡牌作为完成标准。

## 本轮实际补齐

- 完整 OrC 解析管线替代旧的少量正则预检生成器；官方属性、词条、效果图经原生编译、实例化、资产往返校验后生成卡包。
- 正确拆分“单位和总部”：单位可选双方，独立结算指定总部伤害；选靶取消发生在扣费前，不扣总部血、不抽牌。
- 总部、双方总部、所有目标和敌方所有目标查询接入 OrC-KSD 原生效果选靶判定器，组合查询去重；单位查询仍不会选中总部。
- 总部增加防御力经原生数值修饰面执行，不再把总部传入只支持单位的分支后静默丢弃。
- 原生引擎新增 card.dying 宿主死亡结算阶段，先于效果卸载；原 card.died 继续在清理完成后通知观察者。亡计不再因先卸载而失效，同一死亡不重复结算。
- 回合开始、回合结束、受伤监听、条件部署、牺牲抽牌有实际对局回归。

执行逻辑在相邻 OrC-KSD 引擎源码工程中，Godot 仍只处理输入、展示和效果声明适配。本轮引擎修改在 `H:/Working Folder/OrC-KSD-github/src/Orc.Game/`，尚未发布远端。

## 数据生成与验证

`python tools/build_builtin_packs.py` 或 `dotnet run --project tools/OfficialCards -- <工程根目录> --refresh --write` 使用同一完整编译管线刷新生成卡。报告为 `artifacts/official-card-expansion.json`，缺口记录用于继续实现；不会覆写旧的原始解析基线。

- 1553 条解析语料：1540 条保持原始完整输出，13 条总部语义修复单独审核并保存回归哈希；698 条完整解析。
- `OfficialCardExpansionTests`：实际验证友敌单位选靶、取消、总部伤害/增益、全目标亡计、单次死亡、回合触发、条件部署和受伤抽牌。
- `--verify-export`：全 216 种原版卡分批进入原生对局，精确名单和效果挂载，216 张卡图、内置解析器、卡组多选、保存重开、真实开局和换牌。
- `--verify-ui`：完整原生界面回归。内部源目录 531 种与实际可用池 240 种是不同口径。

## 导出

`tools/release.ps1` 使用 Godot 4.7 .NET 匹配模板，导出后强制解压 ZIP 并运行实际玩家 EXE；必须退出成功且输出 EXPORT_VERIFY_OK 才交付。包括本地数据、卡图、OrC-KSD、ClearScript 和 CLR 运行库，不需要网页或玩家安装 SDK。

修复 Godot 导出脚本类型隔离、PCK JSON 读取和生成目录迁移残留。构建目录只清理已验证归属的生成卡包数据，不修改来源素材。成品为 `dist/OrC-KSD-0.0.0-beta.1-win-x64.zip`；验证日志和校验和位于同目录。

## 2026-10-17 验收结果

发布专项 129/129、OrC-KSD 原生引擎 1101/1101 通过。Godot 完整回归得到 `UI_VERIFY_OK screens=9 cards=531 texture_cache=64/64`。新版 ZIP 解压后独立 EXE 验证得到 `EXPORT_VERIFY_OK playable=240 pack-cards=216 all-pack-effects-mounted artwork=216`。ZIP SHA-256：`A9323DBB2529F5ACCC275A94969C984A40C49D3B2D12493CB2A46281A2BEF27E`。日志分别为 `artifacts/official-mechanics-release-gate.log`、`artifacts/native-mechanics-regression.log`、`artifacts/official-mechanics-ui.log` 和 `dist/verify-export.log`。
