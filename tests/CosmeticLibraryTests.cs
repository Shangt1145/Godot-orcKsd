using Kards.Ui.Core;
using Xunit;

namespace Kards.Ui.Tests;

public sealed class CosmeticLibraryTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"orcksd-cosmetics-tests",Guid.NewGuid().ToString("N"));
    private static byte[] Png()=>Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
    [Fact] public void ImportedImagesAndEquippedAppearanceSurviveRestartWithoutSourceFile()
    {
        var library=new CosmeticLibrary(_root);var asset=library.Import("back","我的卡背",Png());
        library.Save(library.Profile with{CardBack=asset.Image,Headquarters=asset.Image,HeadquartersName="山城指挥所"});
        var loaded=new CosmeticLibrary(_root);
        Assert.Null(loaded.LoadError);Assert.Equal(asset,Assert.Single(loaded.Assets));Assert.Equal("山城指挥所",loaded.Profile.HeadquartersName);
        Assert.Equal(asset.Image,loaded.Profile.CardBack);Assert.Equal(Png(),loaded.ReadImage(asset.Image));
    }
    [Fact] public void RemovingAnEquippedAssetResetsOnlyItsCategoryAndRetainsSharedImage()
    {
        var library=new CosmeticLibrary(_root);var back=library.Import("back","back",Png());library.Import("decoration","desk",Png());
        library.Save(library.Profile with{CardBack=back.Image,Decoration=back.Image});library.Remove(back);
        Assert.Empty(library.Profile.CardBack);Assert.Equal(back.Image,library.Profile.Decoration);Assert.True(library.HasImage(back.Image));
        Assert.Equal("decoration",Assert.Single(new CosmeticLibrary(_root).Assets).Kind);
    }
    [Fact] public void RejectsEscapingImageIdsOversizedPngAndInvalidPublicProfile()
    {
        var library=new CosmeticLibrary(_root);
        Assert.Throws<InvalidDataException>(()=>library.ImagePath("../decks.json"));
        Assert.Throws<InvalidDataException>(()=>CosmeticImage.Inspect(new byte[CosmeticImage.MaximumBytes+1]));
        Assert.Throws<InvalidDataException>(()=>library.Save(new(){HeadquartersName=new string('名',25)}));
        Assert.Throws<InvalidDataException>(()=>library.Save(new(){Emotes=[new("same","a"),new("same","b")]}));
        Assert.False(File.Exists(Path.Combine(_root,"appearance.json")));
    }
    [Fact] public void DamagedSettingsFallBackWithoutOverwritingTheOriginal()
    {
        Directory.CreateDirectory(_root);var path=Path.Combine(_root,"appearance.json");File.WriteAllText(path,"not json");
        var library=new CosmeticLibrary(_root);Assert.NotNull(library.LoadError);Assert.Empty(library.Profile.CardBack);Assert.Equal("not json",File.ReadAllText(path));
    }
    [Fact] public void DeckAppearanceIsIndependentWhileNationSharesEmotesAndDecorationAfterReload()
    {
        var library=new CosmeticLibrary(_root);var asset=library.Import("back","back",Png());
        library.SaveFor("first","britain",new(){CardBack=asset.Image,HeadquartersName="第一总部",Decoration=asset.Image,Emotes=[new("hello","收到")]});
        library.SaveFor("second","britain",library.Resolve("second","britain") with{HeadquartersName="第二总部"});
        library=new CosmeticLibrary(_root);
        Assert.Equal(asset.Image,library.Resolve("first","britain").CardBack);
        Assert.Empty(library.Resolve("second","britain").CardBack);
        Assert.Equal("第一总部",library.Resolve("first","britain").HeadquartersName);
        Assert.Equal("第二总部",library.Resolve("second","britain").HeadquartersName);
        Assert.Equal(asset.Image,library.Resolve("second","britain").Decoration);
        Assert.Equal("收到",Assert.Single(library.Resolve("second","britain").Emotes).Label);
        Assert.Empty(library.Resolve("third","japan").Decoration);
        Assert.Equal(CosmeticProfile.MaximumEmotes,library.Resolve("third","japan").Emotes.Length);
    }
    [Fact] public void LegacyGlobalAppearanceMigratesOnlyToFirstSelectedDeckAndNation()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root,"appearance.json"),"""
            {"Profile":{"HeadquartersName":"旧总部","Emotes":[{"Id":"hello","Label":"旧问候","Image":""}]},"Assets":[]}
            """);
        var library=new CosmeticLibrary(_root);library.SetContext("first","britain");library.Save(library.Profile);
        library.SetContext("second","japan");
        Assert.Empty(library.Profile.HeadquartersName);
        var reloaded=new CosmeticLibrary(_root);
        Assert.Equal("旧总部",reloaded.Resolve("first","britain").HeadquartersName);
        Assert.Equal("旧问候",Assert.Single(reloaded.Resolve("third","britain").Emotes).Label);
        Assert.Empty(reloaded.Resolve("second","britain").HeadquartersName);
    }
    [Fact] public void RemovingAnAssetUpdatesEveryDeckWithoutResettingOtherCategories()
    {
        var library=new CosmeticLibrary(_root);var back=library.Import("back","back",Png());
        library.SaveFor("first","britain",new(){CardBack=back.Image,Decoration=back.Image,Headquarters=back.Image});
        library.SaveFor("second","japan",new(){CardBack=back.Image});library.Remove(back);
        var reloaded=new CosmeticLibrary(_root);
        Assert.Empty(reloaded.Resolve("first","britain").CardBack);Assert.Empty(reloaded.Resolve("second","japan").CardBack);
        Assert.Equal(back.Image,reloaded.Resolve("first","britain").Decoration);
        Assert.Equal(back.Image,reloaded.Resolve("first","britain").Headquarters);
    }
    [Fact] public void EightEmotesAreSupportedButDuplicateOrNinthEmotesAreRejected()
    {
        var library=new CosmeticLibrary(_root);library.Save(new());
        Assert.Equal(8,new CosmeticLibrary(_root).Profile.Emotes.Length);
        Assert.Throws<InvalidDataException>(()=>library.Save(new(){Emotes=CosmeticProfile.DefaultEmotes().Append(new("ninth","第九条")).ToArray()}));
    }
    [Fact] public void OriginalEmoteVariantIsValidatedPersistedAndIncludedInImageTransfers()
    {
        var library=new CosmeticLibrary(_root);var image=library.CacheImage(Png());var variant=library.CacheImage(Png().Concat(new byte[]{0}).ToArray());
        var emote=new CosmeticEmote("original-test","七转\n八起",image,"special","CFCCB9",variant);
        library.Save(new(){Emotes=[emote]});
        var loaded=new CosmeticLibrary(_root).Profile;
        Assert.Equal(emote,Assert.Single(loaded.Emotes));Assert.Contains(variant,loaded.Images());
        Assert.Throws<InvalidDataException>(()=>(loaded with{Emotes=[emote with{EnemyImage="../invalid"}]}).Checked());
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
