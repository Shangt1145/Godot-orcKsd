namespace Kards.Ui.Core;

/// <summary>
/// 覆盖写之前留一份"上一版"：<c>cards.json</c> → <c>cards.json.bak1</c> → <c>.bak2</c> → <c>.bak3</c>。
///
/// <para>
/// 为什么需要：卡库/卡组/工坊都是**覆写式**原子写（写 .tmp 再 Move 覆盖）。原子写保证的是
/// "不会写出半个文件"，但保证不了"写出一个逻辑上错的文件"——比如导入了一张坏卡、
/// 或者一次误删。以前这种情况只能靠 git 或者手工备份文件（本机就躺着一个
/// <c>diy-packs.json.before-drag-mechanics-….bak</c>，那是人肉救回来的痕迹）。
/// </para>
///
/// <para>
/// 为什么是环形 3 份而不是按时间戳：时间戳文件名会让目录无限增长，且"该保留哪一份"变成玩家要做的决定。
/// 固定三格是"我可能手滑两三次"的量级，且每格都有确定含义（bak1＝上一次保存前的样子）。
/// </para>
///
/// <para>
/// 失败策略：**备份写不出去就让这次保存失败**（异常抛给调用方，调用方已有内存回滚）。
/// 反过来"备份失败但照样覆盖"是最糟的组合——玩家以为有后路，实际没有。
/// </para>
/// </summary>
public static class BackupRing
{
    /// <summary>保留几份历史。改这个数会让已有备份的语义变化，别轻易动。</summary>
    public const int Keep = 3;

    public static string PathFor(string path, int slot) => $"{path}.bak{slot}";

    /// <summary>
    /// 把当前文件推进备份环：最老的一格被挤掉，其余依次后退，当前内容成为 bak1。
    /// 目标文件不存在时什么也不做（第一次保存没有"上一版"）。
    ///
    /// <para>
    /// 从最老的一格往下搬，只有"上一格真的有东西要搬进来"时才覆盖它。
    /// 早先的写法是先把 bak3 删掉再搬，遇到空档（中间某格被手工删过）就会
    /// 白丢一份还有用的历史——环里明明还有位置。搬的时候覆盖就等于挤掉最老那份，
    /// 满环时行为和原来一致。
    /// </para>
    /// </summary>
    public static void Rotate(string path, int keep = Keep)
    {
        if (keep < 1 || !File.Exists(path)) return;

        for (var slot = keep; slot >= 2; slot--)
        {
            var from = PathFor(path, slot - 1);
            if (!File.Exists(from)) continue;
            File.Move(from, PathFor(path, slot), overwrite: true);
        }
        File.Copy(path, PathFor(path, 1), overwrite: true);
    }

    /// <summary>最新的可用备份；一个都没有时返回 null。读坏了要能给玩家指一条路。</summary>
    public static string? Newest(string path)
    {
        for (var slot = 1; slot <= Keep; slot++)
            if (File.Exists(PathFor(path, slot))) return PathFor(path, slot);
        return null;
    }

    /// <summary>
    /// 读失败时拼在错误消息后面的一句"还有后路"。没有备份就返回空串（不要凭空许诺）。
    /// 各库的 <c>LoadError</c> 会原样显示在界面提示行上，所以这句话是玩家真正看得见的。
    /// </summary>
    public static string Hint(string path)
        => Newest(path) is { } backup ? $"（上一版备份在 {backup}，改名覆盖原文件即可回滚）" : "";

    /// <summary>
    /// 用最新备份盖回原文件。返回 false 表示根本没有备份可还原（调用方要如实说，别假装成功）。
    /// 还原同样走 .tmp + Move，避免中途失败留下半个文件。
    /// </summary>
    public static bool Restore(string path, out string? restoredFrom)
    {
        restoredFrom = Newest(path);
        if (restoredFrom is null) return false;
        var temporary = path + ".restore.tmp";
        File.Copy(restoredFrom, temporary, overwrite: true);
        File.Move(temporary, path, overwrite: true);
        return true;
    }
}
