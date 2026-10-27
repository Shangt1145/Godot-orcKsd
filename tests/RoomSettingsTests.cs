using System.Text;
using System.Text.Json.Nodes;
using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// P7 房间设置：房间名与固定端口。
///
/// 判据一律落在"文件里到底是什么"和"给玩家看的那句话是什么"，
/// 不看调用方有没有把值塞进控件——控件可以显示错的东西，字节和文案不会。
/// </summary>
public class RoomSettingsTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orcksd-p7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewPath() => Path.Combine(NewDir(), "room-settings.json");

    // ── 默认值 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 首次开房用默认值_端口交给系统分配()
    {
        var settings = new RoomSettings(NewPath());
        Assert.Null(settings.LoadError);
        Assert.Equal("", settings.Name);
        Assert.False(settings.FixedPort);
        Assert.Equal(RoomSettings.DefaultPort, settings.Port);
        // 不固定端口时必须向服务端要 0（＝系统分配），否则等于默默固定了端口。
        Assert.Equal(0, settings.RequestedPort);
    }

    [Fact]
    public void 目录不存在也能保存_保存后重读值不变()
    {
        var dir = Path.Combine(NewDir(), "nested", "deeper");
        var path = Path.Combine(dir, "room-settings.json");
        var settings = new RoomSettings(path) { Name = "周五夜战", FixedPort = true, Port = 5123 };
        settings.Save();

        Assert.True(File.Exists(path));
        var reloaded = new RoomSettings(path);
        Assert.Null(reloaded.LoadError);
        Assert.Equal("周五夜战", reloaded.Name);
        Assert.True(reloaded.FixedPort);
        Assert.Equal(5123, reloaded.Port);
        Assert.Equal(5123, reloaded.RequestedPort);
    }

    // ── 房间名 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 房间名去掉控制字符与首尾空白_并截到上限()
    {
        var settings = new RoomSettings(NewPath());
        // 房间名会进 UDP 广播载荷，换行/制表符会让列表里那一行断开。
        settings.Name = "  星\r\n辰\t作战  ";
        Assert.Equal("星辰作战", settings.Name);

        settings.Name = new string('战', RoomSettings.NameLimit + 10);
        Assert.Equal(RoomSettings.NameLimit, settings.Name.Length);

        settings.Name = "   ";
        Assert.Equal("", settings.Name);
        settings.Name = null!;   // 控件清空时给的就是 null
        Assert.Equal("", settings.Name);
    }

    // ── 端口 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1024, true)]
    [InlineData(4717, true)]
    [InlineData(65535, true)]
    [InlineData(1023, false)]   // 1024 以下要管理员权限
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void 端口只接受1024到65535(int port, bool valid)
        => Assert.Equal(valid, RoomSettings.ValidPort(port));

    [Fact]
    public void 手填端口解析失败不改状态_非法值回落默认端口()
    {
        var settings = new RoomSettings(NewPath());
        settings.Port = 5123;

        Assert.True(RoomSettings.TryParsePort(" 6000 ", out var parsed));
        Assert.Equal(6000, parsed);
        Assert.False(RoomSettings.TryParsePort("abc", out _));
        Assert.False(RoomSettings.TryParsePort("80", out _));
        Assert.False(RoomSettings.TryParsePort("", out _));
        Assert.False(RoomSettings.TryParsePort(null, out _));

        // 直接赋非法值不抛，但也不能把房间开到一个 1023 端口上。
        settings.Port = 80;
        Assert.Equal(RoomSettings.DefaultPort, settings.Port);
    }

    // ── 读不懂的存档 ────────────────────────────────────────────────────────

    [Fact]
    public void 存档比游戏新_提示升级游戏且原文件字节不变()
    {
        var path = NewPath();
        var bytes = Encoding.UTF8.GetBytes(
            $"{{\"Schema\":{SchemaVersioning.Latest + 1},\"Name\":\"未来房间\",\"FixedPort\":true,\"Port\":5000}}");
        File.WriteAllBytes(path, bytes);

        var settings = new RoomSettings(path);
        Assert.NotNull(settings.LoadError);
        Assert.Contains("更新版本", settings.LoadError);
        // 读不懂就用默认值，但绝不写回——原文件是玩家升级后唯一的指望。
        Assert.Equal("", settings.Name);
        Assert.Throws<IOException>(() => settings.Save());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void 格式太旧与太新的提示不同()
    {
        var oldPath = NewPath();
        File.WriteAllText(oldPath, "{\"Schema\":0,\"Name\":\"老房间\",\"FixedPort\":false,\"Port\":4717}");
        var old = new RoomSettings(oldPath);
        Assert.NotNull(old.LoadError);
        Assert.Contains("太旧", old.LoadError);
        Assert.DoesNotContain("更新版本", old.LoadError);

        var newPath = NewPath();
        File.WriteAllText(newPath, $"{{\"Schema\":{SchemaVersioning.Latest + 1}}}");
        var fresh = new RoomSettings(newPath);
        Assert.NotNull(fresh.LoadError);
        Assert.Contains("更新版本", fresh.LoadError);
        Assert.DoesNotContain("太旧", fresh.LoadError);
    }

    [Fact]
    public void 坏JSON与多余字段都不静默吞掉_原文件保留()
    {
        var brokenPath = NewPath();
        var brokenBytes = Encoding.UTF8.GetBytes("{ 这不是 json");
        File.WriteAllBytes(brokenPath, brokenBytes);
        var broken = new RoomSettings(brokenPath);
        Assert.NotNull(broken.LoadError);
        Assert.Throws<IOException>(() => broken.Save());
        Assert.Equal(brokenBytes, File.ReadAllBytes(brokenPath));

        // 多余字段＝别的程序/更新的版本写的，宁可报错也不猜它是什么意思。
        var extraPath = NewPath();
        File.WriteAllText(extraPath, "{\"Schema\":1,\"Name\":\"x\",\"FixedPort\":false,\"Port\":4717,\"Password\":\"hunter2\"}");
        var extra = new RoomSettings(extraPath);
        Assert.NotNull(extra.LoadError);
    }

    [Fact]
    public void 读失败后不改原文件_但本次运行仍能开房()
    {
        var path = NewPath();
        var bytes = Encoding.UTF8.GetBytes("{\"Schema\":99}");
        File.WriteAllBytes(path, bytes);

        var settings = new RoomSettings(path);
        Assert.NotNull(settings.LoadError);
        // 读不懂不该连房间都开不了：退回默认值，端口仍然交给系统分配。
        Assert.Equal(0, settings.RequestedPort);
        Assert.Equal(RoomSettings.DefaultPort, settings.Port);
    }
}
