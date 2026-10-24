using Kards.Ui.OrcBridge;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// 客人侧卡池声明的守卫。这条路径没有任何端到端探针覆盖（JoinAsync 只由真实界面与重连调用），
/// 所以"读错一个字段"必须在这里变红，而不是等到跨机开局才发现对账是空的。
/// </summary>
public sealed class PackDeclarationTests
{
    [Fact]
    public void Scan_ReadsIdVersionAndGuid_InDirectoryOrder()
    {
        var root = NewRoot();
        try
        {
            WriteManifest(root, "b-pack", """{"kind":"cardset","id":"pack/b","version":"2.1.0","guid":"guid-b"}""");
            WriteManifest(root, "a-pack", """{"kind":"cardset","id":"pack/a","version":"1.0.0","guid":"guid-a"}""");

            var declared = PackDeclarations.Scan(root);

            Assert.Equal(["pack/a", "pack/b"], declared.Select(p => p.Id).ToArray());
            Assert.Equal("1.0.0", declared[0].Version);
            Assert.Equal("guid-a", declared[0].Guid);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_ToleratesBomAndFallsBackToDirectoryName()
    {
        var root = NewRoot();
        try
        {
            WriteManifest(root, "only-version", """{"version":"3.0.0"}""", withBom: true);

            var declared = PackDeclarations.Scan(root);

            var pack = Assert.Single(declared);
            Assert.Equal("only-version", pack.Id);
            Assert.Equal("3.0.0", pack.Version);
            Assert.Null(pack.Guid);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_SkipsBrokenManifests_InsteadOfFailingTheJoin()
    {
        var root = NewRoot();
        try
        {
            WriteManifest(root, "good", """{"id":"pack/good","version":"1.0.0"}""");
            WriteManifest(root, "broken", """{"id": """);
            Directory.CreateDirectory(Path.Combine(root, "no-manifest"));

            var declared = PackDeclarations.Scan(root);

            Assert.Equal(["pack/good"], declared.Select(p => p.Id).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Scan_MissingOrEmptyRoot_DeclaresNothing()
    {
        Assert.Empty(PackDeclarations.Scan(""));
        Assert.Empty(PackDeclarations.Scan(Path.Combine(Path.GetTempPath(), "no-such-packs-" + Guid.NewGuid().ToString("N"))));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteManifest(string root, string directory, string json, bool withBom = false)
    {
        var target = Path.Combine(root, directory);
        Directory.CreateDirectory(target);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (withBom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        File.WriteAllBytes(Path.Combine(target, "manifest.json"), bytes);
    }
}
