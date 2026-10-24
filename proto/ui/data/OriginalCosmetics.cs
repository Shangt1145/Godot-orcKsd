using System.Text.Json;
using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>Original KARDS artwork only. The manifest records package paths, source hashes and alpha crops.</summary>
internal static class OriginalCosmetics
{
    public static void StyleButton(Button button)
    {
        foreach(var state in new[]{"normal","hover","pressed","disabled","focus"})
        {
            var texture=Ui(state=="disabled"?"T_nui-buttons-background-primary-disabled-4096":"T_nui-buttons-background-secondairy-4096");
            if(texture is null)continue;
            button.AddThemeStyleboxOverride(state,new StyleBoxTexture{Texture=texture,TextureMarginLeft=12,TextureMarginRight=12,TextureMarginTop=12,TextureMarginBottom=12,ModulateColor=state=="pressed"?new Color("60583e"):new Color("30362f")});
        }
        button.AddThemeFontOverride("font",Font());
    }

    private const string Root="res://proto/art/original-cosmetics/";
    private sealed record Entry(string Kind,string Name,string Image,string Resource,string Payload="");
    public sealed record EmoteEntry(string Id,string Label,string Image,string Style,string Tint,string OriginalId,string Title,string[] Nations,string Group,string Resource,string Payload,string EnemyImage="")
    {
        public CosmeticEmote Emote=>new(Id,Label,Image,Style,Tint,EnemyImage);
    }
    private sealed record PropEntry(string Image,string Name,string[] Parts);
    private static Dictionary<string,string[]> _props=new();
    public static string[] PropParts(string hash)=>_props.GetValueOrDefault(hash)??[];
    private static Dictionary<string,string> _ui=new();
    private static readonly Dictionary<string,string> PayloadsByName=new();
    private static readonly Dictionary<string,Texture2D> UiTextures=new();
    private static readonly Dictionary<string,Font> Fonts=new();
    private static readonly HashSet<string> HqImages=[];
    private static readonly Dictionary<string,string> HqNames=new();
    private static readonly Dictionary<string,string> BackDefaults=new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<EmoteEntry> Emotes {get;private set;}=[];
    public static bool Available {get;private set;}
    private static T Read<T>(string name)=>JsonSerializer.Deserialize<T>(Godot.FileAccess.GetFileAsString(Root+name))??throw new InvalidDataException("原版外观目录为空："+name);
    public static void Register(CosmeticLibrary library)
    {
        if(!Godot.FileAccess.FileExists(Root+"runtime-catalog.json"))throw new FileNotFoundException("缺少原版资源，请先运行原版素材同步与提取工具。");
        var entries=Read<Entry[]>("runtime-catalog.json");_ui=Read<Dictionary<string,string>>("ui.json");Emotes=Read<EmoteEntry[]>("emotes.json");
        _props=Read<PropEntry[]>("props.json").ToDictionary(p=>p.Image,p=>p.Parts);
        foreach(var entry in entries)
        {
            PayloadsByName[entry.Name]=entry.Payload;
            if(entry.Kind=="emote")continue;
            var payload=entry.Payload;
            library.RegisterBuiltIn(new(entry.Kind,entry.Name,entry.Image,true),()=>Godot.FileAccess.GetFileAsBytes(payload));
            if(entry.Kind=="hq"){HqImages.Add(entry.Image);HqNames[entry.Name]=entry.Image;}
        }
        var native=Read<JsonElement[]>("native-provenance.json");
        foreach(var entry in native)
        {
            var image=entry.GetProperty("Image").GetString()!;var payload=entry.GetProperty("Payload").GetString()!;
            if(Emotes.Any(e=>e.EnemyImage==image))library.RegisterBuiltIn(new("emote-variant",entry.GetProperty("Name").GetString()!,image,true),()=>Godot.FileAccess.GetFileAsBytes(payload));
        }
        foreach(var emote in Emotes)
        {
            var payload=emote.Payload;
            library.RegisterBuiltIn(new("emote",emote.Title,emote.Image,true),()=>Godot.FileAccess.GetFileAsBytes(payload));
        }
        foreach(var (nation,name) in new[]{("germany","BasicGermanA"),("britain","BasicBritainA"),("usa","UsaBasicA"),("soviet","SovietBasicA"),("japan","JapanBasicA"),("","BasicBetaA")})
        {
            var back=entries.FirstOrDefault(e=>e.Kind=="back"&&e.Name==name)?.Image??entries.First(e=>e.Kind=="back").Image;
            BackDefaults[nation]=back;
            library.RegisterDefault(nation,new(){CardBack=back,Emotes=Defaults(nation)});
        }
        Available=true;
    }
    public static CosmeticEmote[] Defaults(string nation)
    {
        string[] ids=["emote_hello","emote_cool_deck","emote_mistake","emote_thanks","emote_good_game","emote_hurt","emote_glhf","emote_gg"];
        return ids.Select(id=>Emotes.FirstOrDefault(e=>e.OriginalId==id)?.Emote).OfType<CosmeticEmote>().ToArray();
    }
    public static bool IsHeadquarters(string hash)=>HqImages.Contains(hash);
    public static string DefaultHq(string? nation)
    {
        var name=nation?.ToLowerInvariant() switch{"japan"=>"Truk_HQ-04","britain"=>"WaW_London_HQ_art-24-24","usa"=>"WaW_Washington-05-05","soviet"=>"StalingradCardGraphic-01",_=>"T_hq_bunker_generic"};
        return HqNames.GetValueOrDefault(name)??HqNames.Values.FirstOrDefault()??"";
    }
    public static string DefaultBack(string? nation)=>BackDefaults.GetValueOrDefault(nation??"")??BackDefaults.GetValueOrDefault("")??"";
    public static Texture2D? Ui(string name)
    {
        if(UiTextures.TryGetValue(name,out var existing))return existing;
        if(!_ui.TryGetValue(name,out var path)&&!PayloadsByName.TryGetValue(name,out path))return null;
        var image=new Image();if(image.LoadPngFromBuffer(Godot.FileAccess.GetFileAsBytes(path))!=Error.Ok)return null;
        return UiTextures[name]=ImageTexture.CreateFromImage(image);
    }
    public static Font Font(string name="NotoSansSC-Regular")
    {
        if(Fonts.TryGetValue(name,out var existing))return existing;
        var path=Root+"fonts/"+name+".ttf.bin";
        var font=new FontFile{Data=Godot.FileAccess.GetFileAsBytes(path)};
        return Fonts[name]=font;
    }
    public static Texture2D? DefaultPaper=>Ui("new_emote_base_1-01");
}
