using System.Text;
using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

public sealed record DeckOptions
{
    public string MainNation { get; init; } = "av76";
    public string AllyNation { get; init; } = "deran";
    public bool UnrestrictedNations { get; init; }
    public int TotalCards { get; init; } = 40; // Includes the HQ; never insert an HQ into CardIds.
    public bool UnrestrictedSize { get; init; }
}

public sealed record ConstructedDeck
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新卡组";
    public DeckOptions Options { get; init; } = new();
    public string[] CardIds { get; init; } = [];
    /// <summary>null is a legacy deck: infer only the packs its cards actually use.</summary>
    public string[]? PackIds { get; init; }
    public ConstructedDeck Snapshot() => this with { CardIds = CardIds.ToArray(), PackIds = PackIds?.ToArray() };
}

public static class DeckCardPacks
{
    private static IReadOnlyDictionary<string, string> _families = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string,string> _authoredOrigins=new Dictionary<string,string>(),_authoredFamilies=new Dictionary<string,string>();
    public static void ConfigureAuthored(IReadOnlyDictionary<string,string> origins,IReadOnlyDictionary<string,string> families)
    {Volatile.Write(ref _authoredOrigins,new Dictionary<string,string>(origins));Volatile.Write(ref _authoredFamilies,new Dictionary<string,string>(families));}
    /// <summary>Package relationships come from the engine's frozen cardpack manifests.</summary>
    public static void ConfigureFamilies(IReadOnlyDictionary<string, string> families) =>
        Volatile.Write(ref _families, new Dictionary<string, string>(families, StringComparer.Ordinal));
    public static string Family(string packId)
    {
        // Old saved decks and exported deck codes selected the single DIY pool.
        // Keep that selection bound to the migrated legacy pack, without admitting new packs.
        if(packId=="diy"&&Volatile.Read(ref _authoredOrigins).Values.Contains("pack/diy-legacy"))return "pack/diy-legacy";
        return Volatile.Read(ref _authoredFamilies).TryGetValue(packId,out var parent)?Family(parent):Volatile.Read(ref _families).GetValueOrDefault(packId, packId);
    }
    public static string RawOf(string cardId) => Volatile.Read(ref _authoredOrigins).TryGetValue(cardId,out var authored)?authored:cardId.StartsWith("pack/", StringComparison.Ordinal)
        ? string.Join('/', cardId.Split('/').Take(2))
        : cardId.StartsWith("diy/", StringComparison.Ordinal) ? "diy"
        : cardId.StartsWith("orc/", StringComparison.Ordinal) ? "orc" : "source";
    public static string Of(string cardId) => Family(RawOf(cardId));
    public static string[] Selected(ConstructedDeck deck) => (deck.PackIds
        ?? (deck.CardIds.Length == 0 ? ["source"] : deck.CardIds.Select(RawOf).ToArray()))
        .Select(Family).Distinct(StringComparer.Ordinal).ToArray();
    public static bool Allows(ConstructedDeck deck, string cardId) => Selected(deck).Contains(Of(cardId), StringComparer.Ordinal);
    public static bool ValidSelection(string[]? packs) => packs is null || (packs.Length is > 0 and <= 256
        && packs.All(p => p is "source" or "diy" or "orc" || (p is not null && p.StartsWith("pack/", StringComparison.Ordinal)
            && p.Length > 5 && p[5..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
        && packs.Distinct(StringComparer.Ordinal).Count() == packs.Length);
}

public static class ConstructionRules
{
    private static readonly string[] BuiltInNations = [ "av76", "deran", "USG", "UN",
        "germany", "soviet", "usa", "britain", "japan", "france", "italy", "poland", "finland", "anzac" ];
    private static string[] _customNations=[];
    public static IReadOnlyList<string> MainNations=>BuiltInNations.Concat(Volatile.Read(ref _customNations)).Distinct().ToArray();
    public static void ConfigureCustomNations(IEnumerable<string> ids)=>Volatile.Write(ref _customNations,ids.Distinct().ToArray());
    // The source project's card-import.js explicitly maps these metal names to KARDS rarities.
    public static int CopyLimit(string rarity) => rarity.ToLowerInvariant() switch
    {
        "iron" or "common" or "standard" => 4,
        "bronze" or "limited" => 3,
        "silver" or "special" => 2,
        "gold" or "elite" => 1,
        _ => 0
    };
    public static IReadOnlyList<string> Validate(ConstructedDeck deck, IReadOnlyList<UiCardDefinition> catalog,
        IReadOnlySet<string>? playable = null)
    {
        var errors = new List<string>();
        if (!DeckCardPacks.ValidSelection(deck.PackIds)) errors.Add("请至少选择一个有效卡包，且不能重复");
        if (string.IsNullOrWhiteSpace(deck.Name) || deck.Name.Trim().Length > 64) errors.Add("卡组名称须为 1–64 个字符");
        var o = deck.Options;
        if (o.TotalCards is < 6 or > 512) errors.Add("自定义总张数须为 6–512（含总部）");
        if (deck.CardIds.Length < 5) errors.Add("卡组至少需要 5 张卡牌，另加 1 张总部");
        if (deck.CardIds.Length > 511) errors.Add("卡组超出当前支持的容量");
        if (!o.UnrestrictedSize && deck.CardIds.Length != o.TotalCards - 1)
            errors.Add($"已选 {deck.CardIds.Length} / {o.TotalCards - 1} 张卡牌，另加 1 张总部");
        if (!MainNations.Contains(o.MainNation)) errors.Add("请选择有效的主国");
        if (!MainNations.Contains(o.AllyNation)) errors.Add("请选择有效的盟国；可以不加入盟国卡牌");
        // 国家同一性以规范国籍为准：主国 av76（苏联旧卡池）＋ 盟国 soviet 在 UI 里是两个字符串，
        // 到引擎却是同一个国家（对局创建期 fail-fast 拒绝）。这里提前拦住并说清原因。
        var mainNation = DeckNations.Canonical(o.MainNation);
        var allyNation = DeckNations.Canonical(o.AllyNation);
        if (DeckNations.Same(o.MainNation, o.AllyNation))
            errors.Add(string.Equals(o.MainNation, o.AllyNation, StringComparison.Ordinal)
                ? "主国和盟国不能相同"
                : $"主国和盟国不能是同一个国家：「{o.MainNation}」与「{o.AllyNation}」是同一国的两种写法");
        var definitions = catalog.ToDictionary(c => c.CardId, StringComparer.Ordinal);
        foreach (var group in deck.CardIds.GroupBy(id => id, StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(group.Key, out var card)) { errors.Add($"卡牌已不存在：{group.Key}"); continue; }
            if (!DeckCardPacks.Allows(deck, card.CardId)) errors.Add($"{card.Name} 不属于此卡组选择的卡包");
            if (card.IsToken || CopyLimit(card.Rarity) == 0) errors.Add($"{card.Name} 无法加入普通卡组");
            else if (group.Count() > CopyLimit(card.Rarity)) errors.Add($"{card.Name} 最多 {CopyLimit(card.Rarity)} 张");
            if (playable is not null && !playable.Contains(card.CardId)) errors.Add($"{card.Name} 尚未支持实战");
            if (!o.UnrestrictedNations)
            {
                // 卡牌归属同样按规范国籍判定：主国选“苏联”时，旧卡池 av76 的苏联卡也算主国卡。
                var cardNation = DeckNations.Canonical(card.Set);
                if (cardNation != mainNation && cardNation != allyNation) errors.Add($"{card.Name} 不属于主国或盟国");
                if (allyNation.Length > 0 && cardNation == allyNation && CopyLimit(card.Rarity) == 1) errors.Add($"盟国不能使用精英卡：{card.Name}");
            }
        }
        if (!o.UnrestrictedNations && allyNation.Length > 0 && allyNation != mainNation
            && deck.CardIds.Count(id => definitions.TryGetValue(id, out var c) && DeckNations.Canonical(c.Set) == allyNation) > 12)
            errors.Add("盟国卡牌最多 12 张");
        return errors.AsReadOnly();
    }
    public static string? CannotAdd(ConstructedDeck deck, UiCardDefinition card)
    {
        if (!DeckCardPacks.Allows(deck, card.CardId)) return "这张卡不属于所选卡包";
        if (card.IsToken || CopyLimit(card.Rarity) == 0) return "衍生卡或未知稀有度不能构筑";
        if (deck.CardIds.Count(id => id == card.CardId) >= CopyLimit(card.Rarity)) return $"同名卡已达到 {CopyLimit(card.Rarity)} 张上限";
        if (deck.CardIds.Length >= (deck.Options.UnrestrictedSize ? 511 : deck.Options.TotalCards - 1)) return "卡组张数已满";
        if (!deck.Options.UnrestrictedNations)
        {
            var mainNation = DeckNations.Canonical(deck.Options.MainNation);
            var allyNation = DeckNations.Canonical(deck.Options.AllyNation);
            var cardNation = DeckNations.Canonical(card.Set);
            if (cardNation != mainNation && cardNation != allyNation) return "仅可加入主国和盟国卡牌";
            if (allyNation.Length > 0 && cardNation == allyNation && CopyLimit(card.Rarity) == 1) return "盟国不能使用精英卡";
        }
        return null;
    }
}

/// <summary>Only saved snapshots are persisted; editing or importing never changes the active deck.</summary>
public sealed class DeckLibrary
{
    private sealed record FileData(int Schema, string? ActiveId, ConstructedDeck[] Decks);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private List<ConstructedDeck> _decks = [];
    public string? ActiveId { get; private set; }
    public string? LoadError { get; private set; }
    public IReadOnlyList<ConstructedDeck> Decks => _decks.Select(d => d.Snapshot()).ToArray();
    public ConstructedDeck? ActiveDeck => _decks.FirstOrDefault(d => d.Id == ActiveId)?.Snapshot();
    public DeckLibrary(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json);
            if (data is null || data.Schema != 1 || data.Decks is null || data.Decks.Length > 500
                || data.Decks.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id) || d.Name is null || d.Options is null
                    || d.Options.MainNation is null || d.Options.AllyNation is null
                    || d.CardIds is null || d.CardIds.Length > 511 || d.CardIds.Any(string.IsNullOrWhiteSpace))
                || data.Decks.Any(d => !DeckCardPacks.ValidSelection(d.PackIds))
                || data.Decks.Select(d => d.Id).Distinct().Count() != data.Decks.Length)
                throw new InvalidDataException("卡组文件格式不受支持");
            _decks = data.Decks.Select(d => d.Snapshot()).ToList();
            ActiveId = _decks.Any(d => d.Id == data.ActiveId) ? data.ActiveId : null;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { LoadError = $"卡组文件读取失败，原文件保留：{e.Message}"; }
    }
    public void Save(ConstructedDeck deck)
    {
        if (string.IsNullOrWhiteSpace(deck.Name) || deck.Name.Trim().Length > 64 || deck.Options is null
            || deck.Options.MainNation is null || deck.Options.AllyNation is null
            || string.IsNullOrWhiteSpace(deck.Id) || deck.CardIds.Length > 511 || deck.CardIds.Any(string.IsNullOrWhiteSpace)
            || !DeckCardPacks.ValidSelection(deck.PackIds))
            throw new InvalidDataException("卡组名称或卡牌列表无效");
        if (_decks.Count >= 500 && !_decks.Any(d => d.Id == deck.Id)) throw new InvalidDataException("已达到 500 副卡组的保存上限");
        Mutate(() => { _decks.RemoveAll(d => d.Id == deck.Id); _decks.Add(deck.Snapshot() with { Name = deck.Name.Trim() }); });
    }
    public void Activate(string id) => Mutate(() => { if (!_decks.Any(d => d.Id == id)) throw new InvalidDataException("卡组不存在"); ActiveId = id; });
    public void Delete(string id) => Mutate(() => { _decks.RemoveAll(d => d.Id == id); if (ActiveId == id) ActiveId = null; });
    private void Mutate(Action action)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        var previous = _decks.ToList(); var active = ActiveId;
        try
        {
            action();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new FileData(1, ActiveId, _decks.ToArray()), Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch { _decks = previous; ActiveId = active; throw; }
    }
    public static string Export(ConstructedDeck deck) => "ORK1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deck)));
    public static ConstructedDeck Import(string text)
    {
        if (!text.Trim().StartsWith("ORK1:", StringComparison.Ordinal) || text.Length > 200_000) throw new InvalidDataException("请输入 OrC-KSD 卡组代码（ORK1:）");
        try
        {
            var d = JsonSerializer.Deserialize<ConstructedDeck>(Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim()[5..])))
                ?? throw new InvalidDataException("卡组代码为空");
            if (d.Name is null || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 64 || d.Options is null
                || d.Options.MainNation is null || d.Options.AllyNation is null
                || d.CardIds is null || d.CardIds.Length > 511 || d.CardIds.Any(string.IsNullOrWhiteSpace)
                || !DeckCardPacks.ValidSelection(d.PackIds)) throw new InvalidDataException("卡组代码内容无效");
            return d with { Id = Guid.NewGuid().ToString("N") };
        }
        catch (Exception e) when (e is FormatException or JsonException) { throw new InvalidDataException("卡组代码无效", e); }
    }
}
