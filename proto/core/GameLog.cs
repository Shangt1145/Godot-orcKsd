namespace Kards.Ui.Core;

public enum GameLogLevel { Debug, Info, Warn, Error }

public sealed record GameLogEntry(DateTime At, GameLogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{At:HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant()[..1]}] {Category} · {Message}";
}

/// <summary>
/// 进程级环形日志。对局里的效果派发、目标选择、协议回调和编译错误都要留痕——
/// 这些路径有很大一部分跑在协议线程上，所以写入必须线程安全，
/// 而且**任何一条日志都不能把业务带崩**（回调异常就地吞掉）。
/// </summary>
public static class GameLog
{
    public const int Capacity = 1000;

    private static readonly object Gate = new();
    private static readonly Queue<GameLogEntry> Ring = new(Capacity + 1);
    private static long _dropped;

    /// <summary>新日志回调。**在写入线程上触发**，订阅方要自己 marshal 回主线程。</summary>
    public static event Action<GameLogEntry>? Written;

    public static long Dropped { get { lock (Gate) return _dropped; } }

    public static void Debug(string category, string message) => Write(GameLogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(GameLogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(GameLogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(GameLogLevel.Error, category, message);

    public static void Error(string category, string message, Exception error)
        => Write(GameLogLevel.Error, category, message + "：" + error.GetType().Name + " " + error.Message);

    public static void Exception(string category, Exception error, string? context = null)
        => Write(GameLogLevel.Error, category,
            (context is null ? "" : context + "：" + error.GetType().Name + " " + error.Message + "\n")
            + error.StackTrace);

    public static void Write(GameLogLevel level, string category, string message)
    {
        var entry = new GameLogEntry(DateTime.Now, level, category, message);
        Action<GameLogEntry>? handler;
        lock (Gate)
        {
            Ring.Enqueue(entry);
            while (Ring.Count > Capacity) { Ring.Dequeue(); _dropped++; }
            handler = Written;
        }

        // 在锁外触发：订阅方若回读 Snapshot 不会自锁；抛异常也不该连坐业务。
        try { handler?.Invoke(entry); }
        catch (Exception) { /* 日志永不反噬调用方 */ }
    }

    public static GameLogEntry[] Snapshot()
    {
        lock (Gate) return Ring.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) { Ring.Clear(); _dropped = 0; }
    }
}
