using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

[Collection("Workshop")]
public sealed class BuiltInWorkshopTests : IDisposable
{
    private const string Source=@"H:\Working Folder\OrC-KSD.Godot\proto\data\nations";
    private const string Native=@"H:\Working Folder\OrC-KSD.Godot\proto\data\kards";
    private readonly string root=Path.Combine(Path.GetTempPath(),"OrC BuiltIn Edit "+Guid.NewGuid().ToString("N"));
    private readonly CardCatalog catalog=new();
    private readonly DiyWorkshopLibrary store;
    private readonly DiyCardLibrary cards;
    public BuiltInWorkshopTests()
    {
        Directory.CreateDirectory(root);store=new(Path.Combine(root,"packs.json"));cards=new(Path.Combine(root,"cards.json"));
        OrcEngineHost.Reset();OrcEngineHost.WarmUp(Native);catalog.Load(Source);
        var loaded=OrcEngineHost.LoadPackCards(Native);Assert.Empty(loaded.Failures);catalog.SetPackCards(loaded.Cards);
        WorkshopAuthoring.Configure(store,[]);
    }
    [Fact]
    public void ModifyCatalogIncludesSourceNativeBaseAndEveryExpansionWithoutDuplicateLocalEdits()
    {
        var packs=BuiltInWorkshopCatalog.Packs(store);
        Assert.Contains(packs,p=>p.Id=="source"&&!p.IsExpansion);
        Assert.Contains(packs,p=>p.Id=="pack/builtin"&&!p.IsExpansion);
        Assert.Contains(packs,p=>p.Id=="pack/exp-winterwar"&&p.BasePackId=="pack/builtin");
        foreach(var descriptor in OrcEngineHost.PackDescriptors)Assert.Contains(packs,p=>p.Id=="pack/"+descriptor.Id);
        var original=packs.Single(p=>p.Id=="pack/builtin");store.Save(original with{Name="本地修改原版"});
        Assert.Equal("本地修改原版",BuiltInWorkshopCatalog.Packs(store).Single(p=>p.Id==original.Id).Name);
        var reopened=new DiyWorkshopLibrary(Path.Combine(root,"packs.json"));Assert.Null(reopened.LoadError);
        Assert.Equal("本地修改原版",Assert.Single(reopened.Packs).Name);
    }
    [Fact]
    public void NativeAndSourceDeclarationsLoadWithTheirActualProgramAndArtReference()
    {
        store.Save(BuiltInWorkshopCatalog.Packs(store).Single(p=>p.Id=="pack/exp-winterwar"));WorkshopAuthoring.Configure(store,[]);
        var original=catalog.Cards.Single(c=>c.CardId=="pack/exp-winterwar/through_the_ice");
        var draft=BuiltInWorkshopCatalog.Edit(original,"pack/exp-winterwar",Source);
        Assert.Equal(original.CardId,draft.SourceCardId);Assert.Equal(original.CardId,draft.ArtCardId);
        Assert.NotEmpty(draft.OrcEffects);Assert.All(draft.OrcEffects,e=>Assert.NotNull(e.DesktopJson));
        var result=DiyCardCompiler.Validate(draft,catalog.Cards);Assert.True(result.Success,string.Join(";",result.Errors));
        var source=catalog.Cards.Single(c=>c.CardId=="USG/commands/_7");
        Assert.NotEmpty(BuiltInWorkshopCatalog.Edit(source,"source",Source).OrcEffects);
    }
    [Fact]
    public void EditedNativeCardRetainsOriginalIdInStorageCatalogConstructionAndReload()
    {
        var pack=BuiltInWorkshopCatalog.Packs(store).Single(p=>p.Id=="pack/exp-winterwar");store.Save(pack);
        var original=catalog.Cards.Single(c=>c.CardId=="pack/exp-winterwar/432_infantry_regiment");
        var draft=BuiltInWorkshopCatalog.Edit(original,pack.Id,Source) with{Name="修改后的步兵",Attack=8};cards.Save(draft);
        WorkshopAuthoring.Configure(store,cards.Cards);
        var service=new DeckConstructionService(catalog.Cards,Source);service.UpdatePacks(catalog.Cards.Where(c=>c.CardId.StartsWith("pack/")).ToArray());service.UpdateCustom(cards.Cards);
        Assert.Empty(service.CustomErrors);Assert.Equal(8,service.Cards.Single(c=>c.CardId==original.CardId).BaseAttack);
        Assert.DoesNotContain(service.Cards,c=>c.CardId==draft.Id);Assert.Contains(original.CardId,service.PlayableIds);
        catalog.SetCustomCards(service.CustomCards);Assert.Equal("修改后的步兵",catalog.Cards.Single(c=>c.CardId==original.CardId).Name);
        var reopened=new DiyCardLibrary(Path.Combine(root,"cards.json"));Assert.Null(reopened.LoadError);Assert.Equivalent(draft,Assert.Single(reopened.Cards));
        service.UpdateCustom([]);catalog.SetCustomCards([]);Assert.Equal(original,catalog.Cards.Single(c=>c.CardId==original.CardId));
    }
    [Theory]
    [InlineData("pack/exp-winterwar/through_the_ice","pack/exp-winterwar")]
    [InlineData("USG/commands/_7","source")]
    public async Task EditedProgramReplacesOriginalExactlyOnceInNativeMatch(string id,string packId)
    {
        store.Save(BuiltInWorkshopCatalog.Packs(store).Single(p=>p.Id==packId));
        var draft=BuiltInWorkshopCatalog.Edit(catalog.Cards.Single(c=>c.CardId==id),packId,Source)
            with {Name="改为抽一张",Cost=0,EffectText="",OrcEffects=[],Effects=[new(){Op="draw",Amount=1}],UseStructuredEffects=true,DesktopCardJson=null};
        cards.Save(draft);WorkshopAuthoring.Configure(store,cards.Cards);
        using var session=await OrcMatchSession.CreateFromCatalogAsync(catalog.Cards,31,"local-edit-"+Guid.NewGuid().ToString("N"),"unused",
            sourceDirectory:Source,openingHand:[id],customCards:cards.Cards,firstPlayerIndex:0);
        session.DriveOpponent=false;await session.SettleMulliganAsync(false);
        var actor=session.View.SelfHand.Single(c=>c.CardId==id);Assert.Equal(id,actor.CardId);
        var before=session.View.SelfHand.Count;
        Assert.Equal(UiSubmitOutcome.Applied,await session.SubmitAsync(new PlayCard(actor.Uid)));
        session.SettleProjection();Assert.Equal(before,session.View.SelfHand.Count);Assert.False(session.TargetPending);
    }
    public void Dispose()
    {WorkshopAuthoring.Configure(new DiyWorkshopLibrary(Path.Combine(root,"empty.json")),[]);OrcEngineHost.Reset();Directory.Delete(root,true);}
}
