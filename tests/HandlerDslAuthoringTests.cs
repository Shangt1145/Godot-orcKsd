using System.Reflection;
using System.Text.Json.Nodes;
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
/// **写回链路**：DSL 文档 → 草案 → 摊平 → 装载器接受并注册 handler。
/// <para>
/// 这是「复杂 DIY」这条路上唯一能证明路真的通的一段：画布产出的 JSON 只要走完这三步，
/// 就会变成一份**自包含的组件资产**（csx 由 DSL 现场生成、带双哈希来源标记），装载器认得它。
/// 不碰任何 UI，所以失败时责任边界很清楚——要么我们的提交路径错，要么引擎的摊平错。
/// </para>
/// <para>
/// **父是模板，不是具体效果**（R39）：引擎把「可覆写」从资产级开关改成「父把成员声明成空位」，
/// 具体效果组件不声明空位，覆写它会被摊平器拒绝。能填的只有
/// <see cref="EffectTemplateLibrary"/> 派生的 <c>template.&lt;信号&gt;.&lt;档&gt;</c>。
/// </para>
/// <para>
/// 夹具用引擎自带的 <c>samples/card-pack-diy</c>：包根有 manifest 与 lock、卡目录有 sidecar，
/// 少一样都可能「装载成功但触发链不认」。手工搭最小包是这类测试最常见的自欺。
/// </para>
/// <para>
/// 只有两条端到端用例会**重建进程级引擎宿主**（对局只认宿主快照里的包），
/// 所以本类进 <c>engine-host</c> 非并行集合，并在收尾把宿主还原成未初始化。
/// </para>
/// </summary>
[Collection("engine-host")]
public sealed class HandlerDslAuthoringTests : IDisposable
{
    /// <summary>「打出时」的模板：信号 <c>card.played</c>、守卫档 any。</summary>
    private const string ParentId = "template.card.played.any";

    /// <summary>内置模板的空位成员名（R34 第 3 条：固定 <c>onHook</c>）。</summary>
    private const string Member = "onHook";

    private const string DrawTwo =
        """{"schemaVersion":1,"actions":[{"op":"draw","count":{"lit":2}}]}""";

    /// <summary>示例卡「火力支援」的卡包全名（<c>pack/&lt;包id&gt;/&lt;卡id&gt;</c>）。</summary>
    private const string SampleCard = "pack/diy/diy.fire-support";

    /// <summary>示例卡当前引用的效果（覆盖模式要替换的就是它）。</summary>
    private const string SampleEffect = "diy.fire-support.effect";

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
        // 模板库根由包根推导：<base>/packs/<包> → <base>/effect-templates。
        _authoring = new HandlerDslAuthoring(_editor, _editor.Session!.TemporaryRoot);
        _authoring.EnsureTemplateLibrary();
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

    /// <summary>模板库根（与 <see cref="HandlerDslAuthoring.DefaultTemplateRootFor"/> 同一推导）。</summary>
    private string TemplateRoot => HandlerDslAuthoring.DefaultTemplateRootFor(SessionRoot);

    /// <summary>按模板的视图声明造草案：漏掉字段＝把模板给的视图整段删掉。</summary>
    private string DraftFor(string draftId, string dslPath, string productId, string dslJson)
        => HandlerDslAuthoring.BuildDraft(
            draftId, ParentId, Member, dslPath,
            viewFields: _authoring.ViewFieldsOf(ParentId, Member).Values.ToArray(),
            dslJson: dslJson, productId: productId);

    [Fact]
    public void DSL文档与草案可提交_摊平产物自包含且带生成标记()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        Assert.Equal("handler-dsl/on-played.hdsl.json", dslPath);
        Assert.True(File.Exists(Path.Combine(SessionRoot, "handler-dsl", "on-played.hdsl.json")));

        var draftPath = _authoring.CommitDraft("draw2", DraftFor("demo.draw2.draft", dslPath, "demo.draw2", DrawTwo));
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

        // 模板的挂载会被继承，草案**不能再声明一遍**：摊平器把两边拼接、不去重，
        // 重复一条就是 handler 挂两遍。这个坑的代价是「抽 2 变抽 4」，而产物看上去毫无异常。
        Assert.Single(product.Mounts);
        Assert.Equal("engine#card.played", product.Mounts[0].Target);
        Assert.Equal(Member, product.Mounts[0].Member);

        // 视图字段照抄模板：草案的 view 是**整段替换**，少写一个字段就是把它删掉。
        Assert.Equal(
            ["Card", "Player", "Source"],
            product.Members.Single(m => m.Name == Member).View.Fields
                .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).OrderBy(n => n switch
                {
                    "Card" => 0,
                    "Player" => 1,
                    _ => 2,
                }));
    }

    [Fact]
    public void 摊平产物能被装载器接受_且handler注册进运行期()
    {
        var dslPath = _authoring.CommitDsl("on-played", DrawTwo);
        var draftPath = _authoring.CommitDraft("draw2", DraftFor("demo.draw2.draft", dslPath, "demo.draw2", DrawTwo));
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
        var draftPath = _authoring.CommitDraft("draw2", DraftFor("demo.draw2.draft", dslPath, "demo.draw2", DrawTwo));
        Assert.True(_authoring.Flatten(draftPath).Success);
        Assert.True(_authoring.Check(draftPath).UpToDate);

        // DSL 改一个字：草案没重摊，Check 必须报出来——这正是保存后要提示作者的那条。
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
    public void 多输出的积木图能摊平成csx_两个输出各自落变量()
    {
        // 这条证明的不是「界面画得出来」，而是**我们写出的 JSON 真能喂进引擎的读取器与生成器**：
        // 对象形态的 bind 若序列化错了（写成字符串、或键名不对），这里会以 node/pin 错误失败，
        // 而不是等作者保存成功、开局打出才发现「取到的牌是空」。
        var doc = new HandlerDocument();
        doc.Actions.Add(new HandlerNode("fetchFromDeck")
            .Set("target", HandlerValue.ViewField("Card"))
            .SetOutputBind("status", "st")
            .SetOutputBind("card", "found"));

        var saved = _authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "demo.fetch", doc.ToJsonText(HandlerCatalog.Default)));
        Assert.True(saved.Success, saved.Describe());

        var product = AssetSchema.Read(File.ReadAllText(Path.Combine(SessionRoot, saved.ProductPath!)), "product");
        var script = product.Members.Single(m => m.Name == Member).Handler!.Script!;

        // 结果对象只求值一次（多输出不能内联两次调用），两个名字各自从它上面取。
        Assert.Contains("__res", script, StringComparison.Ordinal);
        Assert.Contains("var st = ", script, StringComparison.Ordinal);
        Assert.Contains("var found = ", script, StringComparison.Ordinal);
        // 枚举输出落成 string：status 是封闭集，下游当 string 用才成立。
        Assert.Contains("Status.ToString()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 草案重复声明模板已有的挂载会被点名拒绝而不是效果翻倍()
    {
        // R40 之前这里是**静默翻倍**：产物 mounts 两条相同、handler 挂两遍、`draw 2` 抽 4 张，
        // 而产物合法、装载成功、`flatten --check` 报「最新」（我们提的 issue #4）。
        // 现在引擎在展开期拒绝，且必须点名「谁已经声明过、谁又声明了一次」——只报「重复」等于没说。
        var dslPath = _authoring.CommitDsl("dup-mount", DrawTwo);
        var draftPath = _authoring.CommitDraft("dup", HandlerDslAuthoring.BuildDraft(
            "demo.dup.draft", ParentId, Member, dslPath,
            viewFields: _authoring.ViewFieldsOf(ParentId, Member).Values.ToArray(),
            mounts: [new HandlerMount("engine#card.played", Member)],
            dslJson: DrawTwo, productId: "demo.dup"));

        var result = _authoring.Flatten(draftPath);

        Assert.False(result.Success);
        var text = string.Join("\n", result.Errors.Select(error => error.Category + " " + error.Target + " " + error.Message));
        Assert.Contains("重复声明挂载", text, StringComparison.Ordinal);
        Assert.Contains("engine#card.played", text, StringComparison.Ordinal);
        // 归属：模板是原声明者，草案是重复者——两个 id 都要出现，作者才知道该删哪一条。
        Assert.Contains(ParentId, text, StringComparison.Ordinal);
        Assert.Contains("demo.dup.draft", text, StringComparison.Ordinal);

        // 失败不落产物：留着半份产物会让「重试」变成「改一份已经存在的东西」。
        Assert.False(File.Exists(Path.Combine(SessionRoot, "effects-v4", "demo.dup.component.json")));

        // 同一成员挂到**不同**信号是合法的（原文比较，不做作用域归一化）：这是「一个成员多个触发点」的正路。
        // 信号不硬编码：从模板库里挑一个宿主需求相同的，否则会撞「挂错宿主」的另一条校验，测不到这里想测的东西。
        var parentSpec = EffectTemplateLibrary.Derive().Single(spec => spec.Id == ParentId);
        var otherSignal = EffectTemplateLibrary.Derive()
            .First(spec => spec.Signal != parentSpec.Signal && spec.RequiresHost == parentSpec.RequiresHost)
            .Signal;
        var other = _authoring.CommitDraft("dup2", HandlerDslAuthoring.BuildDraft(
            "demo.dup2.draft", ParentId, Member, dslPath,
            viewFields: _authoring.ViewFieldsOf(ParentId, Member).Values.ToArray(),
            mounts: [new HandlerMount("engine#" + otherSignal, Member)],
            dslJson: DrawTwo, productId: "demo.dup2"));
        var second = _authoring.Flatten(other);
        Assert.True(second.Success, second.Describe());
        Assert.Equal(2, AssetSchema.Read(second.ProductJson!, "product").Mounts.Count);
    }

    [Fact]
    public void 父清单来自引擎模板库_不是包里的效果组件()
    {
        var parents = _authoring.DescribeParents();
        var derived = EffectTemplateLibrary.Derive();

        Assert.Equal(derived.Count, parents.Count);
        Assert.Equal(parents.Count, parents.Select(p => p.ParentId).Distinct(StringComparer.Ordinal).Count());

        // 内置模板的成员名恒为 onHook，挂载点＝信号地址。
        Assert.All(parents, option => Assert.Equal("onHook", option.Member));
        Assert.All(parents, option => Assert.Equal("engine#" + option.Signal, option.MountedTarget));

        var played = parents.Single(option => option.ParentId == "template.card.played.any");
        Assert.Equal("card.played", played.Signal);
        Assert.Equal("Any", played.Guard);
        Assert.Contains("card.played", played.Label, StringComparison.Ordinal);
        Assert.Contains("onHook", played.Label, StringComparison.Ordinal);

        // 包里的具体效果组件**不再**是父：它们没有空位，覆写会被摊平器拒绝。
        Assert.DoesNotContain(parents, option => option.ParentId == SampleEffect);
        // 加载期信号不出模板（与任何时点都冲突，R33 的派生规则）。
        Assert.DoesNotContain(parents, option => option.Signal == "card.load");
    }

    [Fact]
    public void 视图字段来自模板的载荷声明()
    {
        // 画布能读什么由模板声明决定：card.played 的载荷是 Card／Player／Source。
        var fields = _authoring.ViewFieldsOf(ParentId, Member);
        Assert.Equal(["Card", "Player", "Source"], fields.Keys.OrderBy(k => k, StringComparer.Ordinal).OrderBy(k => k switch
        {
            "Card" => 0,
            "Player" => 1,
            _ => 2,
        }));
        Assert.Equal("entity", fields["Card"].Type);
        Assert.False(fields["Card"].Writable);

        // 成员不存在＝空表：界面据此把视图引用标红，而不是假装有。
        Assert.Empty(_authoring.ViewFieldsOf(ParentId, "onPlayed"));
        // 父不存在＝不抛（DescribePrefab 的契约是给诊断），这里只要求它别炸。
        Assert.Empty(_authoring.ViewFieldsOf("no.such.effect", Member));
    }

    [Fact]
    public void 模板库缺失时保存会整批生成()
    {
        var fresh = Path.Combine(_root, "fresh-templates");
        Assert.False(Directory.Exists(Path.Combine(fresh, "effects-v4")));

        var authoring = new HandlerDslAuthoring(_editor, SessionRoot, fresh);
        var saved = authoring.Save(new HandlerDslSaveRequest(ParentId, Member, "demo.fresh", DrawTwo));

        Assert.True(saved.Success, saved.Describe());
        var files = Directory.EnumerateFiles(Path.Combine(fresh, "effects-v4"), "*.component.json").ToArray();
        Assert.Equal(EffectTemplateLibrary.Derive().Count, files.Length);
        // 再调一次不重复写（作者可能正拿其中一份当父）。
        Assert.Equal(0, authoring.EnsureTemplateLibrary());
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
        // 同名＝摊平会覆盖父，而父正是这次摊平的输入。
        var error = Assert.Throws<ArgumentException>(() =>
            _authoring.Save(new HandlerDslSaveRequest(ParentId, Member, ParentId, DrawTwo)));
        Assert.Contains("父效果", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 父必须是模板_具体效果组件不能当父()
    {
        // R39 之前这条路是通的（示例效果声明了 authorize-moding）；现在那个开关已废止，
        // 没有空位的成员不允许被覆写。提前拦住比「三份文件落盘后再说不是空位」好得多。
        var error = Assert.Throws<ArgumentException>(() =>
            _authoring.Save(new HandlerDslSaveRequest(SampleEffect, "onPlayed", "demo.nope", DrawTwo)));
        Assert.Contains("模板", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(SessionRoot, "effects-v4", "demo.nope.component.json")));
    }

    [Fact]
    public void 覆盖必须说明替换哪个效果()
    {
        var error = Assert.Throws<ArgumentException>(() => _authoring.Save(
            new HandlerDslSaveRequest(ParentId, Member, "", DrawTwo, HandlerDslSaveMode.ReplaceExisting)));
        Assert.Contains("替换哪个效果", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 覆盖已有卡牌效果_卡牌引用改指产物而原效果保留()
    {
        var saved = _authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "", DrawTwo, HandlerDslSaveMode.ReplaceExisting, RetargetFromId: SampleEffect));

        Assert.True(saved.Success, saved.Describe());

        // 产物 id 是**确定性派生**的：作者不填，也不该填——填了，二次保存就会换成另一份资产，
        // 而卡牌还指着上一份，作者会得到「改了一次、第二次没生效」。
        Assert.Equal("diy.fire-support.effect.dsl", saved.ProductId);
        Assert.Equal("effects-v4/diy.fire-support.effect.dsl.component.json", saved.ProductPath);

        var retarget = Assert.IsType<HandlerDslRetargetResult>(saved.Retarget);
        Assert.True(retarget.Clean, string.Join("；", retarget.Failed));
        Assert.Equal(1, retarget.References);
        Assert.Equal(["cards/diy.fire-support.card.json"], retarget.Cards);

        // 卡牌引用**真的**改了。产物存在但没人引用，等于没改——这是覆盖模式唯一的意义所在。
        var card = JsonNode.Parse(File.ReadAllText(
            Path.Combine(SessionRoot, "cards", "diy.fire-support.card.json")))!.AsObject();
        var prefabs = card["components"]!.AsArray()
            .Single(component => component!["component"]!.GetValue<string>() == "effects")!["prefabs"]!.AsArray();
        Assert.Equal(["diy.fire-support.effect.dsl"], prefabs.Select(item => item!.GetValue<string>()));

        // 原效果原样留着：它是「还原」的退路（删掉就没法再改第二次）。
        Assert.True(File.Exists(Path.Combine(SessionRoot, "effects-v4", "diy.fire-support.effect.component.json")));
        Assert.True(saved.UpToDate);
    }

    [Fact]
    public void 覆盖模式二次保存落在同一份产物上_引用不再变化()
    {
        var first = _authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "", DrawTwo, HandlerDslSaveMode.ReplaceExisting, RetargetFromId: SampleEffect));
        Assert.True(first.Success, first.Describe());

        var secondDsl = DrawTwo.Replace("\"lit\":2", "\"lit\":3", StringComparison.Ordinal);
        var second = _authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "", secondDsl, HandlerDslSaveMode.ReplaceExisting, RetargetFromId: SampleEffect));

        Assert.True(second.Success, second.Describe());
        Assert.Equal(first.ProductId, second.ProductId);
        Assert.Equal(first.DslPath, second.DslPath);
        Assert.Equal(first.DraftPath, second.DraftPath);

        // 卡已经指过去了，第二次没有可改的引用——这不是失败，是幂等。
        Assert.Equal(0, second.Retarget!.References);
        Assert.True(second.Retarget.Clean);

        // 包里只有一个积木产物：`x.dsl.dsl` 正是这条要防的事。
        var products = Directory.EnumerateFiles(
                Path.Combine(SessionRoot, "effects-v4"), "*" + HandlerDslAuthoring.ComponentSuffix)
            .Select(Path.GetFileName)
            .Where(name => name!.Contains(".dsl.", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(products);

        // 第二次的内容真的写进产物了（用哈希判，比匹配生成代码的形态稳）。
        var product = AssetSchema.Read(File.ReadAllText(Path.Combine(SessionRoot, second.ProductPath!)), "product");
        Assert.Equal(HandlerContent.Hash(secondDsl),
            product.Members.Single(m => m.Name == Member).Handler!.Generated!.DslHash);
    }

    [Fact]
    public void 覆盖产物不会混进父清单()
    {
        var before = _authoring.DescribeParents().Count;
        Assert.True(_authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "", DrawTwo, HandlerDslSaveMode.ReplaceExisting, RetargetFromId: SampleEffect)).Success);

        var parents = _authoring.DescribeParents();

        // 父清单是模板库的投影：产物既不是模板、也不该出现在这里。
        Assert.Equal(before, parents.Count);
        Assert.DoesNotContain(parents, option => option.ParentId.Contains(".dsl", StringComparison.Ordinal));
        // 模板仍在，作者还能再改一次。
        Assert.Contains(parents, option => option.ParentId == ParentId && option.Member == Member);
    }

    /// <summary>
    /// **覆盖模式的收尾那一锤**：与写回链路的端到端同构，但**不再手工改卡牌 JSON**——
    /// 引用是工具改的。这条绿了，「改一个已有的脚本效果」才算真的能走。
    /// </summary>
    [Fact]
    public async Task 覆盖之后真实对局里打出走的是积木版本()
    {
        var saved = _authoring.Save(new HandlerDslSaveRequest(
            ParentId, Member, "", DrawTwo, HandlerDslSaveMode.ReplaceExisting, RetargetFromId: SampleEffect));
        Assert.True(saved.Success, saved.Describe());
        Assert.Equal(1, saved.Retarget!.References);

        // 会话写的是临时副本，宿主只认磁盘上的包：整份搬过去（卡牌引用已经是改过的）。
        var engineBase = Path.Combine(_root, "engine");
        CopyDirectory(SessionRoot, Path.Combine(engineBase, "packs", "demo"));

        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(engineBase);
        _hostRebuilt = true;

        using var session = await OpenSampleMatchAsync("hdsl-replace");
        var card = session.View.SelfHand.First(c => c.CardId == SampleCard);

        var handBefore = session.View.SelfHand.Count;
        var outcome = await session.SubmitAsync(new PlayCard(card.Uid));
        session.Pump();

        Assert.Equal(UiSubmitOutcome.Applied, outcome);
        // 打出 -1、DSL 的 draw 2 → 净 +1。与下面那条相同的判据：手牌是玩家看得见的事实。
        Assert.True(session.View.SelfHand.Count == handBefore - 1 + 2,
            $"手牌应净 +1：打出前 {handBefore}、打出后 {session.View.SelfHand.Count}；"
            + $"outcome={outcome} reason={session.LastRejectionReason}");
    }

    /// <summary>
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
        var draftPath = _authoring.CommitDraft("draw2", DraftFor("demo.draw2.draft", dslPath, "demo.draw2", DrawTwo));
        var flattened = _authoring.Flatten(draftPath);
        Assert.True(flattened.Success, flattened.Describe());

        // ① 产物搬到宿主看得见的磁盘。会话写的是 `packs/demo.tmp`（同目录的临时副本），
        //    而宿主按 `<base>/packs/<包>/manifest.json` 发现包——两者并排会被当成**两个 id 相同的包**，
        //    直接抛「An item with the same key has already been added. Key: diy」。所以另起一个 base。
        var engineBase = Path.Combine(_root, "engine");
        var enginePack = Path.Combine(engineBase, "packs", "demo");
        CopyDirectory(SessionRoot, enginePack);

        // ② 让示例卡改指我们的产物。原效果仍留在包里，只是这张卡不再引用它。
        //    字符串只出现一次（prefabs 数组），替换是安全的。
        var cardFile = Path.Combine(enginePack, "cards", "diy.fire-support.card.json");
        var cardJson = File.ReadAllText(cardFile);
        Assert.Contains("\"" + SampleEffect + "\"", cardJson, StringComparison.Ordinal);
        File.WriteAllText(cardFile,
            cardJson.Replace("\"" + SampleEffect + "\"", "\"demo.draw2\"", StringComparison.Ordinal));

        // ③ 重建宿主，让它发现的包＝刚改过的那个目录。
        OrcEngineHost.Reset();
        OrcEngineHost.Initialize(engineBase);
        _hostRebuilt = true;

        using var session = await OpenSampleMatchAsync("hdsl-draw2");
        var card = session.View.SelfHand.First(c => c.CardId == SampleCard);

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

    /// <summary>
    /// 用示例包建一局：起手就一张「火力支援」，对手不动，先把费攒够。
    /// <para>
    /// 宿主必须**已经**指向要用的包（包是宿主快照的一部分，建局时才读）。
    /// 攒费这段是必需的：这张是指令、1 费，而回合初才涨费——不空过几轮就打出，会以
    /// 「费用不足」被拒，看起来像效果没生效。
    /// </para>
    /// </summary>
    private static async Task<OrcMatchSession> OpenSampleMatchAsync(string tag)
    {
        var nations = Path.Combine(RepoRoot(), "proto", "data", "nations");
        var catalog = new CardCatalog();
        catalog.Load(nations, CardCatalog.ResolveArtResource);
        var cards = new DeckConstructionService(catalog.Cards, nations).Cards;
        Assert.NotEmpty(cards);

        var session = await OrcMatchSession.CreateFromCatalogAsync(cards, 31, tag, SampleCard,
            openingHand: [SampleCard], sourceDirectory: nations, firstPlayerIndex: 0);
        session.DriveOpponent = false;
        await session.SettleMulliganAsync(interactive: false);
        session.Pump();

        var card = session.View.SelfHand.FirstOrDefault(c => c.CardId == SampleCard)
            ?? throw new InvalidOperationException(
                $"起手没有 {SampleCard}：[{string.Join(' ', session.View.SelfHand.Select(c => c.CardId))}]");

        var cost = card.Definition.Cost ?? 1;
        for (var turn = 0; turn < 8 && session.View.SelfKredits < cost; turn++)
        {
            await session.SubmitAsync(new EndTurn());
            session.Pump();
        }

        Assert.True(session.View.SelfKredits >= cost,
            $"攒不到费用：kredits={session.View.SelfKredits} cost={cost}");
        return session;
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
