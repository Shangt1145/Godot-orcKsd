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
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("DIY 卡库文件过大");
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json);
            if (data is null || data.Schema != 1 || data.Cards is null || data.Cards.Length > 128)
                throw new InvalidDataException("DIY 卡库版本或内容无效");
            foreach (var card in data.Cards) CheckShape(card);
            if (data.Cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != data.Cards.Length)
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
            || card.ArtCardId is null || card.ArtCardId.Length > 200 || card.Flavor is null || card.Flavor.Length > 300
            || card.Keywords is null || card.Keywords.Length > 16 || card.Keywords.Any(string.IsNullOrWhiteSpace)
            || card.Keywords.Distinct(StringComparer.Ordinal).Count() != card.Keywords.Length
            || card.Effects is null || card.Effects.Length > 8
            || card.Effects.Any(e => e is null || e.Op is null || e.Target is null || e.Until is null))
            throw new InvalidDataException("DIY 卡牌字段无效，编号必须使用 diy/ 前缀");
    }
    public void Save(DiyCardDraft card)
    {
        CheckShape(card);
        if (_cards.Count >= 128 && !_cards.Any(c => c.Id == card.Id)) throw new InvalidDataException("最多保存 128 种 DIY 卡牌");
        Mutate(() => { _cards.RemoveAll(c => c.Id == card.Id); _cards.Add(card.Snapshot() with { Name = card.Name.Trim() }); });
    }
    public void Delete(string id) => Mutate(() => _cards.RemoveAll(c => c.Id == id));
    private void Mutate(Action change)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        var previous = _cards.ToList();
        try
        {
            change();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new FileData(1, _cards.ToArray()), Json), new UTF8Encoding(false));
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
        if (text is null || text.Length > 50_000 || !text.Trim().StartsWith("ORCDIY1:", StringComparison.Ordinal))
            throw new InvalidDataException("请输入 ORCDIY1: 开头的 DIY 卡牌代码");
        try
        {
            var card = JsonSerializer.Deserialize<DiyCardDraft>(Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()[8..])), Json);
            CheckShape(card);
            // Import is a new card, never an overwrite of another author's card.
            return card! with { Id = "diy/" + Guid.NewGuid().ToString("N") };
        }
        catch (Exception e) when (e is FormatException or JsonException)
        { throw new InvalidDataException("DIY 卡牌代码无效", e); }
    }
}
