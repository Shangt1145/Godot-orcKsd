using System.Text.Json;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>整包导出结果：<see cref="Archive"/> 为 null ＝检查没通过，原因在 <see cref="Problems"/>。</summary>
public sealed record DiyPackExportResult(string? Archive, IReadOnlyList<string> Problems, int Cards);

/// <summary>
/// 把一个 DIY 卡包导出成**原生卡包格式的 ZIP**，对方用「导入卡包 ZIP…」装完即可直接用。
///
/// <para>
/// 为什么导出成原生格式而不是自家 JSON：对方装的是卡包，不是我的草稿库。原生格式里
/// 卡已经是引擎编译好的定义（<c>cards/*.card.json</c>），词条与国家进
/// <c>network-vocabulary.json</c> / <c>workshop.presentation.json</c>，卡图进 <c>art/</c>，
/// 展示文本进 <c>display-texts.json</c>，选靶声明进 <c>desktop-effects.json</c>——这套目录
/// 就是联机工坊交给服务端的那一套，所以打包直接复用 <see cref="NetworkWorkshopSession.Prepare"/>，
/// 不另造第二份编译器（两份编译器一定会漂）。
/// </para>
///
/// <para>
/// 分享前对账：坏卡、丢图、编译不了的词条在导出前一次性列出来并**拦下**。以前这类问题
/// 会随 ZIP 原样传给下载方，而对方连修的机会都没有。
/// </para>
/// </summary>
public static class DiyPackExport
{
    /// <summary>导出前检查（只读，不写磁盘）。返回空＝可以导出。</summary>
    public static IReadOnlyList<string> Check(DiyPackDraft pack, IReadOnlyList<DiyCardDraft> cards,
        IReadOnlyList<UiCardDefinition> catalog, string? artDirectory)
    {
        var problems = new List<string>();
        if (cards.Count == 0) problems.Add("这个卡包里还没有卡牌，导出的包在对方那里是空的。");
        foreach (var card in cards)
        {
            // 对原生卡的本地修改不能随 DIY 包走：对方装包后要去改他自己那份原始卡，
            // 而包里根本没有那张原始卡的元数据——打包必然失败，不如现在说清楚。
            if (card.SourceCardId.Length > 0)
            {
                problems.Add($"「{card.Name}」：这是对原生卡牌的本地修改，不能放进 DIY 卡包导出。");
                continue;
            }
            var validation = DiyCardCompiler.Validate(card, catalog);
            if (!validation.Success) problems.Add($"「{card.Name}」：{string.Join("；", validation.Errors)}");
            if (card.AlternateArt is { } skin && (artDirectory is null || !File.Exists(Path.Combine(artDirectory, skin.Art))))
                problems.Add($"「{card.Name}」：完整异画文件缺失。");
            if (card.ArtFile.Length > 0 && (artDirectory is null || !File.Exists(Path.Combine(artDirectory, card.ArtFile))))
                problems.Add($"「{card.Name}」：自建卡图 {card.ArtFile} 找不到，对方看到的会是空白卡面。");
        }
        foreach (var keyword in pack.Keywords)
            if (WorkshopAuthoring.ValidateKeyword(keyword) is { } error) problems.Add($"词条「{keyword.Name}」：{error}");
        return problems;
    }

    /// <summary>
    /// 导出到 <paramref name="destination"/>（.zip）。检查不过＝不写文件，返回原因。
    /// 扩展包缺基础包只是**提示**（包本身完整，对方先装基础包即可），不阻断导出。
    /// </summary>
    public static DiyPackExportResult Export(DiyPackDraft pack, IReadOnlyList<DiyCardDraft> cards,
        IReadOnlyList<UiCardDefinition> catalog, string? artDirectory, string destination)
    {
        var blocking = Check(pack, cards, catalog, artDirectory);
        if (blocking.Count > 0) return new(null, blocking, cards.Count);
        // 提示，不是拦截：扩展包的卡要在对方那里进卡池，得先有基础包的词汇表。
        IReadOnlyList<string> notes = pack.IsExpansion && pack.BasePackId is { Length: > 0 } basePack
            ? [$"这是扩展包：对方需要先装基础包「{basePack}」，扩展包里的卡才能进卡池。"]
            : [];

        var sessionRoot = Path.Combine(Path.GetTempPath(), "OrC-KSD.UI", "pack-export");
        Directory.CreateDirectory(sessionRoot);
        string? directory = null;
        try
        {
            var session = NetworkWorkshopSession.Prepare(sessionRoot, [], cards, catalog,
                cards.Select(c => c.RuntimeId).ToArray(), artDirectory);
            directory = session.Directory;
            var local = pack.Id.StartsWith("pack/", StringComparison.Ordinal) ? pack.Id[5..] : pack.Id;
            var root = session.Roots.FirstOrDefault(candidate => ManifestId(candidate) == local)
                ?? throw new InvalidDataException("卡牌还挂在别的卡包上，这个包里没有内容可导出。");
            WorkshopPackageArchive.Export(root, destination);
            return new(destination, notes, cards.Count);
        }
        catch (InvalidDataException error)
        {
            // 内容层面打不出来（编译不出卡定义、卡挂错包…）：给原因，且**不留半个 ZIP**——
            // 半包比没有更糟，对方会以为装好了。
            Discard(destination);
            return new(null, [.. blocking, "打包失败：" + error.Message], cards.Count);
        }
        finally
        {
            // 临时目录只服务这一次导出；删不掉（被杀进程占着）也不影响——Prepare 会按 24 小时清理。
            if (directory is not null) DiscardDirectory(directory);
        }
    }

    private static void Discard(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DiscardDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? ManifestId(string root)
    {
        var path = Path.Combine(root, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
