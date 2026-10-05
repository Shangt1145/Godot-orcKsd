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
