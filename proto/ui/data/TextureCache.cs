using Godot;

namespace Kards.Ui;

/// <summary>Bounded resource cache; catalogue pages release their controls on page change.</summary>
public sealed class TextureCache
{
    private readonly Dictionary<string, (Texture2D Texture, LinkedListNode<string> Node)> _items = new();
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _missing = new();
    public int Count => _items.Count;
    public int Capacity { get; } = 64;
    /// <summary>Imported files can change without changing their managed filename.</summary>
    public void Invalidate(string path)
    {
        _missing.Remove(path);
        if (_items.Remove(path, out var cached)) _order.Remove(cached.Node);
    }
    /// <summary>缓存写入（null＝记为缺失并返回 null），res:// 与 diyart:// 两路共用。</summary>
    private Texture2D? Store(string path, Texture2D? tex)
    {
        if (tex is null)
        {
            _missing.Add(path);
            return null;
        }
        var n = _order.AddFirst(path);
        _items[path] = (tex, n);
        if (_items.Count > Capacity && _order.Last is { } last)
        {
            _items.Remove(last.Value);
            _order.RemoveLast();
        }
        return tex;
    }
    public Texture2D? Get(string path)
    {
        if (path.Length == 0 || _missing.Contains(path))
            return null;
        if (_items.TryGetValue(path, out var found))
        {
            _order.Remove(found.Node);
            _order.AddFirst(found.Node);
            return found.Texture;
        }
        // diyart:// 自导入卡图：运行时图像（不经 Godot 资源导入管线），由 DiyArtStore 解析。
        if (path.StartsWith("diyart://", StringComparison.Ordinal))
            return Store(path, DiyArtStore.Load(path[9..]));
        if (!ResourceLoader.Exists(path))
        {
            _missing.Add(path);
            GD.PushWarning($"Missing art: {path}");
            return null;
        }
        var tex = ResourceLoader.Load<Texture2D>(path);
        if (tex is null)
            return null;
        var n = _order.AddFirst(path);
        _items[path] = (tex, n);
        if (_items.Count > Capacity && _order.Last is { } last)
        {
            _items.Remove(last.Value);
            _order.RemoveLast();
        }
        return tex;
    }
}
