using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// 联机探针。<c>--net-probe</c> 只验证客户端协议栈能连上；
/// <c>--net-play</c> 真的开一局（占座 + 加 AI + 开局），用来验证整条对局链路。
/// </summary>
public partial class Main
{
    private static string ProbePacks()
        => System.Environment.GetEnvironmentVariable("ORC_PROBE_PACKS")
           ?? @"H:\Working Folder\OrC-KSD-github\samples\card-pack-demo";

    private static string ProbeSandbox() => ProjectSettings.GlobalizePath("user://net-probe");

    /// <summary>
    /// 从卡包目录现场组一副合法卡组（39 张）：单国、按稀有度同名上限。
    /// 纯读 JSON，不碰引擎类型——探针要能对**任意**包工作，不能硬编码卡 id。
    /// </summary>
    private static string[] ProbeDeck(string packDir, int size = 39)
    {
        var root = packDir.Replace('\\', '/').TrimEnd('/') + "/cards";
        var cards = new List<(string Id, string Name, string Faction, int Limit)>();
        foreach (var file in DirAccess.GetFilesAt(root) ?? [])
        {
            if (!file.EndsWith(".card.json", StringComparison.OrdinalIgnoreCase)) continue;
            using var handle = Godot.FileAccess.Open($"{root}/{file}", Godot.FileAccess.ModeFlags.Read);
            if (handle is null) continue;
            if (Json.ParseString(handle.GetAsText()) is not { VariantType: Variant.Type.Dictionary } parsed) continue;
            var card = parsed.AsGodotDictionary();
            var id = card.TryGetValue("id", out var idValue) ? idValue.AsString() : "";
            var name = card.TryGetValue("name", out var nameValue) ? nameValue.AsString() : id;
            var faction = "";
            var limit = 4;
            var isOrder = false;
            var hasEffects = false;
            if (card.TryGetValue("components", out var components) && components.VariantType == Variant.Type.Array)
            {
                foreach (var entry in components.AsGodotArray())
                {
                    if (entry.VariantType != Variant.Type.Dictionary) continue;
                    var component = entry.AsGodotDictionary();
                    var kind = component.TryGetValue("component", out var kindValue) ? kindValue.AsString() : "";
                    switch (kind)
                    {
                        case "factionCost" when component.TryGetValue("faction", out var factionValue):
                            faction = factionValue.AsString();
                            break;
                        case "tagData" when component.TryGetValue("rarity", out var rarityValue):
                            limit = rarityValue.AsString().ToLowerInvariant() switch
                            {
                                "elite" => 1, "special" => 2, "limited" => 3, _ => 4,
                            };
                            break;
                        case "typeCategory" when component.TryGetValue("type", out var typeValue):
                            foreach (var type in typeValue.AsGodotArray())
                                if (type.AsString() == "order") isOrder = true;
                            break;
                        case "effects":
                            hasEffects = true;
                            break;
                    }
                }
            }

            // 指令卡缺 effects 组件时引擎不生成定义（静默丢弃）——放进卡组必然 deck-invalid。
            if (isOrder && !hasEffects) continue;
            if (id.Length > 0 && faction.Length > 0) cards.Add((id, name, faction, limit));
        }

        if (cards.Count == 0) throw new Exception($"卡组无法构建：{root} 下没有可用卡定义");

        // 单一主国（盟国留空）——同国才能过 DeckRules 的主国/盟国配对校验。
        var factionWinner = cards.GroupBy(c => c.Faction).OrderByDescending(g => g.Count()).First().Key;
        var pool = cards.Where(c => c.Faction == factionWinner).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

        // 同名上限按**卡名**累计（同名的不同版本共用额度），不是按 id。
        var deck = new List<string>(size);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        while (deck.Count < size)
        {
            var progressed = false;
            foreach (var card in pool)
            {
                var used = byName.GetValueOrDefault(card.Name);
                if (used >= card.Limit) continue;
                deck.Add(card.Id);
                byName[card.Name] = used + 1;
                progressed = true;
                if (deck.Count == size) break;
            }
            if (!progressed) break;
        }

        GD.Print($"[net-play] deck built faction={factionWinner} cards={cards.Count} pool={pool.Count} size={deck.Count}");
        if (deck.Count != size) throw new Exception($"卡组只凑到 {deck.Count}/{size} 张（{factionWinner} 池不足）");

        // 房间填充按**应用层全名**查卡池（DeckRules 直接查 id→定义表，不做裸 id 解析），
        // 所以这里必须补包前缀，否则整副卡组都会被判"不在已装载卡池中"。
        var packageId = ReadPackageId(packDir);
        return deck.Select(id => $"pack/{packageId}/{id}").ToArray();
    }

    private static string ReadPackageId(string packDir)
    {
        var manifest = packDir.Replace('\\', '/').TrimEnd('/') + "/manifest.json";
        using var handle = Godot.FileAccess.Open(manifest, Godot.FileAccess.ModeFlags.Read);
        if (handle is null) throw new Exception($"读不到 manifest：{manifest}");
        if (Json.ParseString(handle.GetAsText()) is not { VariantType: Variant.Type.Dictionary } parsed)
            throw new Exception($"manifest 不是 JSON 对象：{manifest}");
        var id = parsed.AsGodotDictionary().TryGetValue("id", out var value) ? value.AsString() : "";
        if (id.Length == 0) throw new Exception($"manifest 缺 id：{manifest}");
        return id;
    }

    private async void NetProbe()
    {
        try
        {
            var packs = ProbePacks();
            GD.Print($"[net-probe] packs={packs}");
            await using var server = await ServerLauncher.StartAsync(packs, ProbeSandbox());
            GD.Print($"[net-probe] server ready port={server.Port}");

            await using var feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            GD.Print("[net-probe] client connected + initialized");

            var info = await feed.RoomInfoAsync();
            GD.Print($"[net-probe] room.info ok={info.Ok} code={info.Code}");
            if (info.Details is { } details)
            {
                GD.Print($"[net-probe] state={details["state"]} packPolicy={details["packPolicy"]}");
                if (details["addresses"] is { } addresses)
                    foreach (var address in addresses.AsArray())
                        GD.Print($"[net-probe]   address {address?["ip"]}:{address?["port"]}");
            }

            if (!info.Ok) throw new Exception("room.info 返回失败：" + info.Code);

            GD.Print("NET_PROBE_OK");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// 真的开一局：房主占座 → 加 AI → 开局 → 等首帧视图 → 打印概况并尝试出一张单位。
    /// 只打印不评判，用来确认"链路能走多远、卡在哪一步"。
    /// </summary>
    private async void NetPlay()
    {
        RemoteMatchFeed? feed = null;
        ServerLauncher? server = null;
        try
        {
            // 卡组必须 39 张（DeckRules.DefaultRequiredSize）且同国，故从包内现场组建，
            // 不再硬编码卡 id——换包就自动跟着换。
            var packs = ProbePacks();
            GD.Print($"[net-play] packs={packs}");
            var deck = ProbeDeck(packs);

            server = await ServerLauncher.StartAsync(packs, ProbeSandbox());
            GD.Print($"[net-play] server port={server.Port}");

            feed = await RemoteMatchFeed.ConnectAsync("127.0.0.1", server.Port);
            feed.ErrorRaised += message => GD.Print($"[net-play] error: {message}");
            feed.ProjectionReady += (view, actions) =>
                GD.Print($"[net-play] view phase={view.Phase} turn={view.Turn} active={view.ActivePlayerSide} " +
                         $"hand={view.SelfHand.Count} line={view.SelfLine.Count} kredits={view.SelfKredits}");

            var token = await feed.ClaimSeatAsync(0, "local", "probe", deck);
            GD.Print($"[net-play] claim={(token is null ? "FAILED" : "ok token=" + token[..Math.Min(8, token.Length)])}");
            if (token is null) { GetTree().Quit(1); return; }

            var ai = await feed.AddAiAsync(1, deck);
            GD.Print($"[net-play] add_ai={ai}");
            if (!ai) { GetTree().Quit(1); return; }

            var started = await feed.StartMatchAsync();
            GD.Print($"[net-play] start={started}");
            if (!started) { GetTree().Quit(1); return; }

            for (var i = 0; i < 100 && feed.CurrentView is null; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            var view = feed.CurrentView;
            if (view is null) { GD.Print("NET_PLAY_NO_VIEW"); GetTree().Quit(1); return; }

            GD.Print($"[net-play] first view phase={view.Phase} hand={view.SelfHand.Count} " +
                     $"line={view.SelfLine.Count} hq={view.SelfHq?.Uid}");

            // 换牌相位必须先确认，否则永远进不了 play 相位，出牌一律被拒。
            if (view.Phase == "mulligan")
            {
                var done = await feed.MulliganDoneAsync(0);
                GD.Print($"[net-play] mulligan.done={done}");
                for (var i = 0; i < 100 && feed.CurrentView?.Phase == "mulligan"; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                view = feed.CurrentView;
                GD.Print($"[net-play] after mulligan phase={view?.Phase} hand={view?.SelfHand.Count} " +
                         $"active={view?.ActivePlayerSide} kredits={view?.SelfKredits}");
            }

            // 能出就出一张：验证 play.unit 的 ref/owner/slot 三参是不是这条链。
            // 注意首回合只有 1 点资源点，挑当前负担得起的牌；出不起就结束回合攒费再试。
            var played = false;
            for (var round = 0; round < 5 && !played; round++)
            {
                view = feed.CurrentView;
                if (view is null) break;

                var affordable = view.SelfHand
                    .Where(c => (c.EffectiveCost ?? int.MaxValue) <= view.SelfKredits)
                    .OrderBy(c => c.EffectiveCost ?? int.MaxValue)
                    .FirstOrDefault();

                if (affordable is null)
                {
                    GD.Print($"[net-play] round {round}: 手牌里没有 ≤{view.SelfKredits} 费的卡，结束回合");
                    await feed.SubmitAsync(new Kards.Ui.Contracts.EndTurn());
                    for (var i = 0; i < 200 && feed.CurrentView?.Turn == view.Turn; i++)
                        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                    continue;
                }

                var uid = affordable.Uid;
                var cardId = affordable.CardId;
                var before = view.SelfHand.Count;
                GD.Print($"[net-play] play attempt cost={affordable.EffectiveCost} kredits={view.SelfKredits} card={cardId}");
                await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(uid, 0));
                for (var i = 0; i < 100 && feed.CurrentView?.SelfHand.Count == before; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

                var after = feed.CurrentView;
                played = after is not null && after.SelfHand.Count < before;
                GD.Print($"[net-play] play {cardId}: hand {before} -> {after?.SelfHand.Count} " +
                         $"line={after?.SelfLine.Count} kredits={after?.SelfKredits}");
            }

            GD.Print("NET_PLAY_OK");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
        finally
        {
            if (feed is not null) await feed.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
        }
    }
}
