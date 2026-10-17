using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Orc.Game.Cards;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class DemoMechanicsUpgradeTests
{
    [Fact]
    public void UpgradeIsBackedUpIdempotentAndPreservesCustomDefinitions()
    {
        var path=Path.Combine(Path.GetTempPath(),"OrC-drag-upgrade-"+Guid.NewGuid().ToString("N"),"packs.json");
        var library=new DiyWorkshopLibrary(path);
        var legacy=new KeywordProgram { Skills=[new(){Id="fallback",Name="撤退",Cost="0",SpendMove=false,SpendAttack=false,
            Actions=[new(){Op="retreat",Target="self"}]}] }.ToJson();
        var edited=new DiyKeywordDraft { Id="kw-demo-jidong",Name="自定义机动",ProgramJson=new KeywordProgram{Rules=[new(){Rule="attack.limit",Value="3"}]}.ToJson() };
        library.Save(new(){Id="pack/diy-demo-mechanics",Name="机制示范包",Keywords=[new(){Id="kw-demo-kuijun",Name="溃退",ProgramJson=legacy},edited]});
        var original=File.ReadAllBytes(path);
        Assert.True(DemoMechanicsUpgrade.Apply(library,path));
        var backup=Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.bak"));
        Assert.Equal(original,File.ReadAllBytes(backup));
        Assert.Equal(DemoMechanicsUpgrade.Retreat().ToJson(),library.Packs[0].Keywords[0].ProgramJson);
        Assert.Equal(edited,library.Packs[0].Keywords[1]);
        var migrated=File.ReadAllBytes(path);
        Assert.False(DemoMechanicsUpgrade.Apply(library,path));Assert.Equal(migrated,File.ReadAllBytes(path));
    }

    [Fact]
    public void UpgradeDeliveredDemoWhenExplicitlyRequested()
    {
        if(Environment.GetEnvironmentVariable("UPGRADE_DEMO_DRAG")!="1")return;
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Godot","app_userdata","OrC-KSD · 卡牌指挥部","diy-packs.json");
        DemoMechanicsUpgrade.Apply(new DiyWorkshopLibrary(path),path);
    }
}
