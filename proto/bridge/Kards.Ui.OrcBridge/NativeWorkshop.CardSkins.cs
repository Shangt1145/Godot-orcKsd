using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui.OrcBridge;

public sealed partial class NativeWorkshop
{
    public CardSkin? PreviewSkin(string localId) => ReadSkin(TemporaryRoot!, localId);
    public CardSkin? SkinFor(string appId)
    {
        var package = OrcEngineHost.Snapshot().CardPacks.FirstOrDefault(p => appId.StartsWith("pack/" + p.Manifest.Id + "/", StringComparison.Ordinal));
        if (package is null) return null;
        var skin = ReadSkin(package.RootDirectory, appId[(package.Manifest.Id.Length + 6)..]);
        return skin is null ? null : ResolveSkin(package.RootDirectory, skin);
    }
    public CardSkin? ResolvePreviewSkin(string localId)
    {
        var skin = PreviewSkin(localId);
        return skin is null ? null : ResolveSkin(TemporaryRoot!, skin);
    }
    private static CardSkin? ResolveSkin(string root, CardSkin skin)
    {
        var file = WorkshopPackageArchive.Child(root, skin.Art);
        return File.Exists(file) ? skin with { Art = "packfull://" + file } : null;
    }
    private static CardSkin? ReadSkin(string root, string localId)
    {
        try
        {
            var path = Path.Combine(root, "workshop.presentation.json");
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("cards", out var cards) || !cards.TryGetProperty(localId, out var card)
                || !card.TryGetProperty("alternateArt", out var raw) || raw.ValueKind == JsonValueKind.Null) return null;
            return raw.Deserialize<CardSkin>()?.Checked();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException) { return null; }
    }
    public void SaveSkin(string localId, CardSkin? skin, byte[]? completeCard = null)
    {
        _ = Editor.GetAsset(Orc.Core.AssetKind.Card, localId);
        if (skin is not null)
        {
            if (completeCard is not null)
            {
                if (completeCard.Length == 0) throw new InvalidDataException("完整卡面文件为空。");
                // 原图字节原样入包，扩展名沿用导入时的格式；不做 PNG 归一或体积校验。
                var relative = "art/alt-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(completeCard)) + CardSkinFile.ExtensionOf(skin.Art);
                skin = skin with { Art = relative };
            }
            skin.Checked();
            var file = WorkshopPackageArchive.Child(TemporaryRoot!, skin.Art);
            if (completeCard is not null) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, completeCard); }
            if (!File.Exists(file)) throw new InvalidDataException("完整卡面文件缺失，请重新导入。");
        }
        var metadata = Presentation();
        var cards = metadata["cards"] as JsonObject ?? new(); metadata["cards"] = cards;
        var card = cards[localId] as JsonObject ?? new(); cards[localId] = card;
        card["alternateArt"] = skin is null ? null : JsonSerializer.SerializeToNode(skin);
        SavePresentation(metadata);
    }
}
