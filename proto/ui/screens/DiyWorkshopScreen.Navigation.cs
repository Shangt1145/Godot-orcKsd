using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    internal void ShowHome()
    {
        if (_nativeDirty) { GuardNative(ShowHome); return; }
        _workspace.Hide(); _editor.Hide(); _navigation.Show(); _pack = null; _compatiblePack = null;
        _notice.Text = _store.LoadError ?? "";
        BuildProjectDashboard();
    }
    private string PackSummary(DiyPackDraft pack)
    {
        var cards=_service.Cards.Where(c=>DeckCardPacks.RawOf(c.CardId)==pack.Id).ToArray();
        var drafts=_cards.Cards.Where(c=>c.PackId==pack.Id).ToArray();
        var count=cards.Select(c=>c.CardId).Concat(drafts.Select(c=>c.RuntimeId)).Distinct().Count();
        var playable=cards.Count(c=>_service.PlayableIds.Contains(c.CardId));
        var parent=BuiltInWorkshopCatalog.Packs(_store).FirstOrDefault(p=>p.Id==pack.BasePackId);
        return $"{count} 张卡牌 · {playable} 张可用\n自定义国家 {pack.Nations.Length+(parent?.Nations.Length??0)} · 词条 {pack.Keywords.Length+(parent?.Keywords.Length??0)}"
            +(pack.IsBuiltIn?$"\n内置 / 自带 · 已修改 {drafts.Count(c=>c.SourceCardId.Length>0)} 张":"")
            +(pack.IsExpansion?"\n包含基础卡包的继承内容":"");
    }
}
