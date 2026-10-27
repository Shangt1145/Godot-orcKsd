namespace Kards.Ui.Core;

/// <summary>
/// 存档 schema 的统一判定入口。
///
/// 原先四处库各自写 `Schema != 1` 硬失败，而且失败原因都被折叠成同一句话
/// （"版本或内容无效"）。玩家看到这句话无法判断到底是文件坏了、还是自己的游戏比存档旧，
/// 两种情况的处置完全不同：后者要升级游戏，前者才需要重做。
///
/// 这里把判定收成一处，并把"能不能读"和"读不了该怎么办"一起返回。
/// 判定区间是 [<see cref="Oldest"/>, <see cref="Latest"/>]：
/// 低于区间＝太旧（需要迁移代码），高于区间＝太新（需要升级游戏）。
///
/// 下一次升版本时改 <see cref="Latest"/>；真需要迁移步骤时在 <see cref="Migrate{T}"/>
/// 里加一条，调用方（四个库）不用跟着动。本轮没有 v2，所以 <see cref="Migrate{T}"/>
/// 里只有 v1 恒等这一步——不假装已经有迁移链。
/// </summary>
public static class SchemaVersioning
{
    /// <summary>当前写入的版本。</summary>
    public const int Latest = 1;

    /// <summary>还能直接读的最老版本。比它更老的必须先写迁移步骤。</summary>
    public const int Oldest = 1;

    public static bool Acceptable(int stored) => stored is >= Oldest and <= Latest;

    /// <summary>
    /// 判定读入的版本，不可接受时抛异常。异常消息会原样显示给玩家（各库都把它塞进 LoadError），
    /// 所以这里必须写"怎么办"，而不是只写"无效"。
    /// </summary>
    public static void Require(int stored, string label)
    {
        if (Acceptable(stored)) return;
        throw new InvalidDataException(stored > Latest
            ? $"{label}来自更新版本的游戏（存档 v{stored}，本版最高 v{Latest}）。升级游戏后再打开；原文件保留不动。"
            : $"{label}的格式太旧（v{stored}），本版只支持 v{Oldest}–v{Latest}；原文件保留不动。");
    }

    /// <summary>
    /// 迁移入口：把 <paramref name="stored"/> 版本读出来的数据变成当前版本。
    /// 目前只有 v1，恒等返回。新增版本时在这里加 `case 1 => …` 并把 <see cref="Oldest"/> 相应下放。
    /// </summary>
    public static T Migrate<T>(T data, int stored, string label)
    {
        Require(stored, label);
        return data;
    }
}
