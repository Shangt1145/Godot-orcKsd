using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Packages;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 一次对局要装配的卡包集合（迁移 S8）：**引擎宿主冻结的资产快照** ＋ 由该快照装载出的**包内容**。
/// 取代旧模型的自造 `PackContent` 列表——清单/择版/依赖/生命周期全部由引擎 `EngineHost` 负责。
/// </summary>
/// <param name="Snapshot">本局资产目录快照（对局内不可变；含卡包与引擎级运行期卡）。</param>
/// <param name="Contents">由快照逐包装载出的内容（定义集 / 效果声明 / 内联预制体 / 效果库 / 词条 / 文本）。</param>
public sealed record PackSet(AssetCatalogSnapshot Snapshot, IReadOnlyList<CardPackContent> Contents)
{
    /// <summary>由资产快照装载全部卡包（单包失败＝调用方决定隔离策略）。</summary>
    /// <exception cref="ArgumentNullException">snapshot 为 null。</exception>
    public static PackSet FromSnapshot(AssetCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new PackSet(
            snapshot,
            snapshot.CardPacks.Select(p => CardPackLoader.Load(p.RootDirectory, p.Manifest)).ToArray());
    }

    /// <summary>空集（无卡包）——仍带快照（插件/判定器/投影提供者需要它）。</summary>
    public static PackSet Empty(AssetCatalogSnapshot snapshot) => new(snapshot, []);

    /// <summary>本局全部卡定义：卡包定义（id 已带 `pack/&lt;包id&gt;/` 前缀）＋ 引擎级运行期卡。</summary>
    public IReadOnlyList<CardDefinitionEntry> Definitions
        => [.. Contents.SelectMany(c => c.Definitions),
            .. Snapshot.RuntimeCards.Select(r => new CardDefinitionEntry(r.Id, r.Definition))];
}
