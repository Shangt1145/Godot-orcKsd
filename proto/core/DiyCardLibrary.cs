using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>Atomic local authoring storage. Stored drafts are recompiled before admission to a match.</summary>
public sealed class DiyCardLibrary
{
    private sealed record FileData(int Schema, DiyCardDraft[] Cards);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private List<DiyCardDraft> _cards = [];
    public string? LoadError { get; private set; }
    public IReadOnlyList<DiyCardDraft> Cards => _cards.Select(c => c.Snapshot()).ToArray();
    public DiyCardLibrary(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("DIY 卡库文件过大");
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json);
            if (data is null || data.Schema != 1 || data.Cards is null || data.Cards.Length > 128)
                throw new InvalidDataException("DIY 卡库版本或内容无效");
            foreach (var card in data.Cards) CheckShape(card);
            if (data.Cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != data.Cards.Length
                || data.Cards.Select(c=>c.RuntimeId).Distinct(StringComparer.Ordinal).Count()!=data.Cards.Length)
                throw new InvalidDataException("DIY 卡牌编号重复");
            _cards = data.Cards.Select(c => c.Snapshot()).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { LoadError = $"DIY 卡库读取失败，原文件保留：{e.Message}"; }
    }
    public static void CheckShape(DiyCardDraft? card)
    {
        if (card is null || card.Id is null || !Regex.IsMatch(card.Id, @"\Adiy/[a-zA-Z0-9_-]{1,64}\z")
            || string.IsNullOrWhiteSpace(card.Name) || card.Name.Length > 40
            || card.Nation is null || card.Rarity is null || card.CardType is null || card.UnitType is null
            || card.PackId is null || (card.PackId.Length>0&&!DiyWorkshopLibrary.ValidPackId(card.PackId))
            || card.SourceCardId is null || card.SourceCardId.Length>200 || card.SourceCardId.Any(char.IsControl)
            || (card.SourceCardId.Length>0 && (card.SourceCardId.StartsWith("diy/",StringComparison.Ordinal)||card.SourceCardId.StartsWith("orc/",StringComparison.Ordinal)))
            || card.ArtSourceFile is null || (card.ArtSourceFile.Length>0&&!Regex.IsMatch(card.ArtSourceFile,@"\A[a-zA-Z0-9_-]{1,80}\.png\z"))
            || card.ArtImportMode is not ("illustration" or "card") || card.ArtRegion is null
            || !float.IsFinite(card.ArtRegion.X+card.ArtRegion.Y+card.ArtRegion.Width+card.ArtRegion.Height)
            || card.ArtRegion.X<0 || card.ArtRegion.Y<0 || card.ArtRegion.Width<=0 || card.ArtRegion.Height<=0
            || card.ArtRegion.X+card.ArtRegion.Width>1.001f || card.ArtRegion.Y+card.ArtRegion.Height>1.001f
            || card.ArtCardId is null || card.ArtCardId.Length > 200
            || card.ArtFile is null || (card.ArtFile.Length > 0 && !Regex.IsMatch(card.ArtFile, @"\A[a-zA-Z0-9_-]{1,64}\.png\z"))
            || card.ArtScale is < 50 or > 300 || card.Flavor is null || card.Flavor.Length > 300
            || card.EffectText is null || card.EffectText.Length > 2000
            || card.Keywords is null || card.Keywords.Length > 128 || card.Keywords.Any(string.IsNullOrWhiteSpace)
            || card.KeywordValues is null || card.KeywordValues.Count>128 || card.KeywordValues.Any(p=>!card.Keywords.Contains(p.Key)||p.Value is <0 or >10000)
            || card.Keywords.Distinct(StringComparer.Ordinal).Count() != card.Keywords.Length
            || card.Effects is null || card.Effects.Length > 8
            || card.Effects.Any(e => e is null || e.Op is null || e.Target is null || e.Until is null)
            || card.OrcEffects is null || card.OrcEffects.Length > 128
            || card.OrcEffects.Any(e => e is null || e.Template is null
                || e.Ops is null || e.Ops.Length > 128
                || e.Ops.Any(o => o is null || o.Op is null || o.CondUnitTypes is {Length:>16} || o.CondUnitTypes?.Any(t=>string.IsNullOrWhiteSpace(t))==true)))
            throw new InvalidDataException("DIY 卡牌字段无效，编号必须使用 diy/ 前缀");
        foreach(var effect in card.OrcEffects.Where(e=>e.DesktopJson is not null))
        {
            if(effect.DesktopJson!.Length>250_000)throw new InvalidDataException("单条效果声明过大");
            using var graph=JsonDocument.Parse(effect.DesktopJson);
            if(graph.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("效果声明必须是对象");
        }
        foreach(var effect in card.OrcEffects.Where(e=>e.PrefabJson is not null))
        {
            if(effect.PrefabJson!.Length>250_000)throw new InvalidDataException("原生预制体声明过大");
            using var raw=JsonDocument.Parse(effect.PrefabJson);
            if(raw.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("原生预制体必须是对象");
        }
        if(card.DesktopCardJson is { } cardJson)
        {
            if(cardJson.Length>250_000)throw new InvalidDataException("卡面声明过大");
            using var raw=JsonDocument.Parse(cardJson);
            if(raw.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("卡面声明必须是对象");
        }
    }
    public void Save(DiyCardDraft card)
    {
        CheckShape(card);
        if(_cards.Any(c=>c.Id!=card.Id && c.RuntimeId==card.RuntimeId))throw new InvalidDataException("同一原始卡牌不能存在多个本地修改");
        if (_cards.Count >= 128 && !_cards.Any(c => c.Id == card.Id)) throw new InvalidDataException("最多保存 128 种 DIY 卡牌");
        Mutate(() => { _cards.RemoveAll(c => c.Id == card.Id); _cards.Add(card.Snapshot() with { Name = card.Name.Trim() }); });
    }

    // ── 自导入卡图 ────────────────────────────────────────────────────────────
    // 图片存卡库旁 art/ 目录（与库文件同生命周期）；文件名由卡牌编号派生，
    // CheckShape 校验 ArtFile 受限字符——不存在路径逃逸面。
    private string ArtDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "art");
    public static string ArtFileName(string cardId) =>
        cardId.StartsWith("diy/", StringComparison.Ordinal) && Regex.IsMatch(cardId[4..], @"\A[a-zA-Z0-9_-]{1,64}\z")
            ? cardId[4..] + ".png" : throw new InvalidDataException("卡牌编号无效，无法派生卡图文件名");
    /// <summary>保存 PNG 字节为该卡的导入卡图（原子写；≤4MB），返回草稿应记录的文件名。</summary>
    public string SaveArt(string cardId, byte[] png)
    {
        if (png is null || png.Length is 0 or > 4_000_000) throw new InvalidDataException("卡图须为 1B–4MB 的 PNG 数据");
        var name = ArtFileName(cardId);
        var directory = ArtDirectory; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, name + ".tmp");
        File.WriteAllBytes(temporary, png);
        File.Move(temporary, Path.Combine(directory, name), overwrite: true);
        return name;
    }
    public string SaveArtSource(string cardId,byte[] png)
    {
        if(png.Length is 0 or >16_000_000)throw new InvalidDataException("原卡面图片最大 16 MB");
        var name=ArtFileName(cardId)[..^4]+"-source.png";Directory.CreateDirectory(ArtDirectory);
        var file=Path.Combine(ArtDirectory,name);File.WriteAllBytes(file+".tmp",png);File.Move(file+".tmp",file,true);return name;
    }
    public byte[] ReadArt(string name)
    {if(!Regex.IsMatch(name,@"\A[a-zA-Z0-9_-]{1,80}\.png\z"))throw new InvalidDataException("图片文件名无效");return File.ReadAllBytes(Path.Combine(ArtDirectory,name));}
    public void Delete(string id) => Mutate(() =>
    {
        _cards.RemoveAll(c => c.Id == id);
        try { File.Delete(Path.Combine(ArtDirectory, ArtFileName(id))); } catch (IOException) { }
    });
    private void Mutate(Action change)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        var previous = _cards.ToList();
        try
        {
            change();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + ".tmp";
            var encoded=JsonSerializer.Serialize(new FileData(1, _cards.ToArray()), Json);
            if(Encoding.UTF8.GetByteCount(encoded)>8_000_000)throw new InvalidDataException("DIY 卡库总大小超过8MB，已有卡库保持不变");
            File.WriteAllText(temporary, encoded, new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        catch { _cards = previous; throw; }
    }
    public static string Export(DiyCardDraft card)
    {
        CheckShape(card);
        return "ORCDIY1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(card, Json)));
    }
    public static DiyCardDraft Import(string text)
    {
        if (text is null || text.Length > 2_000_000 || !text.Trim().StartsWith("ORCDIY1:", StringComparison.Ordinal))
            throw new InvalidDataException("请输入 ORCDIY1: 开头的 DIY 卡牌代码");
        try
        {
            var card = JsonSerializer.Deserialize<DiyCardDraft>(Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()[8..])), Json);
            CheckShape(card);
            // Import is a new card, never an overwrite of another author's card.
            return card! with { Id = "diy/" + Guid.NewGuid().ToString("N"), SourceCardId="" };
        }
        catch (Exception e) when (e is FormatException or JsonException)
        { throw new InvalidDataException("DIY 卡牌代码无效", e); }
    }
}
