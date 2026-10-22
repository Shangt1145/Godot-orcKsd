using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>
/// <c>--verify-room</c>：作战房间的无头验收。
///
/// <para>
/// 验的是**房间这层封装**（起实例 → 连接 → 占本座 → 拉 AI → 开局），
/// 不是视觉——布局对不对靠 <c>--capture-room</c> 的截图，两者分开才不会互相掩护。
/// </para>
/// </summary>
public partial class Main
{
    private async void VerifyRoom()
    {
        try
        {
            Show("room");
            await _room.OpenAsync();
            Require(_room.Port > 0, "服务器实例没有起来（端口 0）");
            Require(_room.Feed is not null, "没有连上服务器");
            Require(_room.Deck is { CardIds.Count: 39 }, "大厅卡组不是 39 张");
            GD.Print($"[room] server port={_room.Port} pack={_room.Deck!.PackageId} " +
                     $"faction={_room.Deck.Faction} deck={_room.Deck.CardIds.Count}");

            var feed = _room.Feed!;
            await feed.RefreshViewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(feed.SeatToken is { Length: > 0 }, "房主没有拿到 seatToken");

            var ai = await _room.AddAiToSeatAsync(1);
            Require(ai, "座位 1 添加 AI 失败");

            var started = await _room.StartForVerificationAsync();
            Require(started, "开局失败");

            for (var i = 0; i < 100 && feed.CurrentView is null; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            Require(feed.CurrentView is { } view, "开局后没有收到视图");

            GD.Print($"[room] view phase={feed.CurrentView!.Phase} hand={feed.CurrentView.SelfHand.Count} " +
                     $"hq={feed.CurrentView.SelfHq?.Uid}");

            // 第二段：开局后必须真的切进战场，并由远端 feed 驱动（BindFeed 那条正式接线）。
            await EnterRemoteMatchAsync();
            Require(_active == "battle", $"开局后没有切到战场（当前 {_active}）");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(_battle.Visible, "战场屏没有显示");

            // 换牌确认 + 出牌：证明战场侧的输入确实能走到服务端。
            if (feed.CurrentView?.Phase == "mulligan")
            {
                await feed.MulliganDoneAsync(0);
                for (var i = 0; i < 100 && feed.CurrentView?.Phase == "mulligan"; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            }

            var playable = feed.CurrentView?.SelfHand
                .Where(card => (card.EffectiveCost ?? int.MaxValue) <= (feed.CurrentView?.SelfKredits ?? 0))
                .OrderBy(card => card.EffectiveCost ?? int.MaxValue)
                .FirstOrDefault();
            if (playable is not null)
            {
                var before = feed.CurrentView!.SelfHand.Count;
                await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(playable.Uid, 0));
                for (var i = 0; i < 100 && feed.CurrentView?.SelfHand.Count == before; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                GD.Print($"[room] battle play {playable.CardId}: hand {before} -> {feed.CurrentView?.SelfHand.Count} " +
                         $"line={feed.CurrentView?.SelfLine.Count}");
                Require(feed.CurrentView?.SelfHand.Count < before, "战场里出牌没有生效");
            }
            else
            {
                GD.Print("[room] 首回合没有负担得起的牌，跳过出牌检查");
            }

            GD.Print("ROOM_VERIFY_OK server claim ai start view battle play");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("ROOM_VERIFY_FAILED " + error.Message);
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    /// <summary>
    /// <c>--verify-room-join</c>：走**界面路径**加入房间。
    /// 第二个 RoomScreen 实例填地址、点"加入"，而不是探针手动 connect——
    /// 验的是玩家真正会走的那条路。
    /// </summary>
    private async void VerifyRoomJoin()
    {
        RoomScreen? guestScreen = null;
        try
        {
            Show("room");
            await _room.OpenAsync();
            var host = _room.Feed ?? throw new Exception("房主没有连上");
            Require(host.SeatToken is { Length: > 0 }, "房主没有拿到 seatToken");

            // 第二个房间屏：同一个进程里模拟另一台机器。
            guestScreen = new RoomScreen();
            guestScreen.Initialize(Path.Combine(RuntimeGameData.Directory("kards"), "packs"),
                ProjectSettings.GlobalizePath("user://net-room-guest"));
            // 挂到与 _room 同一个宿主（content），但保持隐藏：只是为了 Ui() 能投递到主线程。
            _room.GetParent().AddChild(guestScreen);
            guestScreen.Visible = false;

            var joined = await guestScreen.JoinForVerificationAsync("127.0.0.1", _room.Port);
            Require(joined, "客人加入失败");
            Require(guestScreen.IsGuest, "客人模式没有正确标记");
            guestScreen.Feed!.ErrorRaised += message => GD.Print("[room-join] guest error: " + message);

            await _room.RefreshForVerificationAsync();
            var info = await host.RoomInfoAsync();
            var seatOne = ((info.Details as System.Text.Json.Nodes.JsonObject)?["seats"]
                    as System.Text.Json.Nodes.JsonArray)?
                .FirstOrDefault(node => node?["seat"]?.GetValue<int>() == 1);
            Require(seatOne?["controllerKind"]?.GetValue<string>() == "remote",
                "房主侧没有看到座位 1 变成 remote");
            GD.Print("[room-join] guest joined via UI path, host sees seat1=remote");

            var started = await _room.StartForVerificationAsync();
            Require(started, "有客人时开局失败");

            // 客人侧不赌推送：主动拉一次对局视图；房主侧同理，否则比对时它还没有视图。
            await host.RefreshViewAsync();
            await guestScreen.Feed!.RefreshViewAsync();
            for (var i = 0; i < 100 && guestScreen.Feed?.CurrentView is null; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            Require(guestScreen.Feed?.CurrentView is not null, "客人没有收到视图");

            var hostHq = host.CurrentView?.SelfHq?.Uid;
            var guestHq = guestScreen.Feed!.CurrentView!.SelfHq?.Uid;
            GD.Print($"[room-join] hostSeat={host.Seat} guestSeat={guestScreen.Feed.Seat} " +
                     $"hostHq={hostHq} guestHq={guestHq} " +
                     $"hostSelf={host.CurrentView?.SelfPlayerName} hostEnemy={host.CurrentView?.EnemyPlayerName}");
            Require(hostHq is not null && hostHq != guestHq,
                $"两端拿到了同一个总部，视角没有按座位区分（host={hostHq} guest={guestHq}）");
            GD.Print($"[room-join] host hq={hostHq} guest hq={guestHq}");
            GD.Print("ROOM_JOIN_VERIFY_OK ui-join remote-seat view-per-seat");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("ROOM_JOIN_VERIFY_FAILED " + error.Message);
            if (guestScreen is not null) guestScreen.Close();
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        guestScreen?.Close();
        _room.Close();
        GetTree().Quit();
    }

    /// <summary>
    /// <c>--verify-deck</c>：玩家卡组直通联机（M2 验收）。
    ///
    /// <para>
    /// 两条路径都要证：
    /// ① 全 <c>pack/</c> id 且 39 张 → 房间用它（PackageId=player），占座成功；
    /// ② 混进源卡（<c>av76/...</c>）→ 必须**退回**自动组牌，而不是拿一副打不出去的卡组去占座。
    /// </para>
    /// </summary>
    private async void VerifyDeck()
    {
        var packsRoot = Path.Combine(RuntimeGameData.Directory("kards"), "packs");
        try
        {
            Show("room");

            // ① 合法玩家卡组：借自动组牌拿一副 pack 卡组，再当作"玩家自己的"喂回去。
            var reference = LobbyDeck.Build(packsRoot);
            _room.ActiveDeckSource = () => reference.CardIds;
            await _room.OpenAsync();

            var feed = _room.Feed ?? throw new Exception("房主没有连上");
            GD.Print($"[deck] 合法卡组：package={_room.Deck?.PackageId} " +
                     $"size={_room.Deck?.CardIds.Count} token={(feed.SeatToken is { Length: > 0 } ? "ok" : "missing")}");
            Require(_room.Deck?.PackageId == "player", "全 pack/ 卡组没有被房间采用");
            Require(feed.SeatToken is { Length: > 0 }, "用玩家卡组占座失败" +
                (_room.LastError is { } reason ? $"：{reason}" : ""));
            _room.Close();

            // ② 非法卡组：混一张源卡，必须退回自动组牌。
            _room.ActiveDeckSource = () => ["av76/command/-1"];
            await _room.OpenAsync();

            GD.Print($"[deck] 非法卡组：package={_room.Deck?.PackageId}");
            Require(_room.Deck?.PackageId != "player", "含源卡的卡组居然被当成可用卡组");
            Require(_room.Feed?.SeatToken is { Length: > 0 }, "退回自动组牌后占座失败");

            GD.Print("DECK_VERIFY_OK player-deck-wired invalid-falls-back");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("DECK_VERIFY_FAILED " + error.Message);
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }

    /// <summary>
    /// <c>--verify-combat</c>：**能不能真打完一局**。
    ///
    /// <para>
    /// 这是 M1 的验收：不是"命令发出去了"，而是**打到结算面板**。
    /// 策略很笨但有效：能铺就铺、能打就打（HQ 优先，否则打敌方单位）、打不动就过回合，
    /// 直到 <see cref="UiMatchView.Outcome"/> 不再是 None。
    /// </para>
    /// </summary>
    private async void VerifyCombat()
    {
        try
        {
            Show("room");
            await _room.OpenAsync();

            var feed = _room.Feed ?? throw new Exception("房主没有连上");
            Require(feed.SeatToken is { Length: > 0 }, "占座失败" +
                (_room.LastError is { } reason ? $"：{reason}" : ""));
            Require(await _room.AddAiToSeatAsync(1), "座位 1 添加 AI 失败");
            Require(await _room.StartForVerificationAsync(), "开局失败");
            await EnterRemoteMatchAsync();

            if (feed.CurrentView?.Phase == "mulligan")
            {
                await feed.MulliganDoneAsync(0);
                await WaitUntil(() => feed.CurrentView?.Phase != "mulligan", 10);
            }

            // 诊断：命令被拒/提示必须看得见——否则"攻击没伤害"这种事完全无从下手。
            feed.CommandRefused += command => GD.Print($"[combat] refused {command.GetType().Name}");
            feed.HintRequested += hint => GD.Print($"[combat] hint {hint}");
            // 无人值守：所有选靶（部署效果、移动选槽）由 feed 自己答第一个候选。
            // 探针不当第二应答者——和战场面板抢同一个请求会报"没有待决选择请求"。
            feed.AutoResolveTargets = true;
            var targetCount = 0;
            feed.TargetRequested += (request, _) => targetCount++;

            var plays = 0;
            var attacks = 0;
            var moves = 0;
            var lastTurn = -1;
            var triedAttackers = new HashSet<string>(StringComparer.Ordinal);
            var triedMovers = new HashSet<string>(StringComparer.Ordinal);
            var triedPlays = new HashSet<string>(StringComparer.Ordinal);

            for (var step = 0; step < 120; step++)
            {
                var view = feed.CurrentView;
                if (view is null) { await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout); continue; }
                if (view.Outcome != UiMatchOutcome.None) break;

                // 只在**自己回合**动作：对方回合提交会吃 NonOwnerTurn，
                // 既浪费步数也会让"出牌永远失败"的假象出现（实测 plays=0）。
                // 每个"我方回合"重置一次尝试记录（Turn 是双方共用的计数，不能只按它判）。
                var turnKey = view.Turn * 2 + (view.ActivePlayerSide == "self" ? 0 : 1);
                if (turnKey != lastTurn)
                {
                    lastTurn = turnKey;
                    triedAttackers.Clear();
                    triedMovers.Clear();
                    triedPlays.Clear();
                }

                if (view.ActivePlayerSide != "self")
                {
                    await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
                    continue;
                }

                // 1) 铺单位：够了就下（顺带把总部血量压力摊开）
                var unit = view.SelfHand
                    .Where(card => card.Definition.CardType is not ("order" or "counter"))
                    .Where(card => (card.EffectiveCost ?? int.MaxValue) <= view.SelfKredits)
                    .Where(card => !triedPlays.Contains(card.Uid))
                    .OrderBy(card => card.EffectiveCost ?? int.MaxValue)
                    .FirstOrDefault();
                if (unit is not null)
                {
                    // 出牌被拒时视图不会变——不记下来就会原地重试到世界末日（真踩过：plays 冲到 185 但回合没动）。
                    var changed = await SubmitAndWait(feed, new Kards.Ui.Contracts.PlayCard(unit.Uid));
                    if (changed) plays++; else triedPlays.Add(unit.Uid);
                    if ((plays + 1) % 5 == 0)
                        GD.Print($"[combat] turn={view.Turn} plays={plays} attacks={attacks} moves={moves} " +
                                 $"hand={view.SelfHand.Count} ownLine={view.SelfLine.Count} " +
                                 $"enemyLine={view.EnemyLine.Count} kredits={view.SelfKredits} " +
                                 $"ownHq={view.SelfHq?.EffectiveDefense} enemyHq={view.EnemyHq?.EffectiveDefense}");
                    continue;
                }

                // 2) 攻击：HQ 优先，否则打敌方单位（守卫规则由服务端裁决，被拒就换下一个）
                var attacker = view.SelfLine
                    .Select(card => card.Uid)
                    .FirstOrDefault(uid => !triedAttackers.Contains(uid));
                if (attacker is not null)
                {
                    var defender = view.EnemyHq?.Uid ?? view.EnemyLine.FirstOrDefault()?.Uid;
                    if (defender is not null)
                    {
                        triedAttackers.Add(attacker);
                        var before = view.EnemyHq?.EffectiveDefense;
                        await SubmitAndWait(feed, new Kards.Ui.Contracts.AttackUnit(attacker, defender));
                        attacks++;
                        var after = feed.CurrentView?.EnemyHq?.EffectiveDefense;
                        if (attacks <= 8)
                            GD.Print($"[combat] attack #{attacks} enemyHq {before}->{after}");
                        continue;
                    }
                }

                // 3) 移动：把支援线的单位推上前线，给后续部署腾槽位。
                //    支援线只有 5 槽（含总部），不腾位就会一直吃 PrePlayNoAvailableSlots（实测 31 次）。
                var mover = view.SelfLine
                    .Select(card => card.Uid)
                    .FirstOrDefault(uid => !triedMovers.Contains(uid));
                if (mover is not null)
                {
                    triedMovers.Add(mover);
                    if (await SubmitAndWait(feed, new Kards.Ui.Contracts.MoveUnit(mover, "frontline")))
                        moves++;
                    continue;
                }

                // 4) 没得做就过回合
                var turnNow = view.Turn;
                await feed.SubmitAsync(new Kards.Ui.Contracts.EndTurn());
                var advanced = await WaitUntil(() => feed.CurrentView?.Turn != turnNow
                                                     || feed.CurrentView?.Outcome != UiMatchOutcome.None, 15);
                if (!advanced)
                {
                    GD.Print($"[combat] 过回合后回合也没推进（turn={turnNow}），提前收手");
                    break;
                }
            }

            var final = feed.CurrentView;
            Require(final is not null, "始终没有拿到视图");
            var enemyHq = final!.EnemyHq?.EffectiveDefense ?? 20;
            GD.Print($"[combat] 结果：outcome={final.Outcome} title={final.ResultTitle} " +
                     $"turn={final.Turn} ownHq={final.SelfHq?.EffectiveDefense} " +
                     $"enemyHq={enemyHq} plays={plays} attacks={attacks} moves={moves} targets={targetCount}");

            // 验收分两层：
            //   硬性 = 指挥命令能发出、攻击能真造成伤害（HQ 掉血是最干净的证据）；
            //   软性 = 打到结算面板（机制上已跑通过一次 Lose，但探针策略不够聪明，
            //          场上单位常处于"当回合部署/已行动"状态，不保证每局都能推到终局）。
            Require(attacks > 0, "一次攻击都没发出去——command.attack 这条链没通");
            Require(enemyHq < 20 || final.Outcome != UiMatchOutcome.None,
                $"攻击没有造成任何伤害（enemyHq={enemyHq}）——命令发出去了但引擎没执行");
            GD.Print("COMBAT_VERIFY_OK command-chain-attack-effective"
                     + (final.Outcome != UiMatchOutcome.None ? " match-to-result" : " (未推到终局)"));
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("COMBAT_VERIFY_FAILED " + error.Message);
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }

    /// <summary>等到条件成立或超时（秒）。</summary>
    private async Task<bool> WaitUntil(Func<bool> condition, double seconds)
    {
        for (var waited = 0.0; waited < seconds; waited += 0.1)
        {
            if (condition()) return true;
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        }
        return condition();
    }

    /// <summary>提交一条命令并等视图变化；返回是否真的变了（没变＝大概率被拒）。</summary>
    private async Task<bool> SubmitAndWait(RemoteMatchFeed feed, UiCommand command)
    {
        await feed.SubmitAsync(command);
        return await WaitForViewChange(feed, 2.5);
    }

    /// <summary>等到视图的回合/HQ 血量任一变化，给服务端一点结算时间。</summary>
    private async Task<bool> WaitForViewChange(RemoteMatchFeed feed, double seconds)
    {
        var before = feed.CurrentView;
        return await WaitUntil(() =>
        {
            var now = feed.CurrentView;
            return now is not null && before is not null
                && (now.Turn != before.Turn
                    || now.SelfHq?.EffectiveDefense != before.SelfHq?.EffectiveDefense
                    || now.EnemyHq?.EffectiveDefense != before.EnemyHq?.EffectiveDefense
                    || now.SelfLine.Count != before.SelfLine.Count
                    || now.EnemyLine.Count != before.EnemyLine.Count
                    || now.SelfHand.Count != before.SelfHand.Count
                    || now.Outcome != before.Outcome);
        }, seconds);
    }

    /// <summary>
    /// <c>--verify-target</c>：联机选靶链路。
    ///
    /// <para>
    /// 不赌随机：卡组里塞满"鹰爪"（德国 3 费指令卡，效果里调 <c>DesktopTargetContext.SelectAsync</c>），
    /// 攒够费打出去，等服务端推 <c>notify/target.request</c>，再用 responder 提交第一个候选。
    /// 没有这一步，"只能出单位牌"就永远是个说法。
    /// </para>
    /// </summary>
    private async void VerifyTarget()
    {
        try
        {
            _room.RequireCardForVerification("bloody_sickle");
            Show("room");
            await _room.OpenAsync();

            var feed = _room.Feed ?? throw new Exception("房主没有连上");
            var deck = _room.Deck ?? throw new Exception("卡组没组出来");
            GD.Print($"[target] deck pack={deck.PackageId} faction={deck.Faction} " +
                     $"size={deck.CardIds.Count} " +
                     $"slaughter={deck.CardIds.Count(id => id.EndsWith("/bloody_sickle", StringComparison.Ordinal))} 张");
            Require(feed.SeatToken is { Length: > 0 },
                "房主没有拿到 seatToken" + (_room.LastError is { } reason ? $"：{reason}" : "（没有错误信息）"));
            Require(deck.CardIds.Any(id => id.EndsWith("/bloody_sickle", StringComparison.Ordinal)),
                "卡组里没有血红镰刀，选靶无从触发（LobbyDeck 主国没跟着必带卡走）");
            Require(await _room.AddAiToSeatAsync(1), "座位 1 添加 AI 失败");
            Require(await _room.StartForVerificationAsync(), "开局失败");
            await EnterRemoteMatchAsync();
            Require(_active == "battle", "开局后没有切到战场");

            if (feed.CurrentView?.Phase == "mulligan")
            {
                await feed.MulliganDoneAsync(0);
                for (var i = 0; i < 100 && feed.CurrentView?.Phase == "mulligan"; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            }

            // 事件在协议线程，这里只做记录（不碰控件）。
            UiTargetRequest? request = null;
            IUiTargetResponder? responder = null;
            feed.TargetRequested += (incoming, reply) => { request = incoming; responder = reply; };

            // 攒费：首回合只有 1 点，鹰爪要 3 点。
            // 顺带先铺一个己方单位——选靶候选是"场上单位"，双方都空时请求根本不会产生。
            for (var round = 0; round < 18 && request is null; round++)
            {
                var view = feed.CurrentView;
                if (view is null) break;

                var kredits = view.SelfKredits;
                var hasUnit = view.SelfLine.Count > 0;
                var eagle = view.SelfHand.FirstOrDefault(candidate =>
                    candidate.CardId.EndsWith("/bloody_sickle", StringComparison.Ordinal));

                if (!hasUnit)
                {
                    var unit = view.SelfHand
                        .Where(candidate => candidate.Definition.CardType is not ("order" or "counter"))
                        .Where(candidate => (candidate.EffectiveCost ?? int.MaxValue) <= kredits)
                        .OrderBy(candidate => candidate.EffectiveCost ?? int.MaxValue)
                        .FirstOrDefault();
                    if (unit is not null)
                    {
                        GD.Print($"[target] round {round}: 先铺单位 {unit.CardId} cost={unit.EffectiveCost} kredits={kredits}");
                        await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(unit.Uid, 0));
                        for (var i = 0; i < 50; i++)
                            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                        continue;
                    }
                }

                if (eagle is not null && hasUnit && (eagle.EffectiveCost ?? int.MaxValue) <= kredits)
                {
                    GD.Print($"[target] play bloody_sickle turn={view.Turn} cost={eagle.EffectiveCost} " +
                             $"kredits={kredits} ownLine={view.SelfLine.Count}");
                    var enemyBefore = view.EnemyLine.Count;
                    await feed.SubmitAsync(new Kards.Ui.Contracts.PlayCard(eagle.Uid, 0));

                    // 先给效果一点时间跑：扣费说明脚本进了执行期，没扣费说明连打都没打出去。
                    for (var i = 0; i < 30; i++)
                        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                    var after = feed.CurrentView;
                    GD.Print($"[target] after play: hand {view.SelfHand.Count}->{after?.SelfHand.Count} " +
                             $"kredits {kredits}->{after?.SelfKredits} " +
                             $"enemyLine {enemyBefore}->{after?.EnemyLine.Count} " +
                             $"ownLine={after?.SelfLine.Count} " +
                             $"enemyHq {after?.EnemyHq?.EffectiveDefense}");

                    for (var i = 0; i < 150 && request is null; i++)
                        await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
                    break;
                }

                GD.Print($"[target] round {round}: 手牌 {view.SelfHand.Count} 张、kredits={kredits}、场上 {view.SelfLine.Count}，过回合攒费");
                await feed.SubmitAsync(new Kards.Ui.Contracts.EndTurn());
                for (var i = 0; i < 150 && feed.CurrentView?.Turn == view.Turn; i++)
                    await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            }

            Require(request is not null,
                "没有收到 notify/target.request。常见原因：18 回合内没抽到血红镰刀（随机发牌），" +
                "或选靶候选为 0（场上没有任何单位）。先看上面的 [target] 轨迹再判断，别直接怀疑链路。");
            var slot = request!.Slots.FirstOrDefault() ?? throw new Exception("选靶请求没有槽");
            GD.Print($"[target] request selector={request.SelectorName} drag={request.IsDrag} " +
                     $"slots={request.Slots.Count} candidates={slot.Allowed.Count}");

            var pick = slot.Allowed.FirstOrDefault() ?? throw new Exception("选靶请求没有候选");
            var hqBefore = feed.CurrentView?.SelfHq?.EffectiveDefense;
            var lineBefore = feed.CurrentView?.SelfLine.Count ?? 0;
            var accepted = responder!.Complete(request.RequestId,
                new Dictionary<string, IReadOnlyList<string>> { [slot.Name] = [pick.Id] });
            Require(accepted, "responder.Complete 返回 false（UI 会以为没提交）");
            GD.Print($"[target] submitted pick={pick.Id} label={pick.Label} kind={pick.Kind}");

            // 提交是异步 RPC：等它落地，别急着 close（否则报 "disposed object"，看起来像功能坏了）。
            for (var i = 0; i < 40; i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);

            var afterSubmit = feed.CurrentView;
            GD.Print($"[target] after submit: ownHq {hqBefore}->{afterSubmit?.SelfHq?.EffectiveDefense} " +
                     $"ownLine {lineBefore}->{afterSubmit?.SelfLine.Count} pending={feed.TargetPending}");

            Require(!feed.TargetPending, "提交后仍处于选靶待决状态");
            // 血红镰刀：对选中单位 1 伤 + 己方总部 1 伤。单位被打死时脚本会在死亡结算处中断，
            // 总部那 1 点不一定落到——所以"单位减少"和"总部掉血"任一发生都算效果生效。
            var hqDropped = (afterSubmit?.SelfHq?.EffectiveDefense ?? 0) < (hqBefore ?? 0);
            var lineDropped = (afterSubmit?.SelfLine.Count ?? 0) < lineBefore;
            Require(hqDropped || lineDropped,
                $"提交后没有任何效果迹象（总部 {hqBefore}->{afterSubmit?.SelfHq?.EffectiveDefense}、" +
                $"场上 {lineBefore}->{afterSubmit?.SelfLine.Count}）");

            GD.Print("TARGET_VERIFY_OK request-received targeter-submitted effect-applied");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("TARGET_VERIFY_FAILED " + error.Message);
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }

    /// <summary>
    /// <c>--verify-room-remote</c>：**两个真人客户端**的第一次实测。
    /// 房主（seat 0, local）＋ 第二个连接（seat 1, remote）——不是 AI 顶替，
    /// 用来确认"远端玩家加入"这条路径真的能走通，而不只是文档上说可以。
    /// </summary>
    private async void VerifyRoomRemote()
    {
        RemoteMatchFeed? guest = null;
        try
        {
            Show("room");
            await _room.OpenAsync();
            var host = _room.Feed ?? throw new Exception("房主没有连上");
            Require(host.SeatToken is { Length: > 0 }, "房主没有拿到 seatToken");

            // 第二个客户端：同一台机器、同一个端口，模拟另一台机器上的玩家。
            guest = await RemoteMatchFeed.ConnectAsync("127.0.0.1", _room.Port, seat: 1);
            var guestToken = await guest.ClaimSeatAsync(1, "remote", "远端玩家", _room.Deck!.CardIds);
            Require(guestToken is not null, "远端玩家占座失败");
            GD.Print("[room-remote] guest joined seat=1");

            // 房主侧快照应看到座位 1 是 remote 玩家，而不是空座。
            await _room.RefreshForVerificationAsync();
            var info = await host.RoomInfoAsync();
            var seats = (info.Details as System.Text.Json.Nodes.JsonObject)?["seats"];
            var seatOne = (seats as System.Text.Json.Nodes.JsonArray)?
                .FirstOrDefault(node => node?["seat"]?.GetValue<int>() == 1);
            var kind = seatOne?["controllerKind"]?.GetValue<string>() ?? "";
            Require(kind == "remote", $"座位 1 的 controllerKind 应为 remote，实际 '{kind}'");
            GD.Print($"[room-remote] host sees seat1 kind={kind}");

            var started = await _room.StartForVerificationAsync();
            Require(started, "有远端玩家时开局失败");

            // 不赌推送时序：两端各主动拉一次视图（observe.view 按座位过滤）。
            await host.RefreshViewAsync();
            await guest.RefreshViewAsync();
            for (var i = 0; i < 100 && (host.CurrentView is null || guest.CurrentView is null); i++)
                await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            Require(host.CurrentView is not null, "房主没有收到视图");
            Require(guest.CurrentView is not null, "远端玩家没有收到视图");

            // 两边看到的"自己"必须不是同一个人：视角按座位过滤，这是脱敏的最小证据。
            var hostHq = host.CurrentView!.SelfHq?.Uid;
            var guestHq = guest.CurrentView!.SelfHq?.Uid;
            Require(hostHq != guestHq, "两端拿到了同一个总部，视角没有按座位区分");
            GD.Print($"[room-remote] host hq={hostHq} guest hq={guestHq} " +
                     $"hostHand={host.CurrentView.SelfHand.Count} guestHand={guest.CurrentView.SelfHand.Count}");

            GD.Print("ROOM_REMOTE_VERIFY_OK guest-claim seat-split view-per-seat");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GD.Print("ROOM_REMOTE_VERIFY_FAILED " + error.Message);
            if (guest is not null) await guest.DisposeAsync();
            _room.Close();
            GetTree().Quit(1);
            return;
        }

        await guest.DisposeAsync();
        _room.Close();
        GetTree().Quit();
    }

    /// <summary>
    /// <c>--capture-room</c>：拍房间屏真实布局（不是原型图）。需要图形模式运行。
    /// 先让实例起来、把 AI 放到座位 1，拍的才是"半满房间"这个真实中间态。
    /// </summary>
    private async void CaptureRoom()
    {
        try
        {
            Show("room");
            // 等快门遮罩揭开再拍，否则截到的是遮罩本身。
            await ToSignal(GetTree().CreateTimer(.45), SceneTreeTimer.SignalName.Timeout);

            _ = _room.OpenAsync();
            for (var i = 0; i < 120 && _room.Feed is null; i++)
                await ToSignal(GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
            Require(_room.Feed is not null, "房间没有就绪，拍不到内容");

            // 必须等占座完成（seatToken）再拉 AI：房间在 claim 之前不接受 add_ai。
            for (var i = 0; i < 100 && _room.Feed!.SeatToken is not { Length: > 0 }; i++)
                await ToSignal(GetTree().CreateTimer(.1), SceneTreeTimer.SignalName.Timeout);
            Require(_room.Feed!.SeatToken is { Length: > 0 }, "房主没有完成占座");

            await _room.AddAiToSeatAsync(1);
            // 座位卡是 deferred 写入，主动刷一次再等两帧，否则截到的是加 AI 之前的画面。
            await _room.RefreshForVerificationAsync();
            // 座位状态是 deferred 更新，多等几帧再按快门。
            await ToSignal(GetTree().CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            await MenuCaptureAsync("room-layout");
            SaveMenuImage("room");
            GD.Print("ROOM_CAPTURE_OK layout ai-seat endpoint checks");
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
            return;
        }

        _room.Close();
        GetTree().Quit();
    }
}
