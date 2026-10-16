using Godot;

namespace Kards.Ui;

/// <summary>
/// 自导入卡图解析器：把 diyart://&lt;file&gt; 内部 scheme 解析为纹理。
/// 文件只可能位于 DiyCardLibrary 卡库旁的 art/ 目录（文件名经 CheckShape 受限校验，
/// 无路径逃逸面）；基目录由 Main 启动时注入。加载走 Image.LoadFromFile（运行时图像，
/// 不依赖 Godot 资源导入管线），失败＝静默回退文字卡面。
/// </summary>
public static class DiyArtStore
{
    private static string? _baseDir;
    // 绘制缩放按卡牌编号注册（UiCardDefinition 是引擎契约不可加字段；缩放是纯表现元数据，
    // 随卡库装载/保存刷新——Main 持有真源，编辑器即时更新供预览）。
    private static readonly Dictionary<string, int> _zooms = new();
    /// <summary>注入卡库旁 art/ 目录的绝对路径与各卡缩放表（启动时一次）。</summary>
    public static void Configure(string directory, IEnumerable<(string Id, int Zoom)> zooms)
    {
        _baseDir = directory;
        _zooms.Clear();
        foreach (var (id, zoom) in zooms)
            if (zoom is >= 50 and <= 300) _zooms[id] = zoom;
    }
    /// <summary>编辑器导入/调缩放后即时刷新单卡缩放（预览与对局渲染同源）。</summary>
    public static void UpdateZoom(string cardId, int zoom)
    {
        if (zoom is >= 50 and <= 300) _zooms[cardId] = zoom;
        else _zooms.Remove(cardId);
    }
    public static int ZoomOf(string cardId) => _zooms.GetValueOrDefault(cardId, 100);    public static bool Has(string file) => _baseDir is not null
        && file.Length > 0 && File.Exists(Path.Combine(_baseDir, file));
    public static Texture2D? Load(string file)
    {
        if (_baseDir is null || file.Length == 0 || file.Contains('/') || file.Contains('\\') || file.Contains(".."))
            return null;
        try
        {
            var image = Image.LoadFromFile(Path.Combine(_baseDir, file));
            return image is null ? null : ImageTexture.CreateFromImage(image);
        }
        catch { return null; }
    }
}
