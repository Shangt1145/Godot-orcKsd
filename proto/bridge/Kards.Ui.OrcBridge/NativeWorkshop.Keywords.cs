using System.Text.Json;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed partial class NativeWorkshop
{
    /// <summary>
    /// 包里自带的**词条显示名**（引擎 id → 玩家写的名字），读 <c>workshop.presentation.json</c> 的
    /// <c>keywords</c> 段。
    ///
    /// <para>
    /// 规则本体在 <c>network-vocabulary.json</c>（只有 <c>kw-…&lt;hash&gt;</c> 这种引擎 id）。
    /// 没有这一段，收包人卡面上看到的就是那串 id。这里和 <see cref="ReadNations"/> 同构：
    /// 展示层坏了只丢一条装饰，绝不抛——否则一张写坏的展示文件会让整包卡在卡面上消失。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<UiDiyChoice> ReadKeywords(string root)
    {
        var file = Path.Combine(root, "workshop.presentation.json");
        if (!File.Exists(file)) return [];
        try
        {
            return (JsonNode.Parse(File.ReadAllText(file))?["keywords"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(k => new UiDiyChoice(k["id"]?.ToString() ?? "", k["name"]?.ToString() ?? ""))
                .Where(k => k.Id.Length > 0 && k.Label.Length > 0)
                .ToArray();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }
}
