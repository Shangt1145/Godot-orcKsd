using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kards.Ui.Core;

public sealed record CosmeticEmote(string Id, string Label, string Image = "", string Style="diy", string Tint="433a29", string EnemyImage="");
public sealed record CosmeticProfile
{
    // Product capacity, not a claim about every original-client version.
    public const int MaximumEmotes = 8;
    public string CardBack { get; init; } = "";
    public string Decoration { get; init; } = "";
    public string Headquarters { get; init; } = "";
    public string HeadquartersName { get; init; } = "";
    public CosmeticEmote[] Emotes { get; init; } = DefaultEmotes();
    public static CosmeticEmote[] DefaultEmotes() => [new("hello", "你好！"), new("well-played", "打得漂亮"), new("thanks", "谢谢"), new("thinking", "让我想想"), new("deck", "漂亮的卡组"), new("mistake", "那是个错误"), new("surprised", "你在开玩笑吗？"), new("good-game", "一场光荣的战斗")];
    public IEnumerable<string> Images() => new[] { CardBack, Decoration, Headquarters }.Concat(Emotes.SelectMany(e => new[]{e.Image,e.EnemyImage})).Where(s => s.Length > 0).Distinct();
    public CosmeticProfile Checked()
    {
        if (HeadquartersName.Length > 24 || HeadquartersName.Any(char.IsControl) || Emotes.Length is < 1 or > MaximumEmotes
            || Emotes.Any(e => e.Id.Length is < 1 or > 48 || e.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || string.IsNullOrWhiteSpace(e.Label) || e.Label.Length > 160 || e.Label.Any(c=>char.IsControl(c)&&c!='\n')
                || e.Style is not ("paper" or "special" or "diy") || e.Tint.Length!=6 || !e.Tint.All(char.IsAsciiHexDigit))
            || Emotes.Select(e => e.Id).Distinct().Count() != Emotes.Length || Images().Any(id => !CosmeticImage.IsId(id)))
            throw new InvalidDataException($"外观资料无效：总部名称最多 24 字，表情文字最多 160 字，表情 1–{MaximumEmotes} 个。");
        return this;
    }
}
public static class CosmeticImage
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public static bool IsId(string id) => id.Length == 64 && id.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c));
    public static (string Id, int Width, int Height) Inspect(byte[] png)
    {
        if (png.Length is < 33 or > MaximumBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })
            || !png.AsSpan(12,4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("需要不超过 2 MiB 的 PNG 图片。");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException("外观图片尺寸须为 1–4096 像素。");
        return (Convert.ToHexString(SHA256.HashData(png)), width, height);
    }
}
public sealed record CosmeticAsset(string Kind, string Name, string Image, bool BuiltIn=false, string Nation="");
public sealed class CosmeticLibrary
{
    private sealed record Saved(CosmeticProfile Profile, CosmeticAsset[] Assets,
        Dictionary<string,CosmeticProfile>? Decks = null, Dictionary<string,CosmeticProfile>? Nations = null, int Schema = 1);
    private readonly string _root;
    private CosmeticProfile _legacy = new();
    private Dictionary<string,CosmeticProfile> _decks = new(StringComparer.Ordinal), _nations = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _deckId, _nation;
    private bool _legacyPending;
    public CosmeticProfile Profile { get { lock(_gate) return Resolve(_deckId,_nation); } }
    public IReadOnlyList<CosmeticAsset> Assets => _builtInAssets.Concat(_assets).ToArray();
    private readonly List<CosmeticAsset> _builtInAssets=[];
    private readonly Dictionary<string,Func<byte[]>> _builtInImages=new(StringComparer.Ordinal);
    private readonly Dictionary<string,CosmeticProfile> _defaults=new(StringComparer.OrdinalIgnoreCase);
    public void RegisterDefault(string nation,CosmeticProfile profile)=>_defaults[nation]=profile.Checked();
    private readonly List<CosmeticAsset> _assets = [];
    public string? LoadError { get; private set; }
    public CosmeticLibrary(string root)
    {
        _root = Path.GetFullPath(root); Directory.CreateDirectory(Path.Combine(_root,"images"));
        var path = Path.Combine(_root,"appearance.json");
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) ?? throw new InvalidDataException("外观资料为空。");
            if(saved.Schema is not (1 or 2)) throw new InvalidDataException("未知外观资料版本。");
            var legacy = saved.Profile.Checked();
            var decks = saved.Decks ?? new(); var nations = saved.Nations ?? new();
            foreach(var p in decks.Values.Concat(nations.Values)) p.Checked();
            var assets = saved.Assets.Where(a => a.Kind is "back" or "decoration" or "hq" or "emote" && CosmeticImage.IsId(a.Image)).ToArray();
            _legacy = legacy; _legacyPending = saved.Schema == 1;
            _decks = decks; _nations = nations; _assets.AddRange(assets);
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException or NullReferenceException or InvalidOperationException)
        { LoadError = "外观资料未能读取，已使用默认外观；原文件保留：" + e.Message; }
    }
    /// <summary>Legacy global settings attach to the first selected deck, never to every future deck.</summary>
    public void SetContext(string? deckId, string? mainNation)
    {
        lock(_gate)
        {
            _deckId=deckId; _nation=mainNation;
            if(!_legacyPending || string.IsNullOrWhiteSpace(deckId) || string.IsNullOrWhiteSpace(mainNation)) return;
            _decks[deckId]=DeckPart(_legacy); _nations[mainNation]=NationPart(_legacy);
            _legacy=new(); _legacyPending=false;
        }
    }
    public CosmeticProfile Resolve(string? deckId,string? mainNation)
    {
        lock(_gate)
        {
            if(string.IsNullOrWhiteSpace(deckId) || string.IsNullOrWhiteSpace(mainNation)) return _legacy;
            var defaults=_defaults.GetValueOrDefault(mainNation)??_defaults.GetValueOrDefault("")??new();
            var deck=_decks.GetValueOrDefault(deckId)??new(); var nation=_nations.GetValueOrDefault(mainNation)??defaults;
            return deck with { CardBack=deck.CardBack.Length>0?deck.CardBack:defaults.CardBack, Decoration=nation.Decoration, Emotes=nation.Emotes.ToArray() };
        }
    }
    private static CosmeticProfile DeckPart(CosmeticProfile p) => new(){CardBack=p.CardBack,Headquarters=p.Headquarters,HeadquartersName=p.HeadquartersName};
    private static CosmeticProfile NationPart(CosmeticProfile p) => new(){Decoration=p.Decoration,Emotes=p.Emotes.ToArray()};
    public string ImagePath(string id)
    {
        if (!CosmeticImage.IsId(id)) throw new InvalidDataException("无效的外观图片编号。");
        return Path.Combine(_root,"images",id+".png");
    }
    public void RegisterBuiltIn(CosmeticAsset asset,Func<byte[]> readImage)
    {
        if(!CosmeticImage.IsId(asset.Image))throw new InvalidDataException("原版资源哈希无效。");
        _builtInImages[asset.Image]=readImage;
        if(_builtInAssets.All(a=>a.Kind!=asset.Kind||a.Image!=asset.Image))_builtInAssets.Add(asset with{BuiltIn=true});
    }
    public bool HasImage(string id) => CosmeticImage.IsId(id) && (_builtInImages.ContainsKey(id)||File.Exists(ImagePath(id)));
    public byte[] ReadImage(string id) => _builtInImages.TryGetValue(id,out var read)?read():File.ReadAllBytes(ImagePath(id));
    public string CacheImage(byte[] png)
    {
        var info = CosmeticImage.Inspect(png); var path = ImagePath(info.Id);
        if (!File.Exists(path)) { var temp = path+"."+Guid.NewGuid().ToString("N")+".tmp"; File.WriteAllBytes(temp,png); File.Move(temp,path,true); }
        return info.Id;
    }
    public CosmeticAsset Import(string kind, string name, byte[] png)
    {
        if (kind is not ("back" or "decoration" or "hq" or "emote")) throw new InvalidDataException("未知外观分类。");
        var asset = new CosmeticAsset(kind, string.IsNullOrWhiteSpace(name) ? "自定义图片" : name.Trim()[..Math.Min(name.Trim().Length,48)], CacheImage(png));
        if (_assets.All(a => a.Kind != kind || a.Image != asset.Image)) _assets.Add(asset);
        Save(Profile); return asset;
    }
    public void Save(CosmeticProfile profile)
    {
        lock(_gate) SaveFor(_deckId,_nation,profile);
    }
    public void SaveFor(string? deckId,string? mainNation,CosmeticProfile profile)
    {
        lock(_gate)
        {
        profile.Checked();
        if (profile.Images().Any(id => !HasImage(id))) throw new InvalidDataException("装备的图片已缺失，请重新导入。");
        var decks=new Dictionary<string,CosmeticProfile>(_decks,StringComparer.Ordinal);
        var nations=new Dictionary<string,CosmeticProfile>(_nations,StringComparer.Ordinal);
        var legacy=_legacy;
        if(!string.IsNullOrWhiteSpace(deckId) && !string.IsNullOrWhiteSpace(mainNation))
        { decks[deckId]=DeckPart(profile); nations[mainNation]=NationPart(profile); }
        else legacy=profile;
        var path = Path.Combine(_root,"appearance.json"); var temp = path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new Saved(legacy,_assets.ToArray(),decks,nations,2),new JsonSerializerOptions { WriteIndented=true }));
        File.Move(temp,path,true); _legacy=legacy; _decks=decks; _nations=nations;
        }
    }
    public void Remove(CosmeticAsset asset)
    {
        if(asset.BuiltIn)throw new InvalidOperationException("原版资源不能从内置目录删除。");
        _assets.RemoveAll(a => a.Kind == asset.Kind && a.Image == asset.Image);
        CosmeticProfile Reset(CosmeticProfile p) => asset.Kind switch
        {
            "back" when p.CardBack == asset.Image => p with { CardBack="" },
            "decoration" when p.Decoration == asset.Image => p with { Decoration="" },
            "hq" when p.Headquarters == asset.Image => p with { Headquarters="" },
            "emote" => p with { Emotes=p.Emotes.Select(e => e.Image == asset.Image ? e with { Image="" } : e).ToArray() }, _ => p
        };
        lock(_gate)
        {
            var oldDecks=_decks; var oldNations=_nations; var oldLegacy=_legacy;
            _decks=_decks.ToDictionary(p=>p.Key,p=>Reset(p.Value),StringComparer.Ordinal);
            _nations=_nations.ToDictionary(p=>p.Key,p=>Reset(p.Value),StringComparer.Ordinal); _legacy=Reset(_legacy);
            try { Save(Profile); }
            catch { _decks=oldDecks; _nations=oldNations; _legacy=oldLegacy; _assets.Add(asset); throw; }
        }
    }
}
