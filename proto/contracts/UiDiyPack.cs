namespace Kards.Ui.Contracts;

public sealed record DiyArtRegion(float X,float Y,float Width,float Height);
public sealed record DiyNationDraft
{
    public string Id {get;init;}="nation-"+Guid.NewGuid().ToString("N");
    public string Name {get;init;}="新国家";
    public string IconFile {get;init;}="";
    public string Color {get;init;}="526b61";
}
public sealed record DiyKeywordDraft
{
    public string Id {get;init;}="kw-"+Guid.NewGuid().ToString("N");
    public string Name {get;init;}="新词条";
    public string Description {get;init;}="";
    public string EffectText {get;init;}="";
    public bool Battle {get;init;}
    public OrcEffectDraft[] Effects {get;init;}=[];
    public DiyKeywordDraft Snapshot()=>this with{Effects=Effects.Select(e=>e.Snapshot()).ToArray()};
}
public sealed record DiyPackDraft
{
    public string Id {get;init;}="pack/diy-"+Guid.NewGuid().ToString("N");
    public string Name {get;init;}="新卡包";
    public string? BasePackId {get;init;}
    public DiyNationDraft[] Nations {get;init;}=[];
    public DiyKeywordDraft[] Keywords {get;init;}=[];
    public bool IsExpansion=>BasePackId is not null;
    public DiyPackDraft Snapshot()=>this with{Nations=Nations.ToArray(),Keywords=Keywords.Select(k=>k.Snapshot()).ToArray()};
}
