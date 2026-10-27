using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// P1 数据安全：覆盖写之前留上一版（环形 3 份）。
///
/// 判据落在"盘上到底有几个文件、里面是什么"，不看有没有调用某个方法——
/// 备份这种机制最怕的就是"调了但没生效"，只有读文件才看得见。
/// </summary>
public class BackupRingTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orcksd-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 目标文件不存在时什么也不做()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            BackupRing.Rotate(path);
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Null(BackupRing.Newest(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 第一次保存把原内容放进bak1()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            File.WriteAllText(path, "v1");
            BackupRing.Rotate(path);
            Assert.Equal("v1", File.ReadAllText(BackupRing.PathFor(path, 1)));
            Assert.False(File.Exists(BackupRing.PathFor(path, 2)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 连续保存只留三份_越旧越靠后()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            // 模拟四次"覆盖前备份"：每次原文件内容不同。
            foreach (var content in new[] { "v1", "v2", "v3", "v4" })
            {
                File.WriteAllText(path, content);
                BackupRing.Rotate(path);
            }
            Assert.Equal("v4", File.ReadAllText(BackupRing.PathFor(path, 1)));
            Assert.Equal("v3", File.ReadAllText(BackupRing.PathFor(path, 2)));
            Assert.Equal("v2", File.ReadAllText(BackupRing.PathFor(path, 3)));
            // 第四格必须不存在：环只有三格，"v1" 被挤掉了。
            Assert.False(File.Exists(BackupRing.PathFor(path, 4)));
            Assert.Equal(4, Directory.GetFiles(dir).Length); // 原文件 + 三个备份
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 中间缺一格也不炸_顺序照样对()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            File.WriteAllText(path, "now");
            File.WriteAllText(BackupRing.PathFor(path, 1), "older");
            // 故意跳过 bak2，直接造一个 bak3。
            File.WriteAllText(BackupRing.PathFor(path, 3), "oldest");

            BackupRing.Rotate(path);

            Assert.Equal("now", File.ReadAllText(BackupRing.PathFor(path, 1)));
            Assert.Equal("older", File.ReadAllText(BackupRing.PathFor(path, 2)));
            // bak3 必须原样留着：环里还有空位，没理由把这份历史挤掉。
            Assert.Equal("oldest", File.ReadAllText(BackupRing.PathFor(path, 3)));
            Assert.Equal(4, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 最新备份按格子找_缺了就往后退()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            Assert.Null(BackupRing.Newest(path));
            File.WriteAllText(BackupRing.PathFor(path, 2), "two");
            Assert.Equal(BackupRing.PathFor(path, 2), BackupRing.Newest(path));
            File.WriteAllText(BackupRing.PathFor(path, 1), "one");
            Assert.Equal(BackupRing.PathFor(path, 1), BackupRing.Newest(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 还原把最新备份盖回原文件()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            File.WriteAllText(path, "broken");
            File.WriteAllText(BackupRing.PathFor(path, 1), "good");

            Assert.True(BackupRing.Restore(path, out var from));
            Assert.Equal(BackupRing.PathFor(path, 1), from);
            Assert.Equal("good", File.ReadAllText(path));
            // 还原不能留下半截临时文件。
            Assert.False(File.Exists(path + ".restore.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 没有备份时还原返回false且不动原文件()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            File.WriteAllText(path, "current");
            Assert.False(BackupRing.Restore(path, out var from));
            Assert.Null(from);
            Assert.Equal("current", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 保留份数小于一时不做事()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "cards.json");
            File.WriteAllText(path, "v1");
            BackupRing.Rotate(path, keep: 0);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 接进真实库：玩家真的能靠备份找回东西 ────────────────────────────

    [Fact]
    public void 卡组库保存两次后_能从备份里找回上一版()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "decks.json");
            var library = new DeckLibrary(path);
            var first = new ConstructedDeck { Name = "第一版", CardIds = ["a"], Options = new DeckOptions { UnrestrictedSize = true } };
            library.Save(first);
            library.Save(first with { Name = "第二版" });

            // bak1 = 第二次保存之前的样子，里面应当还是"第一版"。
            var backup = BackupRing.PathFor(path, 1);
            Assert.True(File.Exists(backup));
            var recovered = new DeckLibrary(backup);
            Assert.Null(recovered.LoadError);
            Assert.Equal("第一版", recovered.Decks.Single().Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 卡组库删除后_备份里还有被删的那一副()    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "decks.json");
            var library = new DeckLibrary(path);
            var deck = new ConstructedDeck { Name = "会被删的", CardIds = ["a"], Options = new DeckOptions { UnrestrictedSize = true } };
            library.Save(deck);
            library.Delete(deck.Id);
            Assert.Empty(new DeckLibrary(path).Decks);

            var recovered = new DeckLibrary(BackupRing.PathFor(path, 1));
            Assert.Equal("会被删的", recovered.Decks.Single().Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 文件读坏了会告诉玩家备份在哪()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "decks.json");
            var library = new DeckLibrary(path);
            library.Save(new ConstructedDeck { Name = "一", CardIds = ["a"], Options = new DeckOptions { UnrestrictedSize = true } });
            library.Save(new ConstructedDeck { Name = "二", CardIds = ["a"], Options = new DeckOptions { UnrestrictedSize = true } });

            // 主文件写坏（模拟"写出来一个逻辑上错的文件"），备份还在。
            File.WriteAllText(path, "{ 这不是 JSON");

            var broken = new DeckLibrary(path);
            Assert.NotNull(broken.LoadError);
            // 光说"读取失败，原文件保留"等于让玩家自己想办法；要直接指到备份上。
            Assert.Contains("备份", broken.LoadError);
            Assert.Contains(".bak1", broken.LoadError);
        }
        finally { Directory.Delete(dir, true); }
    }
}
