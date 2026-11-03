using System.Reflection;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.HandlerDsl;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Effects;
using Orc.Game.Model;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Packages.Fs;
using Orc.Script;
using Xunit;

namespace Kards.Ui.Tests;

/// <summary>
/// **M1 最小闭环**：DSL 文档 → 草案 → 摊平 → 装载器接受并注册 handler。
/// <para>
/// 这是「复杂 DIY」这条路上唯一能证明路真的通的一段：画布产出的 JSON 只要走完这三步，
/// 就会变成一份**自包含的组件资产**（csx 由 DSL 现场生成、带双哈希来源标记），装载器认得它。
/// 不碰任何 UI，所以失败时责任边界很清楚——要么我们的提交路径错，要么引擎的摊平错。
/// </para>
/// <para>
/// 夹具用引擎自带的 <c>samples/card-pack-diy</c>：包根有 manifest 与 lock、卡目录有 sidecar，
/// 少一样都可能「装载成功但触发链不认」。手工搭最小包是这类测试最常见的自欺。
/// </para>
/// <para>
/// 只有 <c>摊平出来的牌在真实对局里真的生效</c> 这一条会**重建进程级引擎宿主**（对局只认宿主快照里的包），
/// 所以本类进 <c>engine-host</c> 非并行集合，并在收尾把宿主还原成未初始化。
/// </para>
/// </summary>
[Collection("engine-host")]
public sealed class HandlerDslAuthoringTests : IDisposable
{
    private const string ParentId = "diy.fire-support.effect";
    private const string Member = "onPlayed";
    private const string DrawTwo =
        """{"schemaVersion":1,"actions":[{"op":"draw","count":{"lit":2}}]}""";

    /// <summary>示例卡「火力支援」的卡包全名（<c>pack/&lt;包id&gt;/&lt;卡id&gt;</c>）。</summary>
    private const string SampleCard = "pack/diy/diy.fire-support";

    private readonly string _root;
    private readonly string _pack;
    private readonly AssetEditor _editor;
    private readonly HandlerDslAuthoring _authoring;
    private bool _hostRebuilt;

    public HandlerDslAuthoringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-hdsl-authoring-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "packs", "demo");
        CopyDirectory(Path.Combine(EngineRoot(), "samples", "card-pack-diy"), _pack);

        _editor = new AssetEditor(
            new AssetRegistry(),
            new EditableRootPolicy([_root]),
            new AssetEditorOptions { PackageRoots = [Path.Combine(_root, "packs")] });
        _editor.BeginSession(EditorSessionRequest.Edit(_pack));
        _authoring = new HandlerDslAuthoring(_editor, _editor.Session!.TemporaryRoot);
    }

    public void Dispose()
    {
        // 宿主是进程级静态面：本类重建过就还原成未初始化，后面的用例才拿得到它们自己的宿主。
        // 不还原的后果是「单独跑绿、全量跑红」——那是测试编排缺陷，不是产品缺陷。
        if (_hostRebuilt) OrcEngineHost.Reset();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响断言结论（进程退出后由系统回收）。
        }
    }

    private string SessionRoot => _editor.Session!.TemporaryRoot;

    [Fact]
    public void DSL文档与草案可提交_摊平产物自包含且带生成标记()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        Assert.Equal("handler-dsl/on-played.hdsl.json", dslPath);
        Assert.True(File.Exists(Path.Combine(SessionRoot, "handler-dsl", "on-played.hdsl.json")));

        var draftPath = _authoring.CommitDraft("draw2",
            HandlerDslAuthoring.BuildDraft("demo.draw2.draft", ParentId, Member, dslPath, dslJson: DrawTwo));
        Assert.Equal("modings/draw2.draft.moding.json", draftPath);
        Assert.True(File.Exists(Path.Combine(SessionRoot, "modings", "draw2.draft.moding.json")));

        var result = _authoring.Flatten(draftPath);
        Assert.True(result.Success, result.Describe());
        Assert.Equal("demo.draw2", result.ProductId);
        Assert.Equal("effects-v4/demo.draw2.component.json", result.ProductRelativePath);
        Assert.True(File.Exists(Path.Combine(SessionRoot, "effects-v4", "demo.draw2.component.json")));

        var product = AssetSchema.Read(result.ProductJson!, "product");
        Assert.Null(product.Extends); // 产物自包含：不带 extends（R8 第 4 条）

        var handler = product.Members.Single(m => m.Name == Member).Handler!;
        Assert.StartsWith("hdsl/v1/", handler.Id, StringComparison.Ordinal);
        Assert.Contains("DrawAsync", handler.Script!, StringComparison.Ordinal);

        var generated = Assert.IsType<HandlerGeneratedBlock>(handler.Generated);
        Assert.Equal(dslPath, generated.Source);
        Assert.Equal(HandlerContent.Hash(DrawTwo), generated.DslHash);
        Assert.Equal(HandlerContent.Hash(handler.Script!), generated.TextHash);

        // 父效果的挂载会被继承，草案**不能再声明一遍**：摊平器把两边拼接、不去重，
        // 重复一条就是 handler 挂两遍。这个坑的代价是「抽 2 变抽 4」，而产物看上去毫无异常。
        Assert.Single(product.Mounts);
        Assert.Equal("engine#card.played", product.Mounts[0].Target);
        Assert.Equal(Member, product.Mounts[0].Member);
    }

    [Fact]
    public void 摊平产物能被装载器接受_且handler注册进运行期()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        var draftPath = _authoring.CommitDraft("draw2",
            HandlerDslAuthoring.BuildDraft("demo.draw2.draft", ParentId, Member, dslPath, dslJson: DrawTwo));
        Assert.True(_authoring.Flatten(draftPath).Success);

        // 装载器只扫**顶层**的 *.component.json（引擎 AssetSchema.FilePattern）——所以传 effects-v4 目录，
        // 不是包根。传错目录不会报错，只会「什么都没装」，测试会以另一种方式红。
        var runtime = GameEnginePrefabs.CreateRuntime();
        var loaded = AssetPackLoader.Load(
            runtime,
            Path.Combine(SessionRoot, "effects-v4"),
            new CSharpScriptEvaluator(),
            new CSharpSyncScriptEvaluator(),
            handlerDsl: HandlerDslAssembly.Create());

        Assert.True(loaded.Loaded, string.Join("；", loaded.Errors.Select(e => $"[{e.Category}] {e.Code}@{e.NodePath}")));
        Assert.Contains("demo.draw2", loaded.AssetIds);

        // 「装上了」不等于「跑得起来」——handler 必须真的注册进运行期。
        var product = AssetSchema.Read(
            File.ReadAllText(Path.Combine(SessionRoot, "effects-v4", "demo.draw2.component.json")), "product");
        var handlerId = product.Members.Single(m => m.Name == Member).Handler!.Id;
        Assert.NotNull(runtime.Handlers.Resolve(handlerId));
    }

    [Fact]
    public void 摊平之后Check是干净的_改过DSL就变陈旧()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        var draftPath = _authoring.CommitDraft("draw2",
            HandlerDslAuthoring.BuildDraft("demo.draw2.draft", ParentId, Member, dslPath, dslJson: DrawTwo));
        Assert.True(_authoring.Flatten(draftPath).Success);
        Assert.True(_authoring.Check(draftPath).UpToDate);

        // DSL 改一个字：草案没重摊，Check 必须报出来——这正是 M2 保存后要提示作者的那条。
        _authoring.CommitDsl("on-played", DrawTwo.Replace("\"lit\":2", "\"lit\":3"));
        Assert.False(_authoring.Check(draftPath).UpToDate);
    }

    [Fact]
    public void 名字里带路径分隔符直接拒绝()
    {
        // 名字会被拼进包内路径：允许 `..` 或分隔符就等于允许写到包外。
        Assert.Throws<ArgumentException>(() => _authoring.CommitDsl("../escape", DrawTwo));
        Assert.Throws<ArgumentException>(() => _authoring.CommitDsl("a/b", DrawTwo));
    }

    [Fact]
    public void 能当父的效果按可挂载成员列出来()
    {
        var parents = _authoring.DescribeParents();

        // 示例包 6 个组件、7 个可挂载成员（`diy.memorial.effect` 有两个：打出时／死亡时）。
        Assert.Equal(7, parents.Count);
        Assert.Equal(parents.Count, parents.Select(p => p.ParentId + "#" + p.Member).Distinct().Count());

        var played = parents.Single(p => p.ParentId == "diy.fire-support.effect" && p.Member == "onPlayed");
        Assert.Equal("engine#card.played", played.MountedTarget);
        Assert.Contains("engine#card.played", played.Label, StringComparison.Ordinal);

        // 父效果**没有**为这个成员挂信号：产物不会有触发点，界面必须标出来，
        // 否则作者会得到一份「保存成功但永远不跑」的效果。
        var orphan = parents.Single(p => p.ParentId == "diy.adjacent-cover.mitigation");
        Assert.Equal("onDamageApply", orphan.Member);
        Assert.Null(orphan.MountedTarget);
        Assert.Contains("没挂信号", orphan.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void 一条龙保存写出三份文件且Check是干净的()
    {
        var saved = _authoring.Save(new HandlerDslSaveRequest(ParentId, Member, "demo.saved", DrawTwo));

        Assert.True(saved.Success, saved.Describe());
        Assert.Equal("handler-dsl/demo.saved.hdsl.json", saved.DslPath);
        Assert.Equal("modings/demo.saved.draft.moding.json", saved.DraftPath);
        Assert.Equal("effects-v4/demo.saved.component.json", saved.ProductPath);
        Assert.True(saved.UpToDate, "刚保存完，Check 必须是干净的——否则作者会被一条永远消不掉的警告追着跑");
        Assert.Empty(saved.Errors);

        foreach (var path in new[] { saved.DslPath!, saved.DraftPath!, saved.ProductPath! })
            Assert.True(File.Exists(Path.Combine(SessionRoot, path)), path);
    }

    [Fact]
    public void 产物id不能和父效果同名()
    {
        // 同名＝摊平会覆盖父效果，而父效果正是这次摊平的输入。
        var error = Assert.Throws<ArgumentException>(() =>
            _authoring.Save(new HandlerDslSaveRequest(ParentId, Member, ParentId, DrawTwo)));
        Assert.Contains("父效果", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// **M1 的收尾那一锤**：前三步绿了只证明「产物长得对、装载器认得」，
    /// 不证明**打出来真的会抽牌**。这里把四步钉成一条：提交 → 摊平 → 开局 → 打出。
    /// </summary>
    /// <remarks>
    /// 三处刻意为之：
    /// <list type="bullet">
    ///   <item>产物要搬到磁盘：会话写的是**临时副本**（`packs/demo.tmp`），而宿主只认磁盘上的包——
    ///     不搬，测试会以「牌打出来没反应」的形式红，看起来像效果没生效，其实是根本没装。</item>
    ///   <item>改示例卡而不是新造卡：新造卡要连 manifest、lock、sidecar 一起造，任何一处漏了都是
    ///     「装载静默丢卡」；改一张已在包里的卡的 <c>effects.prefabs</c> 只有一个变量。</item>
    ///   <item>断手牌张数而不是断「日志里有 draw」：手牌是玩家看得见的事实。效果没跑、跑错张数、被别的
    ///     抽牌盖住，三种情况都会让这条断言红。</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task 摊平出来的牌在真实对局里真的生效()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        var draftPath = _authoring.CommitDraft("draw2",
            HandlerDslAuthoring.BuildDraft("demo.draw2.draft", ParentId, Member, dslPath, dslJson: DrawTwo));
        var flattened = _authoring.Flatten(draftPath);
        Assert.True(flattened.Success, flattened.Describe());

        // ① 产物搬到宿主看得见的磁盘。会话写的是 `packs/demo.tmp`（同目录的临时副本），
        //    而宿主按 `<base>/packs/<包>/manifest.json` 发现包——两者并排会被当成**两个 id 相同的包**，
        //    直接抛「An item with the same key has already been added. Key: diy」。所以另起一个 base。
        var engineBase = Path.Combine(_root, "engine");
        var enginePack = Path.Combine(engineBase, "packs", "demo");
        CopyDirectory(SessionRoot, enginePack);

        // ② 让示例卡改指我们的产物。父效果 `diy.fire-support.effect` 仍留在包里——摊平需要它存在，
        //    只是这张卡不再引用它。字符串只出现一次（prefabs 数组），替换是安全的。
        var cardFile = Path.Combine(enginePack, "cards", "diy.fire-support.card.json");
        var cardJson = File.ReadAllText(cardFile);
        Assert.Contains("\"diy.fire-support.effect\"", cardJson, StringComparison.Ordinal);
        File.WriteAllText(cardFile,
            cardJson.Replace("\"diy.fire-support.effect\"", "\"demo.draw2\"", StringComparison.Ordinal));

        // ③ 重建宿主，让它发现的包＝刚改过的那个目录。
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(engineBase);
        _hostRebuilt = true;

        var nations = Path.Combine(RepoRoot(), "proto", "data", "nations");
        var catalog = new CardCatalog();
        catalog.Load(nations, CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;
        Assert.NotEmpty(cards);

        using var session = await OrcMatchSession.CreateFromCatalogAsync(cards, 31, "hdsl-draw2", SampleCard,
            openingHand: [SampleCard], sourceDirectory: nations, firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false);
        session.Pump();

        var card = session.View.SelfHand.FirstOrDefault(c => c.CardId == SampleCard)
            ?? throw new InvalidOperationException(
                $"起手没有 {SampleCard}：[{string.Join(' ', session.View.SelfHand.Select(c => c.CardId))}]");

        // 攒费（这张是指令，1 费；回合初才涨费，所以可能要空过几轮）。
        var cost = card.Definition.Cost ?? 1;
        for (var turn = 0; turn < 8 && session.View.SelfKredits < cost; turn++)
        {
            await session.SubmitAsync(new EndTurn());
            session.Pump();
        }
        Assert.True(session.View.SelfKredits >= cost,
            $"攒不到费用：kredits={session.View.SelfKredits} cost={cost}");

        var handBefore = session.View.SelfHand.Count;
        var deckBefore = session.View.SelfDeckCount;
        var outcome = await session.SubmitAsync(new PlayCard(card.Uid));
        session.Pump();

        Assert.Equal(UiSubmitOutcome.Applied, outcome);
        // 打出 -1，DSL 的 draw 2 → 净 +1。手牌张数是玩家看得见的事实，也是这条链唯一诚实的判据。
        var handAfter = session.View.SelfHand.Count;
        Assert.True(handAfter == handBefore - 1 + 2,
            $"手牌应净 +1：打出前 {handBefore}（牌库 {deckBefore}）、打出后 {handAfter}（牌库 {session.View.SelfDeckCount}）"
            + $"；outcome={outcome} reason={session.LastRejectionReason}");
    }

    /// <summary>引擎仓库根：由构建写进程序集元数据，与二进制一一对应（不依赖环境变量）。</summary>
    private static string EngineRoot()
    {
        var recorded = typeof(HandlerDslAuthoring).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "OrcEngineRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(recorded) && Directory.Exists(recorded)) return recorded;

        var fromEnvironment = Environment.GetEnvironmentVariable("ORC_ENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment)) return fromEnvironment;

        throw new InvalidOperationException(
            "找不到引擎仓库根（程序集元数据 OrcEngineRoot 与环境变量都不可用）：本测试需要 samples/card-pack-diy。");
    }

    /// <summary>本仓库根（从测试程序集位置向上找 <c>proto/data/nations</c>，不写死盘符）。</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "proto", "data", "nations"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根（proto/data/nations）：本测试需要卡池目录。");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
