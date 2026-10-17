/* ==========================================================================
 * KG Effects —— 卡牌效果 DSL 执行器
 * 效果结构：
 * {
 *   trigger: 'order'|'deploy'|'mobilize'|'death'|'turnStart'|'turnEnd'|'attack'|'attacked'|'damaged'|'passive'|'activated',
 *   targets: [ {id:'t', side:'enemy'|'friendly'|'any', kind:'unit'|'hq'|'any', filter:{...}, optional:bool, prompt:string} ],
 *   vars:    { x: <numExpr>, y: <numExpr> },
 *   condition: <cond>,
 *   actions: [ <action> ],
 *   else:    [ <action> ],
 *   choices: [ {label, actions} ]        // 抉择
 * }
 * action: { op:'damage', target:'t'|{sel:...}, amount:<numExpr>, ... }
 * ========================================================================== */
(function (global) {
  'use strict';
  const KG = global.KG = global.KG || {};
  const FX = KG.effects = KG.effects || {};

  /* --------------------------------------------------------------- 数值求值 */
  function num(v, state, ctx) {
    if (v && v.expr && FX.composition) return FX.composition.value(v, state, ctx);
    if (v == null) return 0;
    if (typeof v === 'number') return v;
    if (typeof v === 'string') {
      if (ctx && ctx.vars && ctx.vars[v] != null) return ctx.vars[v];
      const n = parseInt(v, 10);
      return isNaN(n) ? 0 : n;
    }
    if (typeof v === 'object') {
      // ★ 「上一回合被消灭的单位数量」（2026-09-22）——与具体单位无关的全局统计，
      //   由 engine 的 beginTurn 在 turnStart 之前快照（unitsLostLastTurn）。
      //   放在这里（unit 上下文之外）是因为 turnStart 这类事件没有"单位"主角。
      if (v.stat === 'lastTurnKilled') {
        const who = (v.of === 'enemy') ? 1 - (ctx.owner || 0) : (ctx.owner || 0);
        const pp = state.players[who] || {};
        return pp.unitsLostLastTurn || 0;
      }
      if (v.var) return (ctx.vars && ctx.vars[v.var]) || 0;
      /* ★ 卡牌变量的数值（Alan 09-26）：asVar{from:'card'} 存的是手牌实例（或 {id} 包装），
       *   取最近一张按 stat 查卡定义：cost / attack / defense。
       *   例：「抽一张牌记为 v1」+「造成等同于其费用的伤害」= amount:{cardVar:'v1',stat:'cost'} */
      if (v.cardVar != null) {
        const ent0 = ctx.vars ? ctx.vars[v.cardVar] : null;
        const ent = Array.isArray(ent0) ? ent0[ent0.length - 1] : ent0;
        const cid = ent && typeof ent === 'object' ? ent.id : ent;
        const cd = (cid != null && KG.cardDef) ? KG.cardDef(state, cid) : null;
        const st = v.stat || 'cost';
        /* ★ 实例实时值（Alan 09-28「卡牌数值和词条都能引用」）：变量存的是**场上单位实例**
         *   （asVar{from:'field'|'summoned'}）时，读它**当前**的攻防 / 装甲，而不是卡面原值。
         *   ⚠ 手牌 / 卡组实例只有 id（没有 attack/defense）→ 不进这个分支，自动退回卡定义值。
         *   判据必须带 attack/defense 存在性：手牌实例也有 uid，光看 uid 会误判成单位。 */
        if (ent && typeof ent === 'object' && ent.uid != null && (ent.attack != null || ent.defense != null)) {
          if (st === 'curAttack') return ent.attack || 0;
          if (st === 'curDefense') return ent.defense || 0;
          if (st === 'curMaxDefense') return ent.maxDefense != null ? ent.maxDefense : (ent.defense || 0);
          if (st === 'curMissing') return (ent.maxDefense || 0) - (ent.defense || 0);
          if (st === 'curArmor') return KG.armorOf ? KG.armorOf(ent) : 0;
        }
        if (!cd) return 0;
        if (st === 'cost') return cd.cost || 0;
        if (st === 'attack') return cd.attack || 0;
        if (st === 'defense') return cd.defense != null ? cd.defense : 0;
        if (st === 'opCost') return cd.opCost || 0;
        /* ★ 词条（Alan 09-28）：keywordCount = 这张卡有几个词条；
         *   hasKeyword = 有没有指定词条（1 / 0，词条名写在 v.kw —— 例「如果它有闪击」）。 */
        if (st === 'keywordCount') return (cd.keywords || []).length;
        if (st === 'hasKeyword') {
          const kw = v.kw || v.keyword;
          if (!kw) return 0;
          if (cd.kwMap && cd.kwMap[kw]) return 1;
          return (cd.keywords || []).indexOf(kw) >= 0 ? 1 : 0;
        }
        return 0;
      }
      // ★ 牌Q 卡包（2026-09-25）动态值：最近一次打出的对战伤害 / 本次效果洗入的数量
      if (v.stat === 'lastCombatDamage') return state.lastCombatDamage || 0;
      if (v.stat === 'shuffledCount') return (ctx && ctx.shuffledCount) || 0;
      if (v.stat === 'shuffledNavy') return (ctx && ctx.shuffledNavy) || 0;
      // ★ 牌Q 卡包（2026-09-25）四种动态数值
      if (v.rand) {
        const lo = num(v.rand[0], state, ctx), hi = num(v.rand[1], state, ctx);
        const a2 = Math.min(lo, hi), b2 = Math.max(lo, hi);
        return a2 + Math.floor((state.rng ? state.rng() : Math.random()) * (b2 - a2 + 1));
      }
      if (v.maxStat) {
        const mSpec = v.maxStat.spec || {};
        const st = v.maxStat.stat || 'attack';
        const us = selectUnits(state, ctx, Object.assign({ sel: 'all' }, mSpec));
        return us.reduce(function (mx, u) { return Math.max(mx, (u && u[st]) || 0); }, 0);
      }
      if (v.playedCount) {
        const who = (v.of === 'enemy') ? 1 - (ctx.owner || 0) : (ctx.owner || 0);
        const pp0 = state.players[who] || {};
        const key = (v.playedCount && v.playedCount.name) || v.playedCount;
        return (pp0.playedCount && pp0.playedCount[key]) || 0;
      }
      if (v.count != null) {
        const spec = v.count;
        const n = selectUnits(state, ctx, Object.assign({ sel: 'all' }, spec)).length;
        if (v.plus != null) return n + num(v.plus, state, ctx);
        // ★ 「每有…」的 N×数量：`{count:spec, times:N}` = 符合条件的单位数 × N。
        //   只加数量是不够的 —— 「每有一个友方太空单位，造成2点伤害」要的是 2×个数。
        return v.times != null ? n * num(v.times, state, ctx) : n;
      }
      if (v.stat) {
        const u = refUnit(state, ctx, v.of || 'self');
        if (v.stat === 'eventCardCost') {
          // 本次事件所用卡牌的花费（例如"打出一张十费指令 → 获得10防御力"）
          const ev = ctx.event || {};
          const cid = ev.cardId || (ev.card && ev.card.id) || (ev.source && ev.source.def && ev.source.def.id);
          const cd = cid ? KG.cardDef(state, cid) : null;
          return cd ? (cd.cost || 0) : 0;
        }
        if (v.stat === 'cost') {
          // 单位的花费（原始卡面花费）
          if (u) return (u.def && u.def.cost) || 0;
          const t = (ctx.targets && (ctx.targets.t || ctx.targets.target)) || null;
          const one = Array.isArray(t) ? t[0] : t;
          if (one && one.value && typeof one.value === 'string') {
            const d = KG.cardDef(state, one.value);
            if (d) return d.cost || 0;
          }
          return 0;
        }
        if (!u) {
          // 没有单位上下文时仍需支持"与单位无关"的动态值（刚弃掉的牌）
          if (v.stat === 'lastDiscardedDefense') return (ctx.vars && ctx.vars.lastDiscardedDefense) || state.__lastDiscardedDefense || 0;
          return 0;
        }
        if (v.stat === 'attack') return u.attack;
        if (v.stat === 'defense') return u.defense;
        if (v.stat === 'maxDefense') return u.maxDefense;
        if (v.stat === 'missing') return u.maxDefense - u.defense;
        if (v.stat === 'armor') return KG.armorOf(u);
        if (v.stat === 'opCost') return u.def.opCost || 0;
        if (v.stat === 'lastDiscardedDefense') {
          // 「获得等同于其防御力的指挥点」——"其"指刚被弃掉的那张牌
          return (ctx.vars && ctx.vars.lastDiscardedDefense) || state.__lastDiscardedDefense || 0;
        }
      }
      if (v.hand != null) return state.players[sideIdx(state, ctx, v.hand)].hand.length;
      // 「对敌方总部造成等同于敌方明牌数的伤害」——明牌 = 手牌里 revealed 的牌（2026-09-24）
      if (v.revealedCount != null) return state.players[sideIdx(state, ctx, v.revealedCount)].hand.filter(function (h) { return h.revealed; }).length;
      if (v.deck != null) return state.players[sideIdx(state, ctx, v.deck)].deck.length;
      if (v.supportFree != null) { const p2 = state.players[sideIdx(state, ctx, v.supportFree)]; return Math.max(0, KG.RULES.supportMax - p2.support.length); }
      if (v.frontlineFree != null) { const i2 = sideIdx(state, ctx, v.frontlineFree); return Math.max(0, KG.effectiveFrontlineMax(state, i2) - KG.frontlineOf(state, i2).length); }
      if (v.lastAdded != null) return ((state.__lastAdded && state.__lastAdded[ctx.owner]) || []).length;
      if (v.hq != null) {
        const p = state.players[sideIdx(state, ctx, v.hq)];
        return v.mode === 'missing' ? (p.hqMax - p.hq) : p.hq;
      }
      if (v.turn === true) return state.turn;
      if (v.kredits != null) return state.players[sideIdx(state, ctx, v.kredits)].kredits;
      if (v.sum) return (v.sum.items || []).reduce(function (a, b) { return a + num(b, state, ctx); }, 0);
      if (v.mul) return (v.mul.items || []).reduce(function (a, b) { return a * num(b, state, ctx); }, 1);
      if (v.min) return Math.min.apply(null, (v.min.items || []).map(function (b) { return num(b, state, ctx); }));
      if (v.max) return Math.max.apply(null, (v.max.items || []).map(function (b) { return num(b, state, ctx); }));
    }
    return 0;
  }
  FX.num = num;

  function sideIdx(state, ctx, side) {
    if (side === 'self' || side === 'friendly') return ctx.owner;
    if (side === 'enemy' || side === 'opponent') return 1 - ctx.owner;
    if (typeof side === 'number') return side;
    return ctx.owner;
  }
  FX.sideIdx = sideIdx;

  function refUnit(state, ctx, ref) {
    if (!ref) return ctx.unit || null;
    if (typeof ref === 'object' && ref.uid) return KG.unitByUid(state, ref.uid);
    if (ref === 'self' || ref === 'source') return ctx.unit || ctx.source || null;
    if (ref === 'eventUnit' || ref === 'event' || ref === 'dead') {
      const ev = ctx.event;
      if (!ev) return null;
      const cand = ev.deadUnit || ev.unit || (ev.source && ev.source.uid ? ev.source : null);
      if (cand && cand.uid) return KG.unitByUid(state, cand.uid) || cand;
      return null;
    }
    // 「友方单位受到伤害时」这类事件里 source 是伤害来源、victim 才是被伤害的那个单位
    // ★ 牌Q 卡包：「指向一个单位」（rememberTarget 记在施法单位身上）→ 之后用 remembered 引用
    if (ref === 'remembered') {
      const u = ctx.unit || ctx.source;
      const r = u && u.remembered;
      if (!r || !r.uid) return null;
      return KG.unitByUid(state, r.uid) || null;
    }
    if (ref === 'eventVictim' || ref === 'victim') {
      const ev = ctx.event || {};
      const cand = ev.victim || ev.deadUnit || ev.unit || ev.source;
      if (cand && cand.uid) return KG.unitByUid(state, cand.uid) || cand;
      return null;
    }
    // 事件主角优先；没有事件时退回效果来源自身（"使其获得…"用在部署/动员效果里指的就是自己）
    if (ref === 'eventOrSelf') {
      const ev = ctx.event || {};
      const cand = ev.deadUnit || ev.unit || ev.victim || (ev.source && ev.source.uid ? ev.source : null);
      if (cand && cand.uid) return KG.unitByUid(state, cand.uid) || cand;
      return ctx.unit || ctx.source || null;
    }
    /* ★「作为变量」：ctx.vars 里的具名单位（asVar 写入）。
     *  取值时再 unitByUid 一次，避免单位已死/换形态拿到旧对象。数值变量（setVar）不满足 u.uid → 自然跳过。 */
    const namedVar = ctx && ctx.vars && ctx.vars[ref];
    if (namedVar) {
      const nv = Array.isArray(namedVar) ? namedVar[0] : namedVar;
      if (nv && nv.uid) return KG.unitByUid(state, nv.uid) || nv;
    }
    if (ref === 'defender' || ref === 'attackTarget') {
      const ev = ctx.event;
      const d = ev && ev.defender;
      if (d && d.uid) return KG.unitByUid(state, d.uid) || d;
      return null;
    }
    // 刚刚召唤出来的单位（例如 zbk1母舰"获得其中一个的攻击力"）
    if (ref === 'summoned' || ref === 'lastSummoned') {
      const list = ctx.summoned || [];
      return list.length ? (KG.unitByUid(state, list[0].uid) || list[0]) : null;
    }
    if (ref === 'attacker') {
      const ev = ctx.event;
      const a = ev && ev.source;
      if (a && a.uid) return KG.unitByUid(state, a.uid) || a;
      return null;
    }
    if (ref === 'target') {
      const t = (ctx.targets && (ctx.targets.t || ctx.targets.target)) || ctx.chosenTarget;
      if (Array.isArray(t)) return t[0] && t[0].unit ? t[0].unit : KG.unitByUid(state, t[0]);
      if (t && t.unit) return t.unit;
      if (typeof t === 'string') return KG.unitByUid(state, t);
    }
    if (ctx.targets && ctx.targets[ref]) {
      const t = ctx.targets[ref];
      if (Array.isArray(t)) return t[0] && (t[0].unit || KG.unitByUid(state, t[0].value || t[0]));
      if (t.unit) return t.unit;
      return KG.unitByUid(state, t.value || t);
    }
    return null;
  }
  FX.refUnit = refUnit;

  /* --------------------------------------------------------------- 单位选择 */
  // spec: {sel:'all'|'random'|'choose'|'self'|'ref', side, zone, filter, count, optional, prompt, excludeSelf}
  function selectUnits(state, ctx, spec) {
    if (!spec) return [];
    const prevCtx = state.__ctxUnit, prevComposition = state.__compositionCtx;
    state.__compositionCtx = ctx;
    state.__ctxUnit = (ctx && (ctx.unit || ctx.source)) || null;
    try {
      return selectUnitsInner(state, ctx, spec).filter(function (u) {
        // 引用和群体选择同样遵守隐蔽免疫；本单位自己的能力仍可生效。
        const source = ctx && (ctx.unit || ctx.source);
        return !KG.hasKw(u, 'conceal') || !(ctx.fromOrder || ctx.counter || (source && source.uid && source.uid !== u.uid));
      });
    } finally {
      state.__ctxUnit = prevCtx; state.__compositionCtx = prevComposition;
    }
  }
  function selectUnitsInner(state, ctx, spec) {
    if (!spec) return [];
    if (typeof spec === 'string') {
      const u = refUnit(state, ctx, spec);
      return u ? [u] : [];
    }
    if (spec.sel === 'self') { const u = refUnit(state, ctx, 'self'); return u ? [u] : []; }
    if (spec.sel === 'ref') { const u = refUnit(state, ctx, spec.ref || 'target'); return u ? [u] : []; }
    // 「作为变量」存了一组单位时，用它做全体操作（{sel:refAll, ref:v1}）
    if (spec.sel === 'refAll') {
      const v = (ctx.vars || {})[spec.ref];
      if (!Array.isArray(v)) return [];
      return v.map(function (x) { return (x && x.uid) ? (KG.unitByUid(state, x.uid) || x) : null; }).filter(function (u) { return u && !u.dead; });
    }
    const zones = spec.zone === 'frontline' ? ['frontline'] : spec.zone === 'support' ? ['support'] : ['support', 'frontline'];
    let sidx = spec.side === 'enemy' ? [1 - ctx.owner]
      : spec.side === 'friendly' ? [ctx.owner]
        : spec.side === 'both' || spec.side === 'any' ? [ctx.owner, 1 - ctx.owner]
          : [ctx.owner];
    let out = [];
    const unitSource = !!(ctx.unit && ctx.unit.uid);
    const fromOrder = !!ctx.fromOrder;
    sidx.forEach(function (oi) {
      zones.forEach(function (z) {
        KG.unitsInZone(state, oi, z).forEach(function (u) {
          if (u.dead) return;
          if (KG.hasKw(u, 'conceal') && (fromOrder || ctx.counter || (unitSource && ctx.unit.uid !== u.uid))) return;
          if (spec.excludeSelf && ctx.unit && u.uid === ctx.unit.uid) return;
          if (!KG.matchFilter(state, u, spec.filter, ctx.owner)) return;
          if (spec.where && FX.composition && !FX.composition.test(spec.where, state, ctx, u)) return;
          if (oi !== ctx.owner && u.mods) {
            // 无视指令：连群体指令也不受影响（"无法被指令指向"只挡指定目标，不挡群体）
            if (fromOrder && (u.mods.ignoreOrders || u.mods.immuneOrders)) return;
            if (unitSource && (u.mods.immuneUnitEffects || u.mods.ignoreEnemyEffects)) return;
          }
          out.push(u);
        });
      });
    });
    if (spec.sel === 'random') {
      out = shuffle(state, out).slice(0, spec.count || 1);
    } else if (spec.count != null && spec.sel !== 'all') {
      out = out.slice(0, spec.count);
    }
    return out;
  }
  FX.selectUnits = selectUnits;

  function shuffle(state, arr) {
    const a = arr.slice();
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(state.rng() * (i + 1));
      const t = a[i]; a[i] = a[j]; a[j] = t;
    }
    return a;
  }
  FX.shuffle = shuffle;

  function allUnits(state, side, ctx) {
    const idxs = side === 'both' ? [0, 1] : [sideIdx(state, ctx, side)];
    let out = [];
    idxs.forEach(function (i) { out = out.concat(KG.allUnitsOf(state, i)); });
    return out;
  }
  FX.allUnits = allUnits;

  /* ------------------------------------------------------------ 目标预选择 */
  // 打出卡牌前，把效果里声明为 choose 的目标解析为具体对象（ctx.targets）
  FX.resolveDeclaredTargets = async function (state, ctx, effects, chooser) {
    ctx.targets = ctx.targets || {};
    for (const ef of effects) {
      if (ef.condition && !evalCond(state, ctx, ef.condition)) continue; // 条件不满足时不询问目标
      const specs = ef.targets || [];
      for (const spec0 of specs) {
        if (ctx.targets[spec0.id]) continue;
        const spec = Object.assign({}, spec0);
        // 让"无法被指令指向 / 无视指令"能被正确排除
        spec.fromOrder = !!ctx.fromOrder;
        spec.unitSource = !!(ctx.unit && ctx.unit.uid);
        const before = state.__compositionCtx; let opts;
        try { state.__compositionCtx = ctx; opts = KG.enumerateTargets(state, ctx.owner, spec); }
        finally { state.__compositionCtx = before; }
        if (opts.length === 0) {
          if (spec.optional) { ctx.targets[spec.id] = []; continue; }
          ctx.targets[spec.id] = [];
          continue;
        }
        const need = spec.count || 1;
        if (need === 1 && spec.mode !== 'multi') {
          const pick = await KG.ask(chooser, {
            kind: 'target', prompt: spec.prompt || '选择一个目标', options: opts,
            spec: spec, state: state,
          });
          const chosen = opts.find(function (o) { return o.value === pick || o === pick; }) || opts[0];
          ctx.targets[spec.id] = [chosen];
        } else {
          const picks = [];
          let pool = opts.slice();
          for (let i = 0; i < need && pool.length; i++) {
            const pick = await KG.ask(chooser, {
              kind: 'target', prompt: (spec.prompt || '选择一个目标') + '（' + (i + 1) + '/' + need + '）',
              options: pool, spec: spec, state: state,
            });
            const chosen = pool.find(function (o) { return o.value === pick || o === pick; }) || pool[0];
            // ★ 牌Q：「本单位被**指向**时…」→ 目标被选中的那一刻广播 targeted
            const cu = chosen && chosen.unit ? chosen.unit : (chosen && chosen.uid ? KG.unitByUid(state, chosen.uid) : null);
            if (cu && !cu.dead && typeof runTrigger === 'function') runTrigger(state, { trigger: 'targeted', owner: cu.owner, victim: cu, unit: cu, source: ctx.unit || null });
            picks.push(chosen);
            pool = pool.filter(function (o) { return o !== chosen; });
          }
          ctx.targets[spec.id] = picks;
        }
      }
    }
    if (ctx.targets.t && ctx.targets.t.length) {
      ctx.chosenTarget = ctx.targets.t[0];
      ctx.targetUnit = ctx.targets.t[0].unit || null;
    }
    // ★ 快照：把每个声明目标"在效果**开始前**是否已被压制"记下来。
    //   「压制一个敌方单位，如果其已被压制，随机消灭一个敌方单位」——如果条件在本效果
    //   的 pin 之后求值，就会永远为真（自己刚刚压制了它）。所以必须用**效果前**的快照。
    const snap = {};
    Object.keys(ctx.targets).forEach(function (k) {
      if (k === '__pinnedBefore') return;
      const ref = ctx.targets[k];
      const one = Array.isArray(ref) ? ref[0] : ref;
      const uid = (one && one.unit) ? one.unit.uid : (one && one.uid) || (typeof one === 'string' ? one : null);
      const u = uid ? KG.unitByUid(state, uid) : null;
      snap[k] = !!(u && !u.dead && ((u.pinnedTurns > 0) || u.kws.pin));
    });
    ctx.targets.__pinnedBefore = snap;
    return ctx.targets;
  };

  /* ============================================================ 条件原语注册表
   * 设计原则：**能拆的拆成原语组合，拆不了的才做成一个独立原语。**
   *   - 每个条件原语是一个纯函数 (state, ctx, c) => boolean，登记在 CONDS 里。
   *   - 需要"比较"的条件一律使用通用比较原语 compare（c.cmp + c.value），
   *     不再各自硬编码比较方向 —— 这是过去 kreditsAtLeast 只能写 ≥
   *     而导致 cmp:'==' 被静默忽略的根因。
   *   - 复合条件（and / or / not）是**组合器**，把子条件接起来。
   *   - 未登记的条件名 → 记一条明确错误日志并放行，不再静默 return true。
   * 审计：tools/fxcheck.js 的「条件 op 自检」会遍历数据里用到的每个条件名，
   *       要求它必须登记在 CONDS 里。
   * ======================================================================== */
  const CONDS = FX.CONDS = {};

  function cmp(a, op, b) {
    switch (op) {
      case '>=': return a >= b;
      case '>': return a > b;
      case '<=': return a <= b;
      case '<': return a < b;
      case '==': case '=': return a === b;
      case '!=': return a !== b;
      default: return a >= b;
    }
  }
  FX.cmp = cmp;

  // ── 组合器：把子条件接起来 ──────────────────────────────────────────
  CONDS.and = (state, ctx, c) => (c.items || []).every(x => evalCond(state, ctx, x));
  CONDS.or = (state, ctx, c) => (c.items || []).some(x => evalCond(state, ctx, x));
  CONDS.not = (state, ctx, c) => !evalCond(state, ctx, c.item || (c.items || [])[0]);
  // 「两个值比较」：通用比较原语。c.left 是取值原语名（见 VALS），c.value/c.right 是右值。
  CONDS.compare = (state, ctx, c) => cmp(num(c.left, state, ctx), c.cmp || '>=', num(c.right != null ? c.right : c.value, state, ctx));

  // ── 资源类 ────────────────────────────────────────────────────────
  // 「如果剩余指挥点 X」——cmp 现在是**真的**被读取了（过去只认 ≥）
  CONDS.kreditsAtLeast = (state, ctx, c) =>
    cmp(state.players[sideIdx(state, ctx, c.side || 'self')].kredits, c.cmp || '>=', num(c.value, state, ctx));
  CONDS.kreditsAtMost = (state, ctx, c) =>
    cmp(state.players[sideIdx(state, ctx, c.side || 'self')].kredits, c.cmp || '<=', num(c.value, state, ctx));
  CONDS.handSize = (state, ctx, c) =>
    cmp(state.players[sideIdx(state, ctx, c.side || 'self')].hand.length, c.cmp || '>=', num(c.value, state, ctx));
  CONDS.deckSize = (state, ctx, c) =>
    cmp(state.players[sideIdx(state, ctx, c.side || 'self')].deck.length, c.cmp || '>=', num(c.value, state, ctx));
  CONDS.turnAtLeast = (state, ctx, c) => cmp(state.turn, c.cmp || '>=', num(c.value, state, ctx));
  CONDS.var = (state, ctx, c) => cmp((ctx.vars || {})[c.name] || 0, c.cmp || '>=', num(c.value, state, ctx));
  /* ★「变量那张卡有没有某词条」（Alan 09-28「卡牌数值和词条都能被引用」）：
   *   例：抽到的牌记为 v1 → 「如果 v1 有闪击」= { op:'cardVarHasKeyword', name:'v1', keyword:'blitz' }。
   *   数值端走 num({cardVar,stat:'hasKeyword'})，这里是**条件**端（能直接挂「如果…」）。
   *   变量是场上单位实例时，单位的临时词条（被授予的）也算 —— 卡定义 + 实例两边都查。 */
  CONDS.cardVarHasKeyword = function (state, ctx, c) {
    const ent0 = (ctx.vars || {})[c.name || 'v1'];
    const ent = Array.isArray(ent0) ? ent0[ent0.length - 1] : ent0;
    const cid = ent && typeof ent === 'object' ? ent.id : ent;
    const cd = (cid != null && KG.cardDef) ? KG.cardDef(state, cid) : null;
    const kw = c.keyword || c.kw;
    let has = false;
    if (kw && cd) {
      has = !!(cd.kwMap && cd.kwMap[kw]) || (cd.keywords || []).indexOf(kw) >= 0;
    }
    if (kw && ent && typeof ent === 'object' && Array.isArray(ent.keywords)) {
      has = has || ent.keywords.indexOf(kw) >= 0;
    }
    return c.negate ? !has : has;
  };

  // ── 总部 / 前线 ───────────────────────────────────────────────────
  CONDS.hqBelow = (state, ctx, c) => cmp(state.players[sideIdx(state, ctx, c.side || 'enemy')].hq, '<', num(c.value, state, ctx));
  CONDS.hqAbove = (state, ctx, c) => cmp(state.players[sideIdx(state, ctx, c.side || 'enemy')].hq, '>', num(c.value, state, ctx));
  CONDS.controlFrontline = (state, ctx, c) => KG.controlsFrontline(state, sideIdx(state, ctx, c.side || 'self'));
  CONDS.frontlineControl = (state, ctx, c) => KG.frontlineControl(state, ctx.owner) === (c.value == null ? 1 : num(c.value, state, ctx));
  CONDS.frontlineEmpty = (state, ctx, c) => KG.frontlineOf(state, sideIdx(state, ctx, c.side || 'enemy')).length === 0;
  // 「敌方占着前线 或 我的前线已满」（探索型方针）
  CONDS.frontlineEnemyOrFull = (state, ctx) => {
    const enemyHolds = KG.frontlineOf(state, 1 - ctx.owner).length > 0;
    const mineFull = KG.frontlineOf(state, ctx.owner).length >= KG.effectiveFrontlineMax(state, ctx.owner);
    return enemyHolds || mineFull;
  };
  // 「前线/支援阵线还有空位」
  CONDS.hasRoom = (state, ctx, c) => {
    const i = sideIdx(state, ctx, c.side || 'self');
    if (c.zone === 'frontline') return KG.frontlineOf(state, i).length < KG.effectiveFrontlineMax(state, i);
    return state.players[i].support.length < KG.RULES.supportMax;
  };

  // ── 单位存在性 / 属性 ─────────────────────────────────────────────
  CONDS.unitCount = (state, ctx, c) =>
    cmp(selectUnits(state, ctx, Object.assign({ sel: 'all' }, c.spec || {})).length, c.cmp || '>=', num(c.value, state, ctx));
  // 「友方单位数量少于敌方」（第989步兵团）
  CONDS.unitCountLess = (state, ctx) => KG.allUnitsOf(state, ctx.owner).length < KG.allUnitsOf(state, 1 - ctx.owner).length;
  CONDS.controlsType = (state, ctx, c) => {
    // filter 可带 keyword/setIn（「控制游击单位」）；只有 unitType 时等价于原来的写法
    const f = c.filter || (c.unitType ? { unitType: c.unitType } : {});
    const n = selectUnits(state, ctx, { sel: 'all', side: c.side || 'self', filter: f }).length;
    return cmp(n, c.cmp || '>=', c.value == null ? 1 : num(c.value, state, ctx));
  };
  CONDS.hasKeyword = (state, ctx, c) => { const u = refUnit(state, ctx, c.target || 'self'); return !!(u && KG.hasKw(u, c.keyword)); };
  CONDS.isUnitType = (state, ctx, c) => { const u = refUnit(state, ctx, c.target || 'self'); return !!(u && KG.isType(u, c.unitType)); };
  CONDS.damaged = (state, ctx, c) => { const u = refUnit(state, ctx, c.target || 'self'); return !!(u && u.defense < u.maxDefense); };
  CONDS.undamaged = (state, ctx, c) => { const u = refUnit(state, ctx, c.target || 'self'); return !!(u && u.defense >= u.maxDefense); };
  // ★ 「若本单位在前线 / 在支援阵线」：看**来源单位自己**所在的阵线（也能看选定目标）。
  //   target 缺省 'self'；zone 缺省 'frontline'。
  CONDS.unitInZone = (state, ctx, c) => {
    const u = refUnit(state, ctx, c.target || 'self');
    return !!(u && String(u.zone) === String(c.zone || 'frontline'));
  };
  CONDS.targetAlive = (state, ctx, c) => { const t = refUnit(state, ctx, c.target || 'target'); return !!(t && !t.dead); };
  // ★ 「其已被压制」——看**选定的目标**是否处于压制状态（抗敌：压制一个敌方单位，如果其已被压制，随机消灭一个敌方单位）
  //   与 hasKeyword('pin') 的区别：这里判的是"已被压制"这个**状态**（pinnedTurns>0 或 kws.pin），
  //   目标可能来自声明式选靶（ctx.targets.t1），不一定是事件主角。
  CONDS.targetPinned = (state, ctx, c) => {
    const tgt = ctx.targets || {};
    const keys = c.target ? [c.target] : Object.keys(tgt).filter(function (k) { return k !== '__pinnedBefore'; });
    if (!keys.length) return false;
    // ★ 优先用"效果开始前"的快照（resolveDeclaredTargets 里拍的）——
    //   否则「压制一个敌方单位，如果其已被压制，…」会在自己 pin 完之后求值 →
    //   条件恒真，等于无条件执行。没有快照时（如监听类效果）才回退到实时状态。
    const snap = tgt.__pinnedBefore || null;
    const check = function (u) { return !!(u && !u.dead && ((u.pinnedTurns > 0) || u.kws.pin)); };
    return keys.some(function (k) {
      if (snap && Object.prototype.hasOwnProperty.call(snap, k)) return !!snap[k];
      const ref = tgt[k];
      const one = Array.isArray(ref) ? ref[0] : ref;
      const uid = (one && one.unit) ? one.unit.uid : (one && one.uid) || (typeof one === 'string' ? one : null);
      const u = uid ? KG.unitByUid(state, uid) : null;
      return check(u);
    });
  };
  CONDS.targetDead = CONDS.targetDestroyed = (state, ctx, c) => {
    const tgt = ctx.targets || {};
    const keys = c.target ? [c.target] : Object.keys(tgt);
    if (!keys.length) return !!ctx.__killedByEffect;   // 没声明目标 → "本效果最近打死过一个单位"
    return keys.some(function (k) {
      const ref = tgt[k];
      const uid = (ref && ref.unit) ? ref.unit.uid : (ref && ref.uid) || (typeof ref === 'string' ? ref : null);
      const u = uid ? KG.unitByUid(state, uid) : null;
      if (!u) return true;                             // 已不在场上 = 被消灭
      return !!u.dead;
    });
  };
  // ★ 取"本次效果声明的目标"：优先指定 key，其次 t/target，最后**扫所有声明目标**
  //   （编译器产出的声明 id 是 t1/t2…，而手写层常用 t —— 只认 't' 会让条件恒假）。
  function pickCtxTarget(ctx, c) {
    const tgt = ctx.targets || {};
    let t = (c.target && tgt[c.target]) || tgt.t || tgt.target || null;
    if (!t) {
      const k = Object.keys(tgt).filter(function (x) { return x !== '__pinnedBefore'; })[0];
      t = k ? tgt[k] : null;
    }
    return Array.isArray(t) ? t[0] : t;
  }
  CONDS.targetIsUnit = (state, ctx, c) => { const one = pickCtxTarget(ctx, c); return !!(one && one.kind === 'unit'); };
  CONDS.targetIsHQ = (state, ctx, c) => { const one = pickCtxTarget(ctx, c); return !!(one && one.kind === 'hq'); };

  // ── 卡牌 / 手牌 / 卡组 ────────────────────────────────────────────
  CONDS.handHasCard = (state, ctx, c) => {
    const p2 = state.players[sideIdx(state, ctx, c.side || 'self')];
    return p2.hand.some(h => { const d = KG.cardDef(state, h.id); return d && String(d.name).indexOf(c.name || '') >= 0; });
  };
  CONDS.cardInDeck = (state, ctx, c) => state.players[ctx.owner].deck.some(function (x) {
    const id = (x && x.id) ? x.id : x;
    const d = KG.cardDef(state, id);
    return d && String(d.name).indexOf(c.name || '') >= 0;
  });
  // 「开发出来的那张牌不在构筑内」（生产）
  CONDS.discoveredCardNotInDeck = (state, ctx, c) => {
    const ev = ctx.event || {};
    const cid = ev.cardId || (ev.card && ev.card.id) || ctx.lastDiscovered;
    if (!cid) return false;
    const d = KG.cardDef(state, cid);
    if (!d) return false;
    // "构筑内" = 这局带进来的卡组里本来就有这张牌
    return !state.players[ctx.owner].deck.some(function (x) { return ((x && x.id) ? x.id : x) === cid; })
      && !(state.players[ctx.owner].startingDeck || []).some(function (x) { return ((x && x.id) ? x.id : x) === cid; });
  };
  CONDS.revealedInEnemyHand = (state, ctx) => state.players[1 - ctx.owner].hand.some(function (h) { return h.revealed; });

  // ── 事件类（监听触发时"这次的当事者是谁"）─────────────────────────
  // 「本次事件涉及的单位是某类」（例如"友方部署一辆Mk坦克后"）
  CONDS.eventUnitIs = (state, ctx, c) => {
    const ev = ctx.event || {};
    const u3 = ev.unit || ev.deadUnit || ev.source;
    if (!u3 || !u3.def) return false;
    const nm = String(u3.def.name || '');
    const types = c.unitType ? (Array.isArray(c.unitType) ? c.unitType : [c.unitType]) : null;
    const nameOk = !c.name || nm.indexOf(c.name) >= 0;
    const typeOk = !types || types.some(function (t) { return KG.isType(u3, t); });
    return nameOk && typeOk;
  };
  CONDS.eventCardIs = (state, ctx, c) => {
    const ev = ctx.event || {};
    const cid = ev.cardId || (ev.card && ev.card.id);
    const cd = cid ? KG.cardDef(state, cid) : null;
    return !!cd && String(cd.name).indexOf(c.name || '') >= 0;
  };
  // ★ 「友方总部受到**来自<兵种>的**伤害时」→ 判断这次伤害的**来源单位**是不是该兵种。
  //   来源由 engine.js 的 damageHQ 写进事件（ev.source）；没有来源（指令直伤 / 无主伤害）→ 不算命中。
  //   ⚠ unitType 可能是**数组**（族，如「陆军」= infantry+tank+artillery）→ 按"任一命中"判。
  // ★ 「若其不小于N，额外获得…」（2026-09-22）——"其"指代**前一个动作的数值**。
  //   本项目里就一种来源：「获得等同于**上一回合被消灭的单位数量**的指挥点；若其不小于N，…」，
  //   所以这里直接把它约定为"上一回合被消灭的单位数"，比较符从卡面捕获。
  //   ⚠ 以后若出现别的"若其…"句式，要在这里扩成按前值类型分派，别再硬编码。
  CONDS.lastTurnKilledCompare = (state, ctx, c) => {
    const p = state.players[ctx.owner] || {};
    const n = p.unitsLostLastTurn || 0;
    const v = num(c.value, state, ctx);
    switch (c.cmp) {
      case '>': return n > v;
      case '>=': return n >= v;
      case '<': return n < v;
      case '<=': return n <= v;
      case '==': return n === v;
      default: return n >= v;      // 卡面「不小于」= >=
    }
  };
  CONDS.damageFromType = (state, ctx, c) => {
    const ev = ctx.event || {};
    const src = ev.source;
    if (!src) return false;
    const ut = src.unitType || (src.def && src.def.unitType) || null;
    if (!ut) return false;
    const want = Array.isArray(c.unitType) ? c.unitType : [c.unitType];
    return want.indexOf(ut) >= 0;
  };
  CONDS.deadUnitCostAtLeast = (state, ctx, c) => {
    const du = ctx.event && ctx.event.deadUnit;
    return !!du && (du.def && (du.def.cost || 0) >= num(c.value, state, ctx));
  };
  // 「（某单位）行动花费 X」——取被选中的单位 / 事件主角；缺省 ctx.unit。
  //   用于「亡计：如果行动花费不小于8，将一张复制加入手牌」这类条件。
  CONDS.opCost = (state, ctx, c) => {
    const u = (ctx.chosen && ctx.chosen.unit) || ctx.unit || (ctx.event && (ctx.event.unit || ctx.event.eventUnit));
    if (!u) return false;
    const cost = (typeof KG.effOpCost === 'function') ? KG.effOpCost(state, u.owner, u) : (u.opCost || 0);
    return cmp(cost, c.cmp || '>=', num(c.value, state, ctx));
  };
  CONDS.defenderIsType = (state, ctx, c) => {
    const d = ctx.event && ctx.event.defender;
    if (!d) return false;
    const list = Array.isArray(c.type) ? c.type : [c.type];
    return list.some(function (tp) { return KG.isType(d, tp); });
  };

  // ── 回合节奏 ─────────────────────────────────────────────────────
  CONDS.playedUnitThisTurn = (state, ctx, c) => state.players[ctx.owner].unitsDeployedThisTurn <= num(c.value == null ? 1 : c.value, state, ctx);
  CONDS.firstUnitThisTurn = (state, ctx) => state.players[ctx.owner].unitsDeployedThisTurn <= 1;
  CONDS.random = (state, ctx, c) => state.rng() < (c.chance == null ? 0.5 : c.chance);

  function evalCond(state, ctx, c) {
    if (!c) return true;
    const fn = CONDS[c.op];
    if (!fn) {
      // 不再静默放行：写一条明确日志，让"写了个没人实现的条件"立刻可见
      if (c.op) KG.log(state, '⚠ 未实现的条件原语: ' + c.op + '（已按"成立"处理）', 'error');
      return true;
    }
    return !!fn(state, ctx, c);
  }
  FX.evalCond = evalCond;

  /* ------------------------------------------------------------------ 动作 */
  const OPS = FX.OPS = {};

  /* ------------------------------------------------ JS 脚本逃生舱（可扩展性兜底）
   * 写法：{ "trigger": "attack", "script": "targets[0].attack = 0" }
   * 脚本里可直接用：
   *   state  — 整局状态（state.players[0].hq 等）
   *   ctx    — 当前上下文（ctx.owner / ctx.unit / ctx.event / ctx.card）
   *   a      — 这条动作本身（含 filter/count 等自定义字段）
   *   targets— 按 a.target 选出的单位数组（自动处理"所有/随机/指向"）
   *   KG     — 引擎全部 API（damageUnit / killUnit / move / canAttack / makeUnit …）
   *   log(t) — 写战斗日志    num(v) — 数值求值    ask(req) — 向玩家询问
   * 返回值忽略；要改数值就直接改对象字段（持久化在单位上）。
   */
  const scriptCache = {};
  async function runEffectScript(state, ctx, a, src) {
    let fn = src;
    if (typeof fn === 'string') {
      if (!scriptCache[fn]) {
        scriptCache[fn] = new Function('state', 'ctx', 'a', 'KG', 'targets', 'log', 'num', 'ask',
          '"use strict";return (async function(){' + fn + '\n})();');
      }
      fn = scriptCache[fn];
    }
    const targets = selectUnits(state, ctx, a.target || a.spec || (a.sel ? a : null));
    ctx.lastAdded = (state.__lastAdded && state.__lastAdded[ctx.owner]) || [];
    const log = (t) => KG.log(state, String(t), 'keyword');
    const numFn = (v) => num(v, state, ctx);
    const ask = (req) => KG.ask(ctx.chooser, req);
    return await fn(state, ctx, a, KG, targets, log, numFn, ask);
  }
  FX.runEffectScript = runEffectScript;

  // 也可以把脚本当成一个 op 用：{"op":"script","code":"..."}
  OPS.script = async function (state, ctx, a) {
    await runEffectScript(state, ctx, a, a.code || a.script);
  };

  // 解析"声明目标"：可能是单位，也可能是**总部**（卡面写「对一个目标造成X点伤害」时，总部也是合法目标）
  //   返回 {kind:'hq', player} 或 null
  function targetEntryHQ(state, ctx, ref) {
    if (typeof ref !== 'string' || !ctx.targets || !ctx.targets[ref]) return null;
    const t = Array.isArray(ctx.targets[ref]) ? ctx.targets[ref][0] : ctx.targets[ref];
    if (!t) return null;
    if (t.kind === 'hq') return { kind: 'hq', player: t.player != null ? t.player : t.value };
    if (typeof t.value === 'string' && /^hq\d$/.test(t.value)) return { kind: 'hq', player: parseInt(t.value.slice(2), 10) };
    return null;
  }
  FX.targetEntryHQ = targetEntryHQ;

  OPS.damage = async function (state, ctx, a) {
    const hq = targetEntryHQ(state, ctx, a.target);
    if (hq) {                                   // 目标选的是总部 → 打总部（「对一个目标造成X点伤害」）
      const p = state.players[hq.player];
      if (p) await KG.damageHQ(state, p, num(a.amount, state, ctx), ctx.source && ctx.source.name, ctx.source);
      return;
    }
    const units = selectUnits(state, ctx, a.target);
    for (const u of units) await KG.damageUnit(state, u, num(a.amount, state, ctx), ctx.source, { fromOrder: !!ctx.fromOrder });
  };
  OPS.damageAll = OPS.damage;
  OPS.heal = async function (state, ctx, a) {
    for (const u of selectUnits(state, ctx, a.target)) {
      if (a.attack === 'defense' || a.defense === 'attack') {
        const from = a.attack === 'defense' ? u.defense : u.attack;
        if (a.attack === 'defense') { u.permAtk = (u.permAtk || 0) + (from - u.attack); }
        else { u.permDef = (u.permDef || 0) + (from - u.maxDefense); u.maxDefense = from; u.defense = from; await killIfNoDefense(state, u, ctx.source); }
      } await KG.healUnit(state, u, num(a.amount, state, ctx)); }
  };
  OPS.healAll = OPS.heal;
  OPS.damageHQ = async function (state, ctx, a) {
    const idx = sideIdx(state, ctx, a.side || (a.target && a.target.side) || 'enemy');
    await KG.damageHQ(state, state.players[idx], num(a.amount, state, ctx), ctx.source && ctx.source.name, ctx.source);
  };
  OPS.healHQ = async function (state, ctx, a) {
    const idx = sideIdx(state, ctx, a.side || 'self');
    await KG.healHQ(state, state.players[idx], num(a.amount, state, ctx));
  };
  /* ★ forEach（Alan 09-28「消灭所有受伤单位，**每消灭一个**，将一张随机进攻洗入卡组」）：
   *   以前 destroy 是一口气 forEach(killUnit)，"每消灭一个"的循环语义没法表达。
   *   现在 a.forEach = [子动作]，**每销毁一个单位结算一次**；子动作里 {sel:'ref', ref:'each'}
   *   / 「该单位」引用刚销毁的那个（和 OPS.forEach 的 as/each 同一套上下文克隆）。 */
  OPS.destroy = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target);
    for (const u of units) {
      await KG.killUnit(state, u, ctx.source);
      if (a.forEach && a.forEach.length) {
        const sub = Object.assign({}, ctx, { targets: Object.assign({}, ctx.targets), vars: Object.assign({}, ctx.vars) });
        sub.targets[a.as || 'each'] = [{ kind: 'unit', value: u.uid, unit: u, label: u.name }];
        sub.chosenTarget = sub.targets[a.as || 'each'][0];
        await FX.exec(state, sub, [{ trigger: null, actions: a.forEach }], ctx.chooser, null);
        if (state.over) break;
      }
    }
  };
  OPS.destroyAll = OPS.destroy;
  OPS.buff = async function (state, ctx, a) {
    const atk = num(a.attack, state, ctx), def = num(a.defense, state, ctx);
    selectUnits(state, ctx, a.target).forEach(function (u) {
      if (a.duration === 'turn') {
        const b = { attack: atk, defense: def, keyword: a.keyword || null, opCostMod: a.opCostMod || 0, untilTurn: state.turn };
        u.tempBuffs.push(b);
        u.attack += atk;
        if (def) { u.maxDefense += def; u.defense += def; }
        if (a.keyword) FX.grantKw(state, u, a.keyword);
        if (a.opCostMod) u.mods.opCostMod += a.opCostMod;
      } else {
        FX.applyBuff(state, u, atk, def);
        if (a.keyword) FX.grantKw(state, u, a.keyword);
        if (a.opCostMod) {
          u.mods.opCostMod += a.opCostMod;
          u.dynMods = u.dynMods || [];
          u.dynMods.push({ mod: 'opCostAdd', value: a.opCostMod });
        }
      }
    });
  };
  OPS.buffAll = OPS.buff;
  OPS.debuff = async function (state, ctx, a) {
    const atk = num(a.attack, state, ctx), def = num(a.defense, state, ctx);
    selectUnits(state, ctx, a.target).forEach(function (u) { applyBuff(state, u, -atk, -def); });
  };
  // 攻击力/防御力互相取值的赋值（"攻击力等同于其防御力"）
  function resolveStatAssign(u, v) {
    if (v === 'defense') return u.defense;
    if (v === 'attack') return u.attack;
    return v;
  }
  // 「升为老兵」：按卡牌的 upgradeTo 换卡（没有则用引擎的老兵判定）
  OPS.upgradeSelf = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target || 'self');
    for (const u of units) {
      if (typeof KG.tryVeteran === 'function') { KG.tryVeteran(state, u); }
      const to = u.def && u.def.upgradeTo;
      if (to && !u.veteran) {
        const p2 = state.players[u.owner];
        u.veteran = true;
        KG.log(state, u.name + ' 升为老兵' + (KG.cardDef(state, to) ? '（' + KG.cardDef(state, to).name + '）' : ''), 'keyword');
      }
    }
  };
  // 「友方X具有+N攻击力」：给场上符合条件的友方单位一个常驻加成
  OPS.auraBuff = async function (state, ctx, a) {
    // 兵种词表从 primitives.js 的 UNIT_TYPES（唯一权威源）取，不再另抄一份
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    const TYPES = (P && P.UNIT_TYPES) || {};
    const t = TYPES[String(a.filter || '').trim()] || null;
    const spec = { sel: 'all', side: 'friendly' };
    if (Array.isArray(t)) spec.filter = { unitType: t };        // 族（陆军/空军）
    else if (t === 'space') spec.filter = { unitType: 'space' }; // 太空族（引擎 TYPE_ALIAS 会展开）
    else if (t) spec.filter = { unitType: t };
    else if (a.filter) spec.filter = { name: a.filter };
    // ★ 「友方**相邻**X」→ 只作用于同一条阵线上与来源单位下标相差 1 的单位
    //   （matchFilter 的 adjacentTo 用 state.__ctxUnit 判定，见 engine.js 的 matchFilter）
    if (a.adjacent) { spec.filter = spec.filter || {}; spec.filter.adjacentTo = 'self'; }
    selectUnits(state, ctx, spec).forEach(function (u) {
      if (a.opCost) {
        // 「友方战斗机行动花费减1」这类光环：只改行动花费，不动身材
        u.mods = u.mods || {};
        u.mods.opCostMod = (u.mods.opCostMod || 0) + a.opCost;
        u.dynMods = u.dynMods || [];
        u.dynMods.push({ mod: 'opCostMod', value: a.opCost });
        return;
      }
      grantKw(state, u, a.attack ? 'buffAtk' : 'buffDef', 0);
      u.permAtk = (u.permAtk || 0) + (a.attack || 0);
      u.attack += (a.attack || 0);
      if (a.defense) { u.maxDefense += a.defense; u.defense += a.defense; }
    });
    KG.log(state, '友方' + a.filter + (a.opCost ? ' 行动花费 ' + a.opCost : '获得 +' + (a.attack || 0) + '/+' + (a.defense || 0)), 'keyword');
  };

  // 「若场上有友方X，获得-N行动花费」
  OPS.condOpCost = async function (state, ctx, a) {
    const u = ctx.unit || refUnit(state, ctx, 'self');
    if (!u) return;
    if (a.filter) {
      const has = KG.allUnitsOf(state, u.owner).some(function (x) { return String(x.name).indexOf(a.filter) >= 0; });
      if (!has) { KG.log(state, '场上没有友方' + a.filter + '，不获得减费', 'keyword'); return; }
    } else {
      // filter 为空 = 「若前线有友方单位则…」这类"任意友方"条件
      const has = KG.frontlineOf(state, u.owner).length > 0 || KG.allUnitsOf(state, u.owner).some(function (x) { return x.zone === 'frontline'; });
      if (!has) { KG.log(state, '前线没有友方单位，不获得减费', 'keyword'); return; }
    }
    u.mods = u.mods || {};
    u.mods.opCostMod = (u.mods.opCostMod || 0) + (a.amount || 0);
    u.dynMods = u.dynMods || [];
    u.dynMods.push({ mod: 'opCostMod', value: a.amount || 0 });
  };

  // 「-N-N，直至下一友方回合开始」
  OPS.debuffTemp = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target || 'self');
    units.forEach(function (u) {
      u.permAtk = (u.permAtk || 0) - (a.attack || 0);
      u.attack = Math.max(0, u.attack - (a.attack || 0));
      if (a.defense) { u.maxDefense = Math.max(1, u.maxDefense - a.defense); u.defense = Math.min(u.defense, u.maxDefense); }
      u.tempDebuff = { attack: a.attack || 0, defense: a.defense || 0, untilTurn: (state.players[u.owner].turnCount || state.turn) + 2 };
    });
  };
  OPS.setStats = async function (state, ctx, a) {
    for (const u of selectUnits(state, ctx, a.target)) {
      // 互相取值：「攻击力等同于其防御力」/「防御力等同于其攻击力」/ 互换
      if (a.attack === 'defense') { const from = u.defense; u.permAtk = (u.permAtk || 0) + (from - u.attack); u.attack = from; continue; }
      if (a.defense === 'attack') { const from = u.attack; u.maxDefense = from; u.defense = from; await killIfNoDefense(state, u, ctx.source); continue; }
      if (a.attack != null) { u.attack = num(a.attack, state, ctx); }
      if (a.defense != null) {
        const d = num(a.defense, state, ctx);
        u.maxDefense = d; u.defense = Math.min(u.defense, d);
        await killIfNoDefense(state, u, ctx.source);
      }
    }
  };
  OPS.setDefense = OPS.setStats;
  /* 「失去 N 点防御力」——**减法**，且允许因此被消灭。
   *   与 debuff 的区别：debuff 走 applyBuff，有 `defense < 1 → 1` 的下限保护（按设计不致死）；
   *   而"失去防御力"在 KARDS 里是真的会把单位打死的，所以这里不设下限、直接过 killIfNoDefense。
   *
   *   byCost:true = 「失去等同于**其**（目标单位自己）花费的防御力」。
   *   ⚠ 为什么不写成 `{stat:'cost'}` 交给 num()：num 的 stat 取值走 `refUnit(ctx,'self')`，
   *     而这一类是指**指令卡**在生效，ctx 里根本没有"自己"这个单位 → refUnit 返回 null
   *     → 兜底 return 0 → 于是"失去防御力"变成"防御力设为 0"，
   *     再叠加 setStats 原来的"赋值不判死"，就出现了卡面 0 防御力却站着的僵尸单位。
   *     所以花费必须**在拿到目标单位之后**再取，不能提前交给 num。 */
  OPS.loseDefense = async function (state, ctx, a) {
    for (const u of selectUnits(state, ctx, a.target)) {
      const n = a.byCost ? ((u.def && u.def.cost) || 0) : num(a.amount != null ? a.amount : a.defense, state, ctx);
      u.defense -= n;
      if (n) KG.log(state, u.name + ' 失去 ' + n + ' 点防御力（剩余 ' + Math.max(0, u.defense) + '）', 'debuff');
      await killIfNoDefense(state, u, ctx.source);
    }
  };
  OPS.draw = async function (state, ctx, a) {
    let idx = sideIdx(state, ctx, a.side || 'self');
    // ★ 2026-09-22：「消灭一个单位，**其所有者**抽N张牌」—— side:{ofTarget:0} = 取本效果
    //   **第 1 个选靶目标**那个单位的拥有者（"其" = 被消灭的那个单位）。
    if (a.side && typeof a.side === 'object' && a.side.ofTarget != null) {
      const vals = ctx.targets ? Object.keys(ctx.targets).map(function (k) { return ctx.targets[k]; }) : [];
      const one = vals[a.side.ofTarget];
      const tu = Array.isArray(one) ? one[0] : one;
      const uu = tu && tu.unit ? tu.unit : (tu && tu.uid ? KG.unitByUid(state, tu.uid) : null);
      if (uu && uu.owner != null) idx = uu.owner;
    }
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    for (let i = 0; i < n; i++) await KG.drawCard(state, state.players[idx], false, { extra: true });
  };
  OPS.discard = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'enemy')];
    // ★ 记录"刚弃掉的牌"，供「并将该牌的复制加入手中」这类后续动作用（自己的 op，不要靠猜）。
    const markDiscarded = function (id) {
      ctx.vars = ctx.vars || {};
      ctx.vars.lastDiscardedCardId = id;
      state.__lastDiscardedCardId = id;
    };
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    if (a.mode === 'named') {
      // 「弃掉手中的空袭」= 弃掉手牌里所有叫这个名字的牌（张数由手牌决定，不看 count）
      const want = String(a.name || '').replace(/[“”"「」]/g, '').trim();
      if (!want) return;
      let got = 0;
      for (let i = p.hand.length - 1; i >= 0; i--) {
        const d = KG.cardDef(state, p.hand[i].id);
        if (d && String(d.name).indexOf(want) >= 0) {
          const inst = p.hand.splice(i, 1)[0];
          p.discard.push(inst.id);
          markDiscarded(inst.id);
          got++;
          KG.log(state, p.name + ' 弃掉了 ' + d.name, 'discard');
        }
      }
      if (!got) KG.log(state, p.name + ' 手中没有「' + want + '」', 'discard');
      return;
    }
    if (a.mode === 'choose') {
      // 由该玩家选择弃哪张（人类玩家会弹出选择；AI 自动挑最差的一张）
      for (let i = 0; i < n && p.hand.length; i++) {
        // ★ 「选择并弃一张单位」：候选只列**符合条件**的手牌（filter.cardType/name…），
        //   否则玩家能从指令牌里挑，与卡面"弃一张单位"不符。
        const cands = [];
        //   判定与 OPS.discardAll 保持一致（matchFilter 不认 cardType，它只认场上的单位）。
        p.hand.forEach(function (inst, idx) {
          const d = KG.cardDef(state, inst.id);
          const f0 = a.filter;
          const match = !f0 || (
            (f0.cardType ? d.cardType === f0.cardType : true) &&
            (f0.name ? String(d.name).indexOf(f0.name) >= 0 : true) &&
            (f0.cardId ? d.id === f0.cardId : true) &&
            (f0.unitType ? d.unitType === f0.unitType : true));
          if (match) cands.push({ idx: idx, d: d });
        });
        if (!cands.length) { KG.log(state, p.name + ' 手上没有符合条件的牌可弃', 'discard'); return; }
        const opts = cands.map(function (c) {
          return { value: c.idx, label: (c.d && c.d.name) + '（' + ((c.d && c.d.cost) || 0) + 'K）' };
        });
        const pick = await KG.ask(ctx.chooser, { kind: 'target', from: 'hand', prompt: a.prompt || '选择一张手牌弃掉', options: opts, owner: p.idx, state: state });
        // 没选到（AI 返回空/下标失效）时退回**候选里的第一张**，别退回手牌第 0 张（可能不符合 filter）
        const idx = typeof pick === 'number' && p.hand[pick] ? pick : cands[0].idx;
        const inst = p.hand.splice(idx, 1)[0];
        if (inst) {
          const dd = KG.cardDef(state, inst.id);
          ctx.vars = ctx.vars || {};
          // 记录"刚被弃掉那张牌的防御力"，供后续「获得等同于其防御力的指挥点」取值
          ctx.vars.lastDiscardedDefense = (dd && (dd.defense != null ? dd.defense : (dd.attack != null ? dd.attack : 0))) || 0;
          state.__lastDiscardedDefense = ctx.vars.lastDiscardedDefense;
          p.discard.push(inst.id);
          markDiscarded(inst.id);
          KG.log(state, p.name + ' 弃掉了 ' + (dd && dd.name), 'discard');
        }
      }
      return;
    }
    for (let i = 0; i < n && p.hand.length; i++) {
      const k = a.mode === 'first' ? 0 : Math.floor(state.rng() * p.hand.length);
      const inst = p.hand.splice(k, 1)[0];
      p.discard.push(inst.id);
      markDiscarded(inst.id);
      KG.log(state, p.name + ' 弃掉了 ' + KG.cardDef(state, inst.id).name, 'discard');
    }
  };
  // 「将该牌的复制加入手中」= 复制**刚被弃掉的那张**进我的手牌（弃牌打点在 OPS.discard 里）
  OPS.copyLastDiscarded = async function (state, ctx, a) {
    const dest = state.players[sideIdx(state, ctx, a.side || 'self')];
    const id = (ctx.vars && ctx.vars.lastDiscardedCardId) || state.__lastDiscardedCardId;
    if (!id) { KG.log(state, '没有可复制的弃牌', 'error'); return; }
    const inst = KG.makeHandInst(id);
    inst.trueCopy = true;
    if (dest.hand.length < KG.RULES.handMax) KG.pushToHand(state, dest, inst);
    KG.log(state, dest.name + ' 把弃掉的 ' + KG.cardDef(state, id).name + ' 的复制加入手牌', 'draw');
  };
  OPS.mill = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'enemy')];
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    for (let i = 0; i < n && p.deck.length; i++) p.discard.push(p.deck.shift());
  };
  // 「友方获得额外指挥点时…」事件
  function emitKredits(state, pi, amount) {
    if (!amount) return;
    /* ★ 牌Q 卡包（2026-09-25）制作者口径：「它在手牌中时，友方获得额外指挥点 → 把自身花费设为 4」。
     *   这类是**卡面级**字段（extraKreditCostSet），不是场上的触发效果 —— 所以在这里扫一遍手牌。 */
    const _p = state.players[pi];
    if (_p) _p.extraKreditsThisTurn = (_p.extraKreditsThisTurn || 0) + amount;
    if (_p && _p.hand) {
      _p.hand.forEach(function (h) {
        const d = KG.cardDef(state, h.id);
        if (d && d.extraKreditCostSet != null) {
          h.costSet = d.extraKreditCostSet;
          KG.log(state, _p.name + ' 的「' + d.name + '」花费设为 ' + d.extraKreditCostSet, 'cost');
        }
      });
    }
    if (typeof KG.runTrigger === 'function') {
      KG.runTrigger(state, { trigger: 'kreditsGained', owner: pi, source: null, amount: amount });
    }
  }
  OPS.gainKredits = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const amt = num(a.amount, state, ctx);
    p.kredits += amt;
    /* ★ 牌Q 卡包（2026-09-25 制作者口径）：「额外获得指挥点」= **非回合开始**的指挥点增长。
     *   ⚠ emitKredits 这个 helper 早就写好了，但**全项目没有一处调用它** ——
     *     于是「友方（单位）额外获得指挥点时…」这一整类监听从来没触发过。
     *     回合开始那一下在 engine.beginTurn（kredits = maxKredits + nextTurnKredits），不走这里。 */
    if (amt > 0) emitKredits(state, p.idx, amt);
  };
  OPS.gainKreditSlot = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    // ★ 「获得一个指挥点槽」只提高**上限**（= 从下一个自己的回合开始，可用的指挥点变多）；
    //   **绝不增加本回合的当前指挥点**。（旧实现误加了 p.kredits，等于"加槽同时送一个指挥点"。）
    //   想要"本回合多一个临时指挥点"应使用 gainKredits，两者语义不同，别混。
    p.maxKredits = Math.min(KG.RULES.kreditSlotHardCap,
      p.maxKredits + num(a.amount == null ? 1 : a.amount, state, ctx));
  };
  OPS.pin = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) {
      if ((u.mods && u.mods.noPin) || u.pinImmune || u.kws.noPin) { KG.log(state, u.name + ' 无法被压制', 'keyword'); return; }
      u.pinnedTurns = Math.max(u.pinnedTurns, num(a.turns == null ? 1 : a.turns, state, ctx));
      u.pinnedUntilTurn = Math.max(u.pinnedUntilTurn || 0, state.turn + (state.active === u.owner ? 2 : 1) + 2 * (u.pinnedTurns - 1));
      u.canAct = false;
      u.kws.pin = true;               // 供 hasKeyword:'pin' 判定
      KG.log(state, u.name + ' 被压制', 'keyword');
      if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'friendlyPinned', owner: u.owner, source: u, victim: u });
    });
  };
  OPS.unpin = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) { u.pinnedTurns = 0; delete u.kws.pin; delete u.pinnedUntilTurn; });
  };
  /* 手牌/卡组预挂（piles）公共筛选（2026-09-26）：与 buffCardsInPiles 的 matches 同一套口径，
   *   抽出来给 grant / grantEffect / buffCardsInPiles 共用。作用对象是卡定义（不是场上单位）。 */
  function pileMatches(filter) {
    return function (d) {
      if (!filter) return true;
      if (filter.cardType && d.cardType !== filter.cardType) return false;
      if (filter.unitType && d.unitType !== filter.unitType) return false;
      if (filter.set && d.set !== filter.set) return false;
      if (filter.maxCost != null && (d.cost || 0) > filter.maxCost) return false;
      if (filter.minCost != null && (d.cost || 0) < filter.minCost) return false;
      if (filter.name && String(d.name).indexOf(filter.name) < 0) return false;
      return true;
    };
  }
  /* 预挂 log 用的触发中文名（取常用；不在表内回落 key） */
  const TRIGGER_CN = {
    deploy: '部署时', death: '亡计', mobilize: '移至前线时', turnStart: '回合开始时',
    turnEnd: '回合结束时', attacked: '被攻击时', attack: '攻击时', afterAttack: '攻击后',
    damaged: '受伤后', play: '使用时', order: '使用时', orderPlayed: '使用指令后',
    unitDeployed: '友方单位部署时', chargeNow: '充能完毕时',
  };
  /* piles 选取（2026-09-26「随机 or 指向」）：buffCardsInPiles / grant(piles) / grantEffect(piles) 共用。
   *   全部（缺省）/ 随机 N 张（random:true + count:N，手牌+卡组合并洗牌）/ 玩家选手牌 N 张
   *   （choose:true + count:N，逐张 ask chooser —— 卡组里的牌双方都看不见，指不了，如实只选手牌）。
   *   handInsts / deckIds 是**已过 filter** 的候选；返回 {hand:[inst], deck:[id]}。
   *   无 chooser（selftest/探针）时 ask 回落第一个候选（engine ask 的既定行为）。 */
  async function pickPiles(state, ctx, a, p, handInsts, deckIds) {
    const count = a.count == null ? null : Math.max(1, num(a.count, state, ctx));
    if (a.choose) {
      const need = count || 1;
      let pool = handInsts.slice();
      const picks = [];
      for (let i = 0; i < need && pool.length; i++) {
        const pick = await KG.ask(ctx.chooser, {
          kind: 'target', from: 'hand',
          prompt: (a.prompt || '选择一张手牌') + (need > 1 ? '（' + (i + 1) + '/' + need + '）' : ''),
          options: pool.map(function (o, j) { return { value: j, label: KG.cardDef(state, o.id).name }; }),
          owner: p.idx, state: state,
        });
        const chosen = (typeof pick === 'number' && pool[pick]) ? pool[pick] : pool[0];
        picks.push(chosen);
        pool.splice(pool.indexOf(chosen), 1);
      }
      if (picks.length) KG.log(state, p.name + ' 选择了 ' + picks.length + ' 张手牌', 'play');
      return { hand: picks, deck: [] };
    }
    if (a.random || count != null) {
      let merged = handInsts.map(function (inst) { return { hand: true, v: inst }; })
        .concat(deckIds.map(function (id) { return { hand: false, v: id }; }));
      if (a.random && merged.length > 1) {
        for (let i = merged.length - 1; i > 0; i--) {
          const j = Math.floor(state.rng() * (i + 1));
          const t = merged[i]; merged[i] = merged[j]; merged[j] = t;
        }
      }
      if (count != null) merged = merged.slice(0, count);
      return {
        hand: merged.filter(function (x) { return x.hand; }).map(function (x) { return x.v; }),
        deck: merged.filter(function (x) { return !x.hand; }).map(function (x) { return x.v; }),
      };
    }
    return { hand: handInsts, deck: deckIds };
  }
  OPS.grant = async function (state, ctx, a) {
    /* 手牌/卡组里的牌获得词条（2026-09-26）：写预挂表，出场/打出时吃到场上单位。
     *   hand → inst.mods.keywords（playCard 打出时消费，engine.js:1639）
     *   deck → cardMods[id].keywords（makeUnit 出场时消费，engine.js:289）
     *   piles:'hand' / 'deck' / 数组或其它真值=两者；省略 piles = 走场上单位原路 */
    if (a.piles) {
      const p = state.players[sideIdx(state, ctx, a.side || 'self')];
      const kws = (Array.isArray(a.keyword) ? a.keyword : [a.keyword]).filter(Boolean);
      if (!kws.length) { KG.log(state, '没有可授予的词条', 'error'); return; }
      const hit = pileMatches(a.filter);
      /* 候选收集 → pickPiles 选取（全部/随机 N/玩家选手牌 N） */
      const handCand = a.piles === 'deck' ? [] : p.hand.filter(function (inst) { return hit(KG.cardDef(state, inst.id)); });
      const deckCand = a.piles === 'hand' ? [] : KG.materializeDeck(state,p).filter(function (id) { return hit(KG.cardDef(state, id)); });
      const sel = await pickPiles(state, ctx, a, p, handCand, deckCand);
      sel.hand.forEach(function (inst) {
        inst.mods = inst.mods || { attack: 0, defense: 0, keywords: [] };
        kws.forEach(function (k) { if (inst.mods.keywords.indexOf(k) < 0) inst.mods.keywords.push(k); });
      });
      sel.deck.forEach(function (id) {
        const m = id.mods = id.mods || { attack: 0, defense: 0, keywords: [] };
        kws.forEach(function (k) { if (m.keywords.indexOf(k) < 0) m.keywords.push(k); });
      });
      const n = sel.hand.length + sel.deck.length;
      KG.log(state, p.name + ' 的 ' + n + ' 张牌获得词条「' + kws.map(k => (KG.KEYWORDS[k] && KG.KEYWORDS[k].cn) || k).join('、') + '」', 'keyword');
      return;
    }
    selectUnits(state, ctx, a.target).forEach(function (u) { grantKw(state, u, a.keyword, a.value); });
  };
  OPS.grantAll = OPS.grant;
  OPS.removeKeyword = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) {
      delete u.kws[a.keyword];
      if (a.keyword === 'ambush') u.ambushReady = false;
      if (a.keyword === 'deathrattle') { u.mods.suppressDeathrattle = true; u.deathSuppressed = true; }
      if (a.keyword === 'guard' || a.keyword === 'smokescreen') { /* 立即生效 */ }
    });
  };
  /* ★ 抑制（2026-10-02 Alan 定稿）：**一次性清除** —— 被抑制的单位在那一瞬失去
   *   所有对战词条、效果和属性增益（光环也算：当时罩在它身上的光环加成一并清掉）；
   *   之后它是一张白纸，可以重新贴膜 —— 包括光环（光环是持续效果，recompute 后照常
   *   重新罩上来）。卡面自带的文字效果（u.silenced）不恢复。
   *   具体动作：清词条 + 关卡面效果 + 属性回滚到基础身材 + dynMods/tempBuffs 清空 +
   *   auraAtk/auraDef/auraKws 记账清零（再 recompute 一次，持续光环自然重新落账）。
   *   免疫：mods.noSuppress（「无法被抑制」常驻修正）。
   *   注：旧名 silence —— 数据里 0 张卡用过它，现升格为唯一的「抑制」原语，不新占 op 名。
   *   监听触发：friendlySuppressed（「友方单位被抑制时」）；原 friendlySilenced 从未被使用，一并改名。 */
  OPS.silence = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target);
    units.forEach(function (u) {
      if (u.dead) return;
      if ((u.mods && u.mods.noSuppress) || u.noSuppress) { KG.log(state, u.name + ' 无法被抑制', 'keyword'); return; }
      u.kws = {}; u.ambushReady = false; u.silenced = true;
      u.extraEffects = []; u.grantedEffects = []; u.primitiveExpiry=[];u.primitiveAura=[];
      u.magneticCharges = 0; u.shieldReady = false;
      u.kwValues = {}; u.chargeReady = false; u.chargeArmed = false; u.chargeConsumed = [];
      delete u.chargeIn; delete u.chargeArmedUndo; delete u.chargeAtkBonus;
      u.pinnedTurns = 0; delete u.pinnedUntilTurn;
      // 属性增益回滚：回到基础身材。
      // ⚠ 顺序陷阱（自测抓到的）：必须**同时把 auraAtk/auraDef 归零**再重置 attack/defense ——
      //   recomputeAuras 第 1 步会执行 `attack -= auraAtk`，若这里只重置不归零，
      //   光环加成会被扣两次（实测光环单位被抑制后攻 2→1，比基础值还低）。
      //   归零后第 1 步不再扣；随后 recompute 会把**持续光环重新罩上来**（= 重新贴膜包括光环）。
      u.auraAtk = 0; u.auraDef = 0; u.auraKws = []; u.auraRmKws = [];
      u.permAtk = 0; u.permDef = 0; u.tempBuffs = [];
      if (u.baseAttack != null) u.attack = u.baseAttack;
      if (u.baseDefense != null) { u.maxDefense = u.baseDefense; u.defense = Math.max(1, u.baseDefense); }
      // 动态修正（grantMod / opCostMod 这类"贴上去"的常驻修正）也算增益，一并清掉；
      // 第 2 步重放 dynMods，数组清空后就不会再回来。turnMods（本回合临时修正）同属增益。
      u.dynMods = []; u.turnMods = [];
      FX.recomputeAuras(state);   // 清完立刻重算：持续光环按"还在场的光环源"重新落账
      KG.log(state, u.name + ' 被抑制', 'keyword');
      if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'friendlySuppressed', owner: u.owner, source: u, victim: u });
    });
  };
  /* ★ 移除（2026-10-02 Alan 定义）：被移除的单位离开战场，**不算被消灭** ——
   *   不走 killUnit：不派发 death / friendlyDeath / afterKill、不计入 unitsLost、
   *   不吃「突袭」那类亡计打断、卡牌也不进弃牌堆，而是进该方的 removed 区（与「流亡」同区）。
   *   同时派发新触发 unitLeft —— 「单位不因消灭离开战场时」监听用（撤退回手牌、手牌满转移除也派发）。 */
  function leaveFieldWithoutDestroy(state, u, why) {
    if (!u || u.dead) return;
    u.dead = true;                       // 复用 dead 标记：立刻退出所有"在场"判定（单位已不在任何阵线数组里）
    KG.removeUnitFromBoard(state, u);
    const p0 = state.players[u.owner];
    if (p0) p0.removed.push(u.cardId);
    KG.log(state, u.name + (why || ' 被移除'), 'death');
    if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'unitLeft', owner: u.owner, source: u, victim: u });
  }
  OPS.removeUnit = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) { leaveFieldWithoutDestroy(state, u, ' 被移除'); });
  };

  OPS.returnToHand = async function (state, ctx, a) {
    // 制作者规则：前线 → 退回支援线；支援线 → 退回手牌
    const units0 = selectUnits(state, ctx, a.target);
    for (const u of units0) {
      if (u.zone === 'frontline') {
        if (state.players[u.owner].support.length >= KG.RULES.supportMax) { KG.log(state, u.name + ' 无法撤退：支援阵线已满', 'error'); continue; }
        KG.removeUnitFromBoard(state, u);
        u.zone = 'support';
        state.players[u.owner].support.push(u);
        KG.log(state, u.name + ' 撤退回支援阵线', 'move');
      } else {
        KG.removeUnitFromBoard(state, u);
        const p0 = state.players[u.owner];
        /* ★ 手牌已满：撤退改为**移除**（2026-10-02 Alan 定义）—— 不算被消灭，进 removed 区 */
        if (p0.hand.length >= KG.RULES.handMax) {
          leaveFieldWithoutDestroy(state, u, ' 撤退失败（手牌已满），被移除');
          continue;
        }
        const returned = KG.makeHandInst(u.cardId); returned.revealed = true;
        KG.pushToHand(state, p0, returned);
        KG.log(state, u.name + ' 撤退回手牌', 'move');
        if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'unitLeft', owner: u.owner, source: u, victim: u });
      }
      if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'friendlyRetreated', owner: u.owner, source: u, victim: u });
    }
    return;
  };
  // 「撤退」= 同一套语义（前线→支援线，支援线→手牌）。卡面和原语里写的就是 retreat，
  // 以前没有这个 op，导致 {"op":"retreat"} 只打一条"未实现"日志。
  OPS.retreat = OPS.returnToHand;
  OPS._returnToHandOld = async function (state, ctx, a) {    selectUnits(state, ctx, a.target).forEach(function (u) {
      const p = state.players[u.owner];
      KG.removeUnitFromBoard(state, u);
      u.dead = true;
      if (p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(u.cardId));
    });
  };
  OPS.summon = async function (state, ctx, a) {
    const side = sideIdx(state, ctx, a.side || 'self');
    const p = state.players[side];
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    // 可按 filter 随机召唤（例如 zbk1母舰："将随机太空战机加入同一阵线"）
    // 注意：只有 filter 里带了真正的筛选条件（兵种/系列）才走随机；只写了 name 的话那是卡名，不是筛选
    let pickIds = null;
    if (a.filter && (a.filter.unitType || a.filter.set)) {
      pickIds = Object.keys(KG.pool || {}).map(function (id) { return KG.cardDef(state, id); })
        .filter(function (c) {
          if (c.cardType !== 'unit' || c.referenceCard) return false;
          if (a.filter.unitType) {
            const types = Array.isArray(a.filter.unitType) ? a.filter.unitType : [a.filter.unitType];
            if (!types.some(function (t) { return KG.isType({ unitType: c.unitType, extraTypes: c.extraTypes }, t); })) return false;
          }
          if (a.filter.set && c.set !== a.filter.set) return false;
          /* ★ 2026-09-24：filter 里写了 nameIncludes/name 就**必须尊重**——
           *   以前只按兵种筛、名字条件被无视 → 「将一张“332工程步兵团”加入支援阵线」
           *   （filter {unitType:'infantry', nameIncludes:'332工程'}）实际召出的是**随机步兵**。
           *   「随机Mk坦克」{unitType:'tank', nameIncludes:'Mk'} 同理早就在乱召。 */
          if (a.filter.nameIncludes && String(c.name || '').indexOf(a.filter.nameIncludes) < 0) return false;
          if (a.filter.name && String(c.name || '').indexOf(a.filter.name) < 0) return false;
          /* ★ 牌Q 卡包（2026-09-25）：「将一张花费不大于N的X单位加入支援线」必须真按花费筛，
           *   否则会召出任意一张同系列单位（静默错卡）。 */
          if (a.filter.maxCost != null && (c.cost || 0) > a.filter.maxCost) return false;
          if (a.filter.minCost != null && (c.cost || 0) < a.filter.minCost) return false;
          if (a.filter.system && !FX.cardHasSystem(c, a.filter.system)) return false;
          return true;
        }).map(function (c) { return c.id; });
    }
    const ctxUnits = [];
    for (let i = 0; i < n; i++) {
      const toFront = a.to === 'frontline' && KG.frontlineOf(state, 1 - side).length === 0 && KG.frontlineOf(state, side).length < KG.effectiveFrontlineMax(state, side);
      const toSame = a.to === 'sameZone' && ctx.unit && ctx.unit.zone === 'frontline'
        && KG.frontlineOf(state, 1 - side).length === 0 && KG.frontlineOf(state, side).length < KG.effectiveFrontlineMax(state, side);
      const useFront = toFront || toSame;
      if (!useFront && p.support.length >= KG.RULES.supportMax) break;
      let id = opCardId(state, ctx, a);
      if (pickIds) { if (!pickIds.length) break; id = pickIds[Math.floor(state.rng() * pickIds.length)]; }
      if (!id) break;
      const u = KG.makeUnit ? KG.makeUnit(state, id, side) : null;
      if (!u) break;
      u.summonedTurn = state.turn;
      u.canAct = !!u.kws.blitz;
      u.zone = useFront ? 'frontline' : 'support';
      // ★ 「加入总部相邻处」= 插到总部紧邻的位置（支援线数组下标 = hqSlot，即总部右邻）。
      //   「加入支援阵线」才是固定放最右边（push 到末尾）。制作者明确指出两者不同。
      const hqAdj = a.to === 'hqAdjacent' && !toFront;
      if (useFront) state.frontline.push(u);
      else if (hqAdj && typeof KG.insertUnitAt === 'function') {
        const hqSlot = p.hqSlot != null ? p.hqSlot : p.support.length;
        // hqDisplace:'right'：总部**相邻** = 右邻，不把总部顶开（数组 at===hqSlot 的默认语义，显式写明）
        KG.insertUnitAt(state, side, u, 'support', Math.min(hqSlot, p.support.length), { hqDisplace: 'right' });
      } else p.support.push(u);
      if (KG.applyAlpine) KG.applyAlpine(state, u);
      ctxUnits.push(u);
      KG.log(state, p.name + ' 召唤了 ' + u.name + '（' + u.attack + '/' + u.defense + '）到' + (useFront ? '前线' : '支援阵线'), 'play');
    }
    ctx.summoned = ctxUnits;      // 供后续动作引用（例如"获得其中一个的攻击力"）
    // ★ 2026-09-25：summon 进场的单位也要执行**自己的「部署：」效果** ——
    //   playCard 部署路径（engine.js）会跑 FX.exec('deploy')，但 summon 只广播了
    //   unitDeployed 监听，自身 deploy 效果从不执行 → 被卫星计划召唤的 332工程步兵团
    //   （「下一个加入战场的友方单位获得守护」）连附魔都挂不上（hqEnchants 恒空）。
    //   顺序对齐 playCard：先跑自身 deploy，再广播 unitDeployed
    //   （监听侧用 ench.source===ev.source 排除自己，不会自吃）。
    //   注：打断型反制（consumeInterrupt）只挂"打出"语义，summon 进场不查，与 playCard 不同。
    // ★ 拉进场的单位也要广播「加入战场」（2026-09-24）：
    //   否则「下一个加入战场的友方单位获得X」这类 once 附魔对 summon 进场**完全不触发**
    //   （卫星计划→332 不贴守护的真凶）。广播与 playCard 的 unitDeployed 同一事件、不重复。
    for (const cu of ctxUnits) {
      await KG.runTrigger(state, { trigger: 'unitDeployed', owner: side, source: cu, unit: cu, unitType: cu.unitType, name: cu.name });
    }
    if (ctxUnits.length) KG.recomputeAuras(state);
    return ctxUnits;
  };
  // 「抽一张XX / 将一张XX加入手牌」：**先在你的卡组里找**（受构筑张数限制），
  // 卡组里没有时：若 XX 是令牌/无标志卡（本来就不在卡组里）才"生成"一张；
  // 若 XX 是正常可构筑卡而卡组里已经没有了 → 什么也不发生（不能再凭空变出第 5 张）
  function isTokenCard(state, id) {
    const d = KG.cardDef(state, id);
    if (!d) return false;
    if (d.token || d.rarity === 'token') return true;
    return !d.rarity;                       // 没有稀有度标志 = 不可构筑（令牌）
  }
  function takeFromDeckByName(state, p, id) {
    const i = p.deck.findIndex(function (x) { return x === id || (x && x.id === id); });
    if (i < 0) return false;
    p.deck.splice(i, 1);
    return true;
  }
  // 「抽一张XX」→ 只从卡组拿（取走，受构筑张数限制）
  // 「将一张XX加入手牌」→ 生成（卡池），不消耗卡组
  OPS.addCardToHand = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const id = opCardId(state, ctx, a);
    if (!id) return;
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    const fromDeck = a.from === 'deck';
    // ★ from:'deck' + filter（卡面"从卡组抽一张X"，没点名）：
    //   必须**在卡组里**按 filter 选卡 —— 池里有 ≠ 卡组里有（构筑上限/卡组只带一部分），
    //   先在池里选 id 再去卡组找，选中一张卡组里没有的就"静默少拿"。
    //   卡组序在开局时锁定（seeded 洗牌，两边一致），在这里筛选天然确定（联机安全）。
    let deckPick = null;
    if (fromDeck && !a.cardId && (a.name == null || a.name === '') &&
        a.filter && Object.keys(a.filter).length) {
      deckPick = pickDeckCardIdByFilter(state, p, a.filter, !!a.random);
      if (!deckPick) {
        KG.log(state, '卡组里没有符合「' + JSON.stringify(a.filter) + '」的卡，本次少拿了', 'draw');
        return;
      }
    }
    let got = 0, empty = 0;
    for (let i = 0; i < n; i++) {
      if (p.hand.length >= KG.RULES.handMax) break;
      if (fromDeck) {
        if (!takeFromDeckByName(state, p, deckPick || id)) { empty++; continue; }
      }
      // ★ 「其花费减为0」/「其花费减少N点」：把花费修正打在**加入的实例**上
      //   （instCost 首行就认 costSet；costMod 走常规加减）。
      //   只修这一句产出的牌，不影响手里原有的同名卡。
      const inst = KG.makeHandInst(id);
      if (a.costTo != null) inst.costSet = a.costTo;
      else if (a.costMod) inst.costMod = (inst.costMod || 0) + a.costMod;
      KG.pushToHand(state, p, inst);
      got++;
    }
    const nm = KG.cardDef(state, id).name;
    if (got) KG.log(state, p.name + (fromDeck ? ' 从卡组抽取了 ' : ' 获得了 ') + got + ' 张 ' + nm, 'draw');
    if (empty) KG.log(state, '卡组里已经没有「' + nm + '」了（构筑上限 4 张），本次少拿了 ' + empty + ' 张', 'draw');
  };
  OPS.intel = async function (state, ctx, a) {
    const foe = state.players[1 - ctx.owner];
    if (!foe.hand.length) return;
    const k = Math.floor(state.rng() * foe.hand.length);
    const id = foe.hand[k];
    state.intel = state.intel || {};
    state.intel[ctx.owner] = state.intel[ctx.owner] || [];
    state.intel[ctx.owner].push(id);
    KG.log(state, '情报：看到对手手牌中的 ' + KG.cardDef(state, id).name, 'keyword');
  };
  OPS.reveal = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) { u.revealed = true; });
  };
  OPS.move = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) {
      const p = state.players[u.owner];
      const to = a.to === 'frontline' ? 'frontline' : 'support';
      if (u.zone === to) return;
      if (to === 'frontline') {
        if (KG.frontlineOf(state, 1 - u.owner).length > 0) return;     // 对手还占着前线
        if (KG.frontlineOf(state, u.owner).length >= KG.effectiveFrontlineMax(state, u.owner)) return;
      }
      if (to === 'support' && p.support.length >= KG.RULES.supportMax) return;
      KG.removeUnitFromBoard(state, u);
      if (to === 'frontline') state.frontline.push(u); else p.support.push(u);
      u.zone = to;
      KG.log(state, u.name + ' 被移动到' + (to === 'frontline' ? '前线' : '支援线'), 'move');
    });
  };
  /* ── 夺取控制权（"控制被攻击单位"，2026-09-22）──
   * 把目标单位从原拥有者的板上搬到效果主人（ctx.owner）的板上：
   * 我方前线有空位就上前线（前线是双方共抢的一条线），满了退而进支援线。
   * ⚠ 目标若已在攻击中阵亡（不在场上）→ 跳过：卡面语义是"夺一个活单位"。 */
  OPS.takeControl = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) {
      if (!u || u.dead) return;
      if (u.owner == null || u.owner === ctx.owner) return;
      if (!KG.zoneOf(state, u.uid)) return;              // 已不在场上（被打死了）
      const canFront = KG.frontlineOf(state, 1 - ctx.owner).filter(function (x) { return x.uid !== u.uid; }).length === 0
        && KG.frontlineOf(state, ctx.owner).length < KG.effectiveFrontlineMax(state, ctx.owner);
      if (!canFront && state.players[ctx.owner].support.length >= KG.RULES.supportMax) return;
      KG.removeUnitFromBoard(state, u);
      u.owner = ctx.owner;
      if (canFront) {
        state.frontline.push(u); u.zone = 'frontline';
      } else {
        state.players[ctx.owner].support.push(u); u.zone = 'support';
      }
      KG.log(state, u.name + ' 的控制权被夺取', 'move');
    });
  };
  OPS.costReduce = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    p.costMod = (p.costMod || 0) - num(a.amount == null ? 1 : a.amount, state, ctx);
  };
  OPS.armorBonus = async function (state, ctx, a) {
    selectUnits(state, ctx, a.target).forEach(function (u) {
      u.armorBonus = (u.armorBonus || 0) + num(a.amount == null ? 1 : a.amount, state, ctx);
    });
  };
  OPS.activate = async function (state, ctx, a) {
    // 手动触发另一个效果组（用于连锁）
    await FX.exec(state, ctx, a.effects || [], ctx.chooser, null);
  };
  /* ★★ 「作为变量」（制作者要求 2026-09-27）：把这次**召唤出来的单位**（或某个引用）
   *   记成具名变量，后面的动作就能用 {sel:'ref', ref:'v1'} / 字符串 'v1' 引用它。
   *   from: card（最近加入手牌）/ hand（手牌里的牌，filter.cardType 可筛单位/指令）/
   *         deck（卡组里的牌）/ field（场上单位，标准选择器 target）/ summoned / target / eventUnit / var。
   *   作用域 = 本次结算上下文 ctx.vars（与 setVar 的数值变量同一张表；单位靠 u.uid 区分）。 */
  OPS.asVar = async function (state, ctx, a) {
    const name = String(a.name || a.as || 'v1');
    const from = String(a.from || 'summoned');
    let units = [];
    if (from === 'card') {
      /* ★ 卡牌变量（Alan 09-26「只要包含卡牌的动作都能将其设为变量并调用它的数值」）：
       *   源 = state.__lastAdded（engine pushToHand 自动记录最近 6 张**加入手牌的实例**——
       *   抽牌 / addCardToHand / shuffleIn / deckToHand 全都在内）。取最近 N 张；
       *   cardId 字面量源则包一层 {id}（数值端 num.cardVar 只读 id，卡实例/包装通吃）。
       *   存实例不存数值快照 —— 读的时候按 stat 现查卡定义。 */
      if (a.cardId) units = [{ id: a.cardId }];
      else {
        const la = (state.__lastAdded && state.__lastAdded[ctx.owner]) || [];
        const n2 = a.count == null ? 1 : num(a.count, state, ctx);
        units = la.slice(-n2);
      }
    }
    else if (from === 'hand' || from === 'deck') {
      /* ★ 全源扩展（Alan 09-27「所有的东西都可以设为变量。比如单位、指令等」）：
       *   手牌存**实例**（cardVar 现查数值），卡组只有 id → 包 {id}（num.cardVar 只读 id，通吃）。
       *   filter 与 buffCardsInPiles 同口径（cardType/unitType/maxCost/name，作用对象是卡定义）；
       *   选取复用 pickPiles：缺省全部 / random N 张（洗牌截取）/ choose N 张（逐张 ask，仅手牌能指）。
       *   side 缺省自己——也能记对面的手牌/卡组（情报型卡面用）。 */
      const p = sideIdx(state, ctx, a.side || 'self');
      const pl = state.players[p] || {};
      const m = pileMatches(a.filter);
      const handCand = (pl.hand || []).filter(u => u && m(KG.cardDef(state, u.id)));
      const deckCand = (pl.deck || []).filter(id => m(KG.cardDef(state, id)));
      const got = await pickPiles(state, ctx, a, p, handCand, deckCand);
      units = from === 'hand' ? got.hand : (got.deck || []).map(id => ({ id: id }));
    }
    else if (from === 'field') {
      /* ★ 场上单位（Alan 09-27）：标准选择器（target：all/random/choose/ref + side/zone/filter/count），
       *   缺省 = 所有友方单位；选出来的单位实例直接进变量，之后 {sel:'ref',ref:'v1'} 引用（refUnit 读 ctx.vars）。 */
      units = selectUnits(state, ctx, a.target || a.spec || { sel: 'all', side: 'friendly' });
    }
    else if (from === 'summoned') units = (ctx.summoned || []).slice();
    else if (from === 'var') { const v = (ctx.vars || {})[a.ref]; units = Array.isArray(v) ? v.slice() : (v ? [v] : []); }
    else { const u = refUnit(state, ctx, a.ref || (from === 'eventUnit' ? 'eventUnit' : 'target')); if (u) units = [u]; }
    ctx.vars = ctx.vars || {};
    ctx.vars[name] = units;
    const say = {
      card: '把最近加入手牌的 ' + units.length + ' 张卡记为变量「' + name + '」',
      hand: '把手牌里的 ' + units.length + ' 张牌记为变量「' + name + '」',
      deck: '把卡组里的 ' + units.length + ' 张牌记为变量「' + name + '」',
      field: '把 ' + units.length + ' 个场上单位记为变量「' + name + '」',
    }[from];
    KG.log(state, say || ('把 ' + units.length + ' 个单位记为变量「' + name + '」'), 'keyword');
  };
  OPS.setVar = async function (state, ctx, a) {
    ctx.vars = ctx.vars || {};
    ctx.vars[a.name] = num(a.value, state, ctx);
  };
  /* ★★「第 N 次才做的事」（Alan 09-28「友方单位部署时，将其返回手牌，三次过后，升为老兵」）
   *   每次跑到这一步计数 +1，够 N 了才执行 then 里的动作。
   *
   *   ⚠⚠ 计数**绝对不能存 ctx**：ctx 是每次结算新建的，存进去等于永远从第 1 次开始。
   *     默认挂 `state.__nth[key]`（跨回合、跨次保留，和 state.__lastAdded 一个道理）；
   *     `a.on==='unit'` 时挂在**单位实例** `u.__nth[key]`（离场即归零，适合"这个单位第 N 次攻击"）。
   *
   *   a: { key, count:3, then:[...动作], every:false, reset:false, on:'global'|'unit', target }
   *     · every/reset = true → 每次达成后**清零**，变成"每 N 次一次"（默认 false = 门槛，之后每次都做）
   *     · key 建议写死（如 'bounce'）；不写就按 then 的内容自动生成一个稳定的键。
   */
  OPS.nthTime = async function (state, ctx, a) {
    const need = Math.max(1, Math.floor(num(a.count == null ? 1 : a.count, state, ctx)));
    const key = String(a.key || ('nth:' + (ctx.srcId || ctx.cardId || 'card') + ':' + JSON.stringify(a.then || []).slice(0, 80)));
    let store = null, where = '';
    if (a.on === 'unit') {
      const us = selectUnits(state, ctx, a.target || 'self');
      const u = us && us[0];
      if (!u) return;                       // 目标没了 → 这次不算
      u.__nth = u.__nth || {}; store = u.__nth; where = u.name;
    } else {
      state.__nth = state.__nth || {}; store = state.__nth; where = '本局';
    }
    store[key] = (store[key] || 0) + 1;
    const got = store[key];
    const hit = got >= need;
    KG.log(state, where + '：第 ' + got + ' / ' + need + ' 次' + (hit ? ' → 达成' : ''), 'info');
    if (!hit) return;
    if (a.every || a.reset) store[key] = 0;
    const acts = a.then || a.actions || [];
    for (const act of acts) {
      const fn = act && OPS[act.op];
      if (fn) await fn(state, ctx, act);
      else if (act && act.op) KG.log(state, '第 N 次里有未实现的动作：' + act.op, 'error');
    }
  };
  OPS.log = async function (state, ctx, a) { KG.log(state, a.text || '', 'info'); };
  // 抉择
  OPS.chooseOne = async function (state, ctx, a) {
    let opts = (a.options || []).slice();
    // 只保留条件满足 / 付得起的选项
    opts = opts.filter(function (o) {
      if (o.condition && !evalCond(state, ctx, o.condition)) return false;
      if (o.requireKredits != null && state.players[ctx.owner].kredits < num(o.requireKredits, state, ctx)) return false;
      if (o.requireKreditsExpr) { /* 由 condition 处理 */ }
      return true;
    });
    if (!opts.length) { KG.log(state, '没有可用选项', 'error'); return; }
    // ★ 选项顺带带出"这个选项会给你哪张卡"（研发/抉择类），供 UI 显示卡图。
    //   卡 id 本来只在 actions 里（addCardToHand/shuffleIn/summon 的 cardId 或 name），
    //   这里不带上，界面就只能显示一行纯文字（"将三张星盟通用大驱加入手牌"）。
    const cardOf = function (o) {
      const acts = o.actions || [];
      for (let i = 0; i < acts.length; i++) {
        const a = acts[i];
        if (!a || ['addCardToHand', 'shuffleIn', 'summon', 'addCardToDeck'].indexOf(a.op) < 0) continue;
        if (a.cardId) return { cardId: a.cardId, cardCount: a.count };
        if (a.name) return { cardName: a.name, cardCount: a.count };
      }
      return null;
    };
    const list = opts.map(function (o, i) {
      const item = { value: i, label: o.label || ('选项' + (i + 1)) };
      const c = cardOf(o);
      if (c) { if (c.cardId) item.cardId = c.cardId; else item.cardName = c.cardName; if (c.cardCount > 1) item.cardCount = c.cardCount; }
      return item;
    });
    const pick = await KG.ask(ctx.chooser, { kind: 'chooseOne', prompt: a.prompt || '抉择', options: list, state: state });
    const idx = typeof pick === 'number' && opts[pick] ? pick : 0;
    const chosen = opts[idx];
    if (chosen) {
      if (chosen.cost != null) {
        const p = state.players[ctx.owner];
        p.kredits = Math.max(0, p.kredits - num(chosen.cost, state, ctx));
        KG.log(state, '支付 ' + num(chosen.cost, state, ctx) + ' 指挥点', 'cost');
      }
      await FX.exec(state, ctx, [{ trigger: null, actions: chosen.actions || [] }], ctx.chooser, null);
    }
  };
  OPS.conditional = async function (state, ctx, a) {
    if (evalCond(state, ctx, a.condition)) await FX.exec(state, ctx, [{ trigger: null, actions: a.then || a.actions || [] }], ctx.chooser, null);
    else if (a.else) await FX.exec(state, ctx, [{ trigger: null, actions: a.else }], ctx.chooser, null);
  };
  OPS.forEach = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target || a.spec);
    for (const u of units) {
      const sub = Object.assign({}, ctx, { targets: Object.assign({}, ctx.targets), vars: Object.assign({}, ctx.vars) });
      sub.targets[a.as || 'each'] = [{ kind: 'unit', value: u.uid, unit: u, label: u.name }];
      sub.chosenTarget = sub.targets[a.as || 'each'][0];
      await FX.exec(state, sub, [{ trigger: null, actions: a.actions }], ctx.chooser, null);
      if (state.over) break;
    }
  };
  OPS.repeat = async function (state, ctx, a) {
    const n = num(a.times, state, ctx);
    for (let i = 0; i < n; i++) {
      await FX.exec(state, ctx, [{ trigger: null, actions: a.actions }], ctx.chooser, null);
      if (state.over) break;
    }
  };
  OPS.randomPick = async function (state, ctx, a) {
    const pool = a.options || [];
    if (!pool.length) return;
    const k = Math.floor(state.rng() * pool.length);
    await FX.exec(state, ctx, [{ trigger: null, actions: pool[k].actions || pool[k] }], ctx.chooser, null);
  };
  /* ★★ 「开发」原语（制作者定义）
   *   开发 = 从**这张卡自己的特定卡池**里复制一张进入手牌（卡池由卡面描述决定，见各卡的 filter）。
   *   从卡组中开发 = 从**构筑（卡组）**里选一张**复制**进手牌 —— 卡组里那张**不会消失**。
   *   ⚠ 与 `discover`（通用"展示若干张选一张"）区分：开发的语义是"从指定卡池/构筑**复制**"。
   *     本原语保留与旧 `discover` 相同的交互（chooser kind='discover'），以兼容既有 UI 与数据。
   */
  OPS.develop = async function (state, ctx, a) {
    // 「开发」= 从**这张卡自己的特定卡池**里随机抽 3 张选 1 张入手（卡池由卡面描述决定，见各卡的 filter）。
    // 例外：卡面写明"从卡组中开发" → a.from === 'deck'，从自己卡组里开发，且**是复制**（卡组里那张不会消失）。
    const match = function (c) {
      if (a.filter) {
        if (a.filter.cardType && c.cardType !== a.filter.cardType) return false;
        if (a.filter.set && c.set !== a.filter.set) return false;
        // setIn：跨国家卡池（例如「协约国」= USG + AV76）
        if (a.filter.setIn && a.filter.setIn.indexOf(c.set) < 0) return false;
        if (a.filter.rarity && c.rarity !== a.filter.rarity) return false;
        if (a.filter.maxCost != null && (c.cost || 0) > a.filter.maxCost) return false;
        if (a.filter.minCost != null && (c.cost || 0) < a.filter.minCost) return false;
        if (a.filter.unitType) {
          const types = Array.isArray(a.filter.unitType) ? a.filter.unitType : [a.filter.unitType];
          const setOfTypes = { air: ['fighter', 'spacefighter', 'bomber'] };   // 空军 = 战斗机 + 轰炸机
          const wanted = [];
          types.forEach(function (t) { (setOfTypes[t] || [t]).forEach(function (x) { wanted.push(x); }); });
          if (!wanted.some(function (t) { return KG.isType({ unitType: c.unitType, extraTypes: c.extraTypes }, t); })) return false;
        }
        // cardIds：指名道姓的卡池（例如"帝国坦克"= 帝国一号/二号/三号坦克）
        if (a.filter.cardIds && a.filter.cardIds.indexOf(c.id) < 0) return false;
        // ⚠ filter.name 是**卡名字串**匹配，必须先去掉卡面引号（「」“”""）——
        //   卡面常写成「开发一张「经济复苏」」，引号只是引用标记，不属于卡名。
        //   不去掉的话 indexOf 恒为 -1 → 卡池空 → **整张卡静默失效**（同一类坑：不报错）。
        if (a.filter.name) {
          const want = String(a.filter.name).replace(/[「」“”"'‘’]/g, '').trim();
          if (want && String(c.name).indexOf(want) < 0) return false;
        }
      }
      if (c.referenceCard) return false;
      // 令牌卡：泛用筛选（如"USG单位"）时排除；但如果卡池是**指名道姓**要它（name/cardIds），就允许
      const named = !!(a.filter && (a.filter.name || a.filter.cardIds));
      if (c.token && a.from !== 'pool' && !a.allowToken && !named) return false;
      return true;
    };
    const p = state.players[ctx.owner];
    let source = null;
    if (a.from === 'deck') {
      source = p.deck.map(function (x) { return (x && x.id) ? x.id : x; })
        .map(function (id) { return KG.cardDef(state, id); })
        .filter(function (c) { return c && match(c); });
    } else {
      source = Object.keys(KG.pool || {}).map(function (id) { return KG.cardDef(state, id); }).filter(match);
    }
    const picks = shuffle(state, source).slice(0, 3);
    if (!picks.length) {
      KG.log(state, (a.from === 'deck' ? '卡组里' : '该卡池里') + '没有可供开发的牌', 'draw');
      return;
    }
    const opts = picks.map(function (c) { return { value: c.id, label: c.name + '（' + (c.cost || 0) + 'K）' }; });
    const chosen = await KG.ask(ctx.chooser, { kind: 'discover', prompt: a.prompt || '选择一张牌加入手牌', options: opts, state: state });
    const id = (typeof chosen === 'string' && picks.some(function (c) { return c.id === chosen; })) ? chosen : picks[0].id;
    // 注意：从卡组开发是"复制"，卡组里那张不消失（只有"抽一张XX"才会把卡组里的牌取走）
    if (a.thenSummon) {
      // 开发后直接进场（卡面："并使其加入战场"）
      const toFront = a.to === 'frontline' && KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
      if (toFront || p.support.length < KG.RULES.supportMax) {
        const u = KG.makeUnit(state, id, ctx.owner);
        u.summonedTurn = state.turn;
        u.zone = toFront ? 'frontline' : 'support';
        u.canAct = !!u.kws.blitz;
        if (toFront) state.frontline.push(u); else p.support.push(u);
        KG.applyAlpine(state, u);
        KG.log(state, p.name + ' 将 ' + KG.cardDef(state, id).name + ' 直接加入战场', 'play');
        if (a.killAtTurnEnd) { u.killAtTurnEnd = true; }
        return;
      }
    }
    if (p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(id));
    KG.log(state, p.name + ' 开发了 ' + KG.cardDef(state, id).name, 'draw');
  };
  // 兼容旧数据：早期「开发」写的是 `discover`（语义相同）。新卡面一律产出 `develop`。
  OPS.discover = OPS.develop;

  OPS.salvage = async function (state, ctx, a) {
    // 从弃牌堆随机取回
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    for (let i = 0; i < n && p.discard.length; i++) {
      const k = Math.floor(state.rng() * p.discard.length);
      const id = p.discard.splice(k, 1)[0];
      if (p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(id));
      KG.log(state, p.name + ' 打捞了 ' + KG.cardDef(state, id).name, 'keyword');
    }
  };
  OPS.fury = async function (state, ctx, a) {
    const u = refUnit(state, ctx, a.target || 'self');
    if (u) { u.attack += num(a.amount == null ? 1 : a.amount, state, ctx); }
  };

  /* ---------------------------------------------------- 卡组 / 手牌 / 修正 */
  function deckOf(state, ctx, side) { return state.players[sideIdx(state, ctx, side || 'self')]; }

  /* ★ 洗切（2026-10-02 Alan 定义）：本身没有效果，是一种**触发词** ——
   *   任何「洗入卡组」（shuffleIn / shuffleInUntil / shuffleRandomSet / shuffleHandIntoDeck）
   *   或「洗切卡组」的动作执行成功后，派发一次 shuffle 事件（卡面「友方洗切卡组时，…」监听它）。
   *   ⚠ 开局准备阶段（mulligan / beginTurn）的洗牌**不算**——那不是效果造成的。 */
  function fireShuffle(state, p) {
    if (typeof KG.runTrigger === 'function') KG.runTrigger(state, { trigger: 'shuffle', owner: p.idx, source: null });
  }

  OPS.shuffleIn = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    let id = a.cardId || (a.name != null && a.name !== '' ? pickCardIdByName(state, a.name, !!a.random) : null);
    if (!id && a.filter) {
      // 按条件从卡池里随机取一张（"将随机数量的联合国国家的卡牌洗入卡组"）
      const cands = Object.keys(KG.pool || {}).map(function (k) { return KG.pool[k]; }).filter(function (c) {
        if (c.referenceCard) return false;
        if (a.filter.set && c.set !== a.filter.set) return false;
        if (a.filter.cardType && c.cardType !== a.filter.cardType) return false;
        if (a.filter.unitType && c.unitType !== a.filter.unitType) return false;
        /* ★ nameIncludes / keyword（Alan 09-28「将一张随机进攻洗入卡组」靠 set；这两个是顺手补的：
         *   「洗入一张随机闪击单位」「洗入一张研发」这类按名字/词条筛的句式也要能走通） */
        if (a.filter.nameIncludes && String(c.name || '').indexOf(a.filter.nameIncludes) < 0) return false;
        if (a.filter.keyword) {
          const kw = a.filter.keyword;
          const has = (c.kwMap && c.kwMap[kw]) || (c.keywords || []).indexOf(kw) >= 0;
          if (!has) return false;
        }
        return true;
      });
      if (cands.length) id = cands[Math.floor(state.rng() * cands.length)].id;
    }
    if (!id && a.from === 'discard' && p.discard.length) {
      id = p.discard[Math.floor(state.rng() * p.discard.length)];
    }
    if (!id) {
      // 随机张数：count 为 null 且写了 range 时
      if (a.range) {
        const k = 1 + Math.floor(state.rng() * num(a.range, state, ctx));
        const cands = Object.keys(KG.pool || {});
        for (let i = 0; i < k && cands.length; i++) {
          const pick = cands[Math.floor(state.rng() * cands.length)];
          if (a.to === 'top') p.deck.unshift(pick); else p.deck.push(pick);
        }
        KG.log(state, p.name + ' 洗入了 ' + k + ' 张随机卡牌', 'deck');
        fireShuffle(state, p);
      }
      return;
    }
    for (let i = 0; i < n; i++) {
      if (a.to === 'top') p.deck.unshift(id); else p.deck.push(id);
    }
    KG.log(state, p.name + ' 将 ' + n + ' 张「' + KG.cardDef(state, id).name + '」洗入卡组', 'deck');
    fireShuffle(state, p);
  };
  OPS.shuffleInUntil = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const target = num(a.deckSize == null ? 39 : a.deckSize, state, ctx);
    let guard = 0;
    const id = opCardId(state, ctx, a);
    if (!id) return;
    while (p.deck.length < target && guard++ < 80) p.deck.push(id);
    KG.log(state, p.name + ' 的卡组被补充至 ' + p.deck.length + ' 张', 'deck');
    fireShuffle(state, p);
  };
  OPS.removeDeckTop = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    for (let i = 0; i < n && p.deck.length; i++) {
      const id = p.deck.shift();
      if (a.to === 'hand' && p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(id));
      else if (a.to === 'removed') p.removed.push(id);
      else p.discard.push(id);
    }
    KG.log(state, p.name + ' 从卡组顶移除了 ' + n + ' 张牌', 'deck');
  };
  OPS.deckToField = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const toFront = a.to === 'frontline' && KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
    const zone = toFront ? 'frontline' : 'support';
    let id = null, scan = 0;
    while (p.deck.length && scan++ < p.deck.length + 1) {
      const cand = p.deck[0];
      const d = KG.cardDef(state, cand);
      if (d.cardType === 'unit' && (!a.filter || (!a.filter.unitType || d.unitType === a.filter.unitType) && (!a.filter.maxCost || (d.cost || 0) <= a.filter.maxCost))) { id = p.deck.shift(); break; }
      p.deck.shift(); p.deck.push(cand);
      if (scan > 30) break;
    }
    if (!id) { KG.log(state, '卡组中没有符合条件的单位', 'deck'); return; }
    const canFront = zone === 'frontline' && KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
    if (!canFront && p.support.length >= KG.RULES.supportMax) { p.discard.push(id); return; }
    const u = KG.makeUnit(state, id, p.idx);
    u.summonedTurn = state.turn;
    if (a.keyword) FX.grantKw(state, u, a.keyword);
    if (a.buff) FX.applyBuff(state, u, num(a.buff.attack, state, ctx), num(a.buff.defense, state, ctx));
    u.canAct = !!u.kws.blitz;
    const realZone = canFront ? 'frontline' : 'support';
    u.zone = realZone;
    if (realZone === 'frontline') state.frontline.push(u); else p.support.push(u);
    KG.applyAlpine(state, u);
    ctx.summoned = [u];   // ★ 供后续动作引用（「将一张X加入阵线，使其获得…」的"其"= 刚加入的这张）
    KG.recomputeAuras(state);   // ★ 进场后重算光环
    // ★ 拉进场的单位也要广播「加入战场」（见 OPS.summon 里的说明）
    await KG.runTrigger(state, { trigger: 'unitDeployed', owner: p.idx, source: u, unit: u, unitType: u.unitType, name: u.name });
    KG.log(state, p.name + ' 将卡组中的 ' + u.name + ' 加入' + (realZone === 'frontline' ? '前线' : '支援阵线'), 'play');
  };
  OPS.deckToHand = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    let moved = 0;
    for (const cand of p.deck.slice()) {
      if (moved >= n || state.over) break;
      const d = KG.cardDef(state, typeof cand === 'object' ? cand.id : cand);
      if (a.filter && ((a.filter.cardType && d.cardType !== a.filter.cardType) || (a.filter.set && d.set !== a.filter.set) || (a.filter.maxCost != null && (d.cost || 0) > a.filter.maxCost))) continue;
      if (!p.deck.includes(cand)) continue;
      await KG.drawCard(state, p, false, { entry: cand, extra: true });
      moved++;
    }
    if (moved) KG.log(state, p.name + ' 从卡组抽取了 ' + moved + ' 张牌', 'draw');
  };
  OPS.enemyDeckToHand = async function (state, ctx, a) {
    const foe = state.players[1 - ctx.owner];
    if (!foe.deck.length) return;
    const id = foe.deck[0];
    const p = state.players[ctx.owner];
    await KG.drawCard(state, p, false, { deck: foe.deck, entry: id, extra: true });
    KG.log(state, p.name + ' 获得了对手卡组顶的 ' + KG.cardDef(state, typeof id === 'object' ? id.id : id).name, 'draw');
  };
  OPS.drawUntil = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const target = num(a.handSize == null ? 7 : a.handSize, state, ctx);
    let guard = 0;
    while (p.hand.length < target && guard++ < 20) await KG.drawCard(state, p, false);
  };
  OPS.noDrawNextTurn = async function (state, ctx, a) {
    deckOf(state, ctx, a.side || 'enemy').noDrawNextTurn = true;
  };
  // 「下个（敌方）回合无法增加指挥点槽」→ 跳过该玩家下一回合的自然增长（+1 槽）
  OPS.noKreditSlotNextTurn = async function (state, ctx, a) {
    deckOf(state, ctx, a.side || 'enemy').noKreditSlotNextTurn = true;
  };
  OPS.opCostMod = async function (state, ctx, a) {
    const amt = num(a.amount == null ? -1 : a.amount, state, ctx);
    const apply = function (u) {
      u.mods.opCostMod += amt;
      u.dynMods = u.dynMods || [];
      u.dynMods.push({ mod: 'opCostAdd', value: amt });
    };
    if (a.target) selectUnits(state, ctx, a.target).forEach(apply);
    else allUnits(state, a.side || 'self', ctx).forEach(apply);
  };
  OPS.setOpCost = async function (state, ctx, a) {
    const v = num(a.value == null ? 0 : a.value, state, ctx);
    selectUnits(state, ctx, a.target).forEach(function (u) { u.instOpCostSet = v; });
  };
  OPS.globalOpCostMod = async function (state, ctx, a) {
    deckOf(state, ctx, a.side || 'self').opCostModGlobal += num(a.amount, state, ctx);
  };
  OPS.setKreditSlots = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.maxKredits = Math.min(KG.RULES.kreditSlotHardCap, Math.max(0, num(a.value, state, ctx)));
    p.kredits = Math.min(p.kredits, p.maxKredits);
  };
  OPS.loseKreditSlots = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    p.maxKredits = Math.max(0, p.maxKredits - num(a.amount == null ? 1 : a.amount, state, ctx));
    p.kredits = Math.min(p.kredits, p.maxKredits);
  };
  OPS.loseKredits = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    p.kredits = Math.max(0, p.kredits - num(a.amount == null ? 1 : a.amount, state, ctx));
  };
  OPS.nextTurnKredits = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.nextTurnKredits = (p.nextTurnKredits || 0) + num(a.amount == null ? 1 : a.amount, state, ctx);
  };
  OPS.nextTurnSlots = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.nextTurnKreditSlots = (p.nextTurnKreditSlots || 0) + num(a.amount == null ? 1 : a.amount, state, ctx);
  };
  // 「下个友方回合开始时额外抽N张牌」（engine.beginTurn 会结算并在日志里说明）
  OPS.nextTurnDrawPending = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.nextTurnDraws = (p.nextTurnDraws || 0) + num(a.amount == null ? 1 : a.amount, state, ctx);
  };
  /* 「下一个XX回合开始时，<一组动作>」—— 控制流原语（与 conditional 同级，后面能衔接多个操作）。
   * 把 actions 排进 state.pendingTurnStarts，引擎在该玩家**下一个**回合开始时执行一次并移除。
   * a.side：'self'（默认）= 自己的下一个回合开始时；'enemy' = 对方的下一个回合开始时。
   * 与 nextTurnDrawPending / nextTurnKredits 这类"写死效果"的区别：动作列表任意组合。
   * 结算点在 engine.js beginTurn（runTrigger('turnStart') 之前）。 */
  OPS.nextTurnStart = async function (state, ctx, a) {
    const acts = a.actions || [];
    if (!acts.length) return;
    const idx = (a.side === 'enemy') ? 1 - ctx.owner : ctx.owner;
    state.pendingTurnStarts = state.pendingTurnStarts || [];
    state.pendingTurnStarts.push({ player: idx, actions: acts, ctxOwner: ctx.owner, queuedTurn: state.turn });
    KG.log(state, '已排定：下一个' + (idx === ctx.owner ? '友方' : '敌方') + '回合开始时生效', 'effect');
  };
  OPS.hqEnchant = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.hqEnchants.push({
      name: a.name || (ctx.card && ctx.card.name) || '总部效果',
      effects: a.effects || [], card: ctx.card, source: ctx.unit || null,
      untilTurn: a.untilTurnEnd ? ((state.players[p.idx].turnCount || state.turn) + 2) : null,
      // ★ once：「下一个…」类一次性附魔 —— 触发一次后自动移除（runTrigger 派发处处理）
      once: !!a.once,
    });
    KG.log(state, p.name + ' 的总部获得效果：' + (a.name || '') + (a.once ? '（一次性）' : ''), 'keyword');
  };
  // 「抑制总部」= 移除挂在总部上的效果（例如 Winter防空塔 给总部的附魔）
  OPS.suppressHQ = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    const had = (p.hqEnchants || []).length;
    p.hqEnchants = [];
    Object.keys(p.hqKws || {}).forEach(function (k) { if (p.hqKwsTemp && p.hqKwsTemp[k]) delete p.hqKws[k]; });
    KG.log(state, p.name + ' 的总部被抑制（移除了 ' + had + ' 个总部效果）', 'keyword');
  };
  /* ── 牌Q 卡包（2026-09-25）新原语 ────────────────────────────────────── */

  /* 「将一个单位转换为“X”」—— 制作者口径：转换就是**把一张卡变成别的卡**。
   *   ⚠ 只换卡面定义与基础身材，**已获得的永久修正按差值保留**
   *     （否则"被强化过的单位一转就回原形"，玩家会觉得把 buff 吞了）。 */
  OPS.transformUnit = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target);
    const id = opCardId(state, ctx, a);
    if (!id) { KG.log(state, '要变成的卡不在卡池里：' + (a.name || a.cardId || '?'), 'error'); return; }
    const def = KG.cardDef(state, id);
    units.forEach(function (u) {
      if (!u || u.dead) return;
      const oldName = u.name;
      const atkBonus = (u.attack || 0) - (u.baseAttack || 0);
      const defBonus = (u.maxDefense || 1) - (u.baseDefense || 1);
      u.cardId = id; u.def = def; u.name = def.name;
      u.baseAttack = def.attack || 0; u.baseDefense = def.defense || 1;
      u.attack = Math.max(0, u.baseAttack + atkBonus);
      u.maxDefense = Math.max(1, u.baseDefense + defBonus);
      u.defense = u.maxDefense;
      u.cardType = def.cardType; u.unitType = def.unitType || null;
      const km = Object.assign({}, def.kwMap || {});
      if (km.valor) { km.fury = true; delete km.valor; }      // 「狂怒/奋战」归一（同 engine.makeUnit）
      u.kws = km;
      u.kwValues = Object.assign({}, def.kwValues || {});
      u.ambushReady = !!km.ambush;
      u.shieldReady = !!km.shield;
      KG.log(state, oldName + ' 转换为 ' + def.name, 'keyword');
    });
    if (KG.recomputeAuras) KG.recomputeAuras(state);
  };

  /* 「使敌方所有指令造成的伤害-1」—— 给某玩家记一个"来自指令的伤害"整体修正（engine.damageUnit 读取） */
  OPS.orderDamageMod = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    p.orderDamageMod = (p.orderDamageMod || 0) + num(a.amount == null ? -1 : a.amount, state, ctx);
    KG.log(state, p.name + ' 的指令伤害修正 ' + p.orderDamageMod, 'keyword');
  };

  /* 「随机将卡组中 N 张X移至卡组顶」 */
  OPS.deckToTop = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    const hit = [];
    for (let i = 0; i < p.deck.length && hit.length < n; i++) {
      const inst = p.deck[i];
      const d = KG.cardDef(state, inst && inst.id ? inst.id : inst);
      if (!d) continue;
      if (a.filter) {
        if (a.filter.cardType && d.cardType !== a.filter.cardType) continue;
        if (a.filter.unitType && d.unitType !== a.filter.unitType) continue;
        if (a.filter.set && d.set !== a.filter.set) continue;
        if (a.filter.nameIncludes && String(d.name || '').indexOf(a.filter.nameIncludes) < 0) continue;
      }
      hit.push(i);
    }
    const movedIds = [];
    hit.slice().sort(function (x, y) { return y - x; }).forEach(function (i) {
      const x = p.deck.splice(i, 1)[0];
      p.deck.unshift(x);
      movedIds.push(x && x.id ? x.id : x);
    });
    ctx.lastMoved = movedIds;
    if (hit.length) KG.log(state, '将 ' + hit.length + ' 张牌移到卡组顶', 'draw');
  };

  /* 「指向一个单位」—— 把选中的目标**记在施法单位身上**，之后用 {sel:'ref',ref:'remembered'} 引用
   *   （交易田本连队：部署：指向一个单位 / 亡计：将其消灭）。 */
  OPS.rememberTarget = async function (state, ctx, a) {
    const u = refUnit(state, ctx, a.holder || 'self') || ctx.unit;
    const t = selectUnits(state, ctx, a.from || a.pick)[0];
    if (!u || !t) { KG.log(state, '没有可记住的目标', 'error'); return; }
    u.remembered = { uid: t.uid, name: t.name };
    KG.log(state, u.name + ' 指向了 ' + t.name, 'keyword');
  };

  /* 「触发山地效果」—— 重跑一次山地/协力的部署结算（布洛尔放良5大队） */
  OPS.triggerMountain = async function (state, ctx, a) {
    const u = refUnit(state, ctx, a.target || 'self') || ctx.unit;
    if (!u) return;
    if (KG.applyDeployTraits) await KG.applyDeployTraits(state, u);
    if (KG.recomputeAuras) KG.recomputeAuras(state);
  };

  /* 「将友方手牌洗入卡组，抽取等量卡牌，对敌方总部造成等同于洗入的海军牌数的伤害」（制胜之招）
   *   把手牌全部洗回卡组，并把两张计数记进 ctx 供后续动作用（num 读 shuffledCount / shuffledNavy）。 */
  OPS.shuffleHandIntoDeck = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = p.hand.length;
    let navy = 0;
    p.hand.forEach(function (h) {
      const d = KG.cardDef(state, h.id);
      if (d && (d.unitType === 'cruiser' || d.unitType === 'landcruiser' || d.set === '海军')) navy++;
      p.deck.push(h.id);
    });
    p.hand = [];
    ctx.shuffledCount = (ctx.shuffledCount || 0) + n;
    ctx.shuffledNavy = (ctx.shuffledNavy || 0) + navy;
    if (KG.shuffle) KG.shuffle(state, p.deck);
    KG.log(state, p.name + ' 把手牌（' + n + ' 张，其中海军 ' + navy + ' 张）洗入卡组', 'shuffle');
    fireShuffle(state, p);
  };

  /* ── 牌Q 卡包（2026-09-25）第二批新原语 ───────────────────────────── */

  /* 「使所有友方山地单位以随机顺序与敌方所有单位进行战斗」（制作者口径：随机打一个，没死就继续打） */
  OPS.fightRepeatedly = async function (state, ctx, a) {
    const side = sideIdx(state, ctx, a.side || 'self');
    const mine = selectUnits(state, ctx, a.target || { sel: 'all', side: 'friendly', filter: { keyword: 'alpine' } });
    shuffle(state, mine);
    for (const u of mine) {
      if (!u || u.dead) continue;
      let guard = 0;
      while (!u.dead && guard++ < 30) {
        const foes = allUnitsOf(state, 1 - side).filter(function (x) { return !x.dead; });
        if (!foes.length) break;
        const t = foes[Math.floor(state.rng() * foes.length)];
        await KG.damageUnit(state, t, u.attack, u, { attacker: u, combat: true });
        if (!t.dead && !u.dead) await KG.damageUnit(state, u, t.attack, t, { attacker: t, combat: true });
      }
    }
  };

  /* 「移除敌方卡组中花费不大于 N 的卡」（UfG-3010-1）—— 把符合条件的牌移出对局（进弃牌堆） */
  OPS.removeFromEnemyDeck = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    let n = 0;
    for (let i = p.deck.length - 1; i >= 0; i--) {
      const d = KG.cardDef(state, p.deck[i].id || p.deck[i]);
      if (!d) continue;
      if (a.filter) {
        if (a.filter.maxCost != null && (d.cost || 0) > a.filter.maxCost) continue;
        if (a.filter.minCost != null && (d.cost || 0) < a.filter.minCost) continue;
        if (a.filter.cardType && d.cardType !== a.filter.cardType) continue;
      }
      p.discard.push(p.deck[i]); p.deck.splice(i, 1); n++;
    }
    if (n) KG.log(state, '移除 ' + p.name + ' 卡组中 ' + n + ' 张牌', 'destroy');
  };

  /* 「使敌方构筑中所有卡花费 +N」—— 给对手**卡组里现在这些牌**记费用修正 */
  OPS.deckCostModAll = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    const amt = num(a.amount == null ? 1 : a.amount, state, ctx);
    KG.materializeDeck(state,p).forEach(function (inst) {
      inst.costMod = (inst.costMod || 0) + amt;
    });
    KG.log(state, p.name + ' 卡组中所有卡花费 ' + (amt > 0 ? '+' : '') + amt, 'cost');
  };

  /* 「接下来两回合内无法增加指挥点槽」（压迫） */
  OPS.noKreditSlotForTurns = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'enemy');
    p.noKreditSlotTurns = Math.max(p.noKreditSlotTurns || 0, num(a.turns == null ? 2 : a.turns, state, ctx));
    KG.log(state, p.name + ' 接下来 ' + p.noKreditSlotTurns + ' 回合无法增加指挥点槽', 'keyword');
  };

  /* 「（攻击总部后）能再次行动」—— 把行动权还给这个单位 */
  OPS.refreshAction = async function (state, ctx, a) {
    const u = refUnit(state, ctx, a.target || 'self') || ctx.unit;
    if (!u || u.dead) return;
    u.actionsLeft = 1; u.canAct = true;
    KG.log(state, u.name + ' 可以再次行动', 'keyword');
  };

  /* 「（把卡组里的牌移到顶后）若其花费为奇数则使其花费 -N」—— 改的是**刚挪过的那几张** */
  OPS.modMovedCost = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const amt = num(a.amount == null ? -1 : a.amount, state, ctx);
    (ctx.lastMoved || []).forEach(function (id) {
      const d = KG.cardDef(state, id);
      if (!d) return;
      if (a.oddOnly && ((d.cost || 0) % 2 === 0)) return;
      if(id&&typeof id==='object')id.costMod=(id.costMod||0)+amt;
      else {const index=p.deck.indexOf(id);if(index<0)return;KG.materializeDeck(state,p);const inst=p.deck[index];inst.costMod=(inst.costMod||0)+amt;}
    });
  };

  /* 「（把这些牌）获得“使用时，…”」—— 手牌附魔：打出时结算（亚萍号 / 11号计划） */
  OPS.grantEffectToHand = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    let cands = p.hand.slice();
    if (a.count != null) cands = cands.slice(0, num(a.count, state, ctx));
    cands.forEach(function (h) {
      h.playEffects = (h.playEffects || []).concat([{ trigger: 'play', actions: a.effects || [] }]);
    });
    if (cands.length) KG.log(state, cands.length + ' 张手牌获得「使用时」效果', 'keyword');
  };

  /* 「将所有空中单位移除，并使其所有者抽取等量卡牌」（BGIT-5514） */
  OPS.scrapAirDraw = async function (state, ctx, a) {
    const lost = [0, 0];
    for (const oi of [0, 1]) {
      for (const u of allUnitsOf(state, oi).slice()) {
        const isAir = KG.isType(u, 'air') || ['fighter', 'spacefighter', 'bomber'].indexOf(u.unitType) >= 0;
        if (!isAir || u.dead) continue;
        lost[oi]++;
        await KG.killUnit(state, u, null);
      }
    }
    for (const oi of [0, 1]) {
      for (let i = 0; i < lost[oi]; i++) await KG.drawCard(state, state.players[oi]);
    }
    KG.log(state, '空中单位被移除，双方各抽等量牌', 'destroy');
  };

  /* 「使战场变为<天气>」—— **天气附加牌**本体（2K 薄雾 / 4K 狂风 / 6K 落雪）。
   *   天气是战场级状态：薄雾=所有单位攻击-1 · 狂风=空军无法攻击 · 落雪=每回合结束全体 1 伤；
   *   传 kind:'clear' 则转晴。 */
  OPS.setWeather = async function (state, ctx, a) {
    const kind = (a.kind === 'clear') ? null : (a.kind || null);
    state.weather = kind;
    const CNW = { mist: '薄雾', gale: '狂风', snow: '落雪' };
    KG.log(state, kind ? ('战场天气变为「' + CNW[kind] + '」') : '战场天气转为晴朗', 'keyword');
    if (KG.recomputeAuras) KG.recomputeAuras(state);
  };

  /* 「预报」—— 制作者口径（2026-09-25 二次修订）：随机给一张 **0 费天气引子**
   *   （蓝天/薄雾/狂风/落雪，四张功能相同 = "开发一张天气附加牌"）。
   *   ★ 引子本身不设置天气；真正的效果在「天气附加牌」（2K/4K/6K）上，由引子开发出来。 */
  OPS.forecast = async function (state, ctx, a) {
    const names = ['蓝天', '薄雾', '狂风', '落雪'];
    const cands = names.map(function (n) { return pickCardIdByName(state, n); }).filter(Boolean);
    if (!cands.length) { KG.log(state, '卡池里没有天气卡', 'error'); return; }
    const id = cands[Math.floor(state.rng() * cands.length)];
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    if (p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(id));
    KG.log(state, p.name + ' 预报到一张「' + KG.cardDef(state, id).name + '」（天气引子）', 'draw');
  };

  /* 「将其反制」：真正的反制由**打断消费**（consumeInterrupt）完成，这里只是让编译层有落点 */
  OPS.countered = async function (state, ctx, a) {
    KG.log(state, '反制生效', 'keyword');
  };

  OPS.hqKeyword = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.hqKws[a.keyword] = a.value == null ? true : a.value;
    // 只持续到该玩家下个回合开始（"一次性的效果"）
    if (a.untilTurnEnd) {
      p.hqKwsTemp = p.hqKwsTemp || {};
      p.hqKwsTemp[a.keyword] = true;
    }
    KG.log(state, p.name + ' 的总部获得词条：' + (KG.KEYWORDS[a.keyword] ? KG.KEYWORDS[a.keyword].cn : a.keyword) +
      (a.untilTurnEnd ? '（直到下个回合开始）' : ''), 'keyword');
  };
  // 「下一回合，随机弃两张牌」这类延迟效果
  OPS.delayNextTurn = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    p.delayed = p.delayed || [];
    p.delayed.push({ atTurn: (p.turnCount || state.turn) + 2, effects: a.effects || [{ op: 'discard', count: a.count || 1, mode: 'random', side: 'self' }] });
  };
  OPS.hqArmor = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    p.hqArmor = (p.hqArmor || 0) + num(a.amount == null ? 1 : a.amount, state, ctx);
  };
  OPS.hqMaxUp = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const n = num(a.amount == null ? 1 : a.amount, state, ctx);
    p.hqMax += n; p.hq += n;
    KG.log(state, p.name + ' 的总部防御力上限 +' + n, 'heal');
  };
  OPS.handToField = async function (state, ctx, a) {
    // 从手牌中选择一个单位直接放入阵线（"空投"类效果）
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const cands = [];
    p.hand.forEach(function (inst, i) {
      const d = KG.cardDef(state, inst.id);
      if (d.cardType !== 'unit') return;
      if (a.filter && a.filter.maxCost != null && (d.cost || 0) > a.filter.maxCost) return;
      if (a.filter && a.filter.unitType && d.unitType !== a.filter.unitType) return;
      cands.push({ value: i, label: d.name + '（' + d.cost + 'K ' + (d.attack || 0) + '/' + (d.defense || 0) + '）' });
    });
    if (!cands.length) { KG.log(state, '手牌中没有符合条件的单位', 'error'); return; }
    // ★ 牌Q 卡包：「**随机**将手中一张单位复制进入阵线」——mode:'random' 时问都不问（直接抽一张）
    const pick = (a.mode === 'random')
      ? cands[Math.floor(state.rng() * cands.length)].value
      : await KG.ask(ctx.chooser, { kind: 'target', prompt: a.prompt || '选择一张手牌单位放入前线', options: cands, state: state });
    const idx = typeof pick === 'number' ? pick : cands[0].value;
    const inst = p.hand.splice(idx, 1)[0];
    if (!inst) return;
    const toFront = a.to === 'frontline' && KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
    const zone = toFront ? 'frontline' : 'support';
    if (!toFront && p.support.length >= KG.RULES.supportMax) { p.hand.splice(idx, 0, inst); return; }
    const u = KG.makeUnit(state, inst.id, p.idx);
    u.zone = zone;
    u.summonedTurn = state.turn;
    u.canAct = !!u.kws.blitz;
    if (zone === 'frontline') state.frontline.push(u); else p.support.push(u);
    KG.applyAlpine(state, u);
    KG.recomputeAuras(state);   // ★ 进场后重算光环
    ctx.summoned = [u];   // ★ 供后续动作引用（「将一张X加入阵线，使其获得…」的"其"= 刚放入的这张）
    // ★ 拉进场的单位也要广播「加入战场」（见 OPS.summon 里的说明）
    await KG.runTrigger(state, { trigger: 'unitDeployed', owner: p.idx, source: u, unit: u, unitType: u.unitType, name: u.name });
    KG.log(state, p.name + ' 将手牌中的 ' + u.name + ' 直接放入' + (zone === 'frontline' ? '前线' : '支援阵线'), 'play');
    (a.removeKeywords || []).forEach(function (kw) { delete u.kws[kw]; if (kw === 'blitz') u.canAct = false; });
  };
  OPS.setHandCost = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    const apply = function (inst) {
      const d = KG.cardDef(state, inst.id);
      if (a.filter && a.filter.cardType && d.cardType !== a.filter.cardType) return false;
      if (a.filter && a.filter.unitType && d.unitType !== a.filter.unitType) return false;
      if (a.filter && a.filter.maxCost != null && (d.cost || 0) > a.filter.maxCost) return false;
      if (a.mode === 'set') inst.costSet = num(a.value == null ? 0 : a.value, state, ctx);
      else inst.costMod += num(a.amount == null ? -1 : a.amount, state, ctx);
      return true;
    };
    if (a.choose && p.hand.length) {
      // 由玩家选择要减费的手牌（界面会把这几张手牌标成可点选）
      const opts = p.hand.map(function (inst, i) {
        const d = KG.cardDef(state, inst.id);
        return { value: i, label: d.name + '（' + (d.cost || 0) + 'K）' };
      });
      const pick = await KG.ask(ctx.chooser, {
        kind: 'target', from: 'hand', prompt: a.prompt || '选择一张手牌减费',
        options: opts, owner: p.idx, state: state,
      });
      const inst = p.hand[typeof pick === 'number' ? pick : 0];
      if (inst && apply(inst)) KG.log(state, p.name + ' 使 ' + KG.cardDef(state, inst.id).name + ' 的花费 -' + Math.abs(num(a.amount == null ? -1 : a.amount, state, ctx)), 'cost');
      return;
    }
    let done = 0;
    for (const inst of p.hand) {
      if (done >= n) break;
      if (apply(inst)) done++;
    }
    KG.log(state, p.name + ' 修改了 ' + done + ' 张手牌的花费', 'cost');
  };

  OPS.fight = async function (state, ctx, a) {
    // "与一个敌方单位战斗"：双方同时互相造成攻击力伤害（不触发反击词条之外的东西）
    const src = refUnit(state, ctx, a.target || 'self') || ctx.unit;
    const targets = selectUnits(state, ctx, a.with || { sel: 'choose', side: 'enemy', filter: a.filter });
    const t = targets[0];
    if (!src || !t) { KG.log(state, '战斗目标不存在', 'error'); return; }
    KG.log(state, src.name + ' 与 ' + t.name + ' 交战', 'attack');
    await KG.damageUnit(state, t, src.attack, src, { attacker: src });
    if (!t.dead && !src.dead) await KG.damageUnit(state, src, t.attack, t, { attacker: t });
  };

  OPS.summonFromHand = OPS.handToField;
  // 「将三张火药机加入手牌，其花费减为0」：把最近加入手牌的那几张改成指定花费
  OPS.setLastAddedCost = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const n = num(a.count == null ? 1 : a.count, state, ctx);
    const val = num(a.value == null ? 0 : a.value, state, ctx);
    const list = (ctx.lastAdded || []).slice(-n);
    list.forEach(function (inst) { inst.costSet = val; });
    if (!list.length) {
      // 没记录就退化为"按名字找手牌里的这几张"
      const name = a.name || (ctx.card && ctx.card.name);
      p.hand.filter(function (h) { return !name || KG.cardDef(state, h.id).name.indexOf(name) >= 0; })
        .slice(0, n).forEach(function (h) { h.costSet = val; });
    }
    KG.log(state, '这些牌的花费变为 ' + val, 'cost');
  };
  // 「从手牌选择一张花费不大于7的单位，将其加入前线并使其失去闪击」
  OPS.handUnitToFrontline = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const maxCost = a.maxCost == null ? null : num(a.maxCost, state, ctx);
    const cands = p.hand.map(function (h, i) { return { h, i }; })
      .filter(function (x) {
        const d = KG.cardDef(state, x.h.id);
        return d.cardType === 'unit' && (maxCost == null || (d.cost || 0) <= maxCost);
      });
    if (!cands.length) { KG.log(state, '手牌里没有符合条件的单位', 'error'); return; }
    const pick = await KG.ask(ctx.chooser, {
      kind: 'target', from: 'hand', prompt: a.prompt || '选择一张手牌单位放到前线',
      options: cands.map(function (x) { return { value: x.i, label: KG.cardDef(state, x.h.id).name }; }),
      owner: p.idx, state: state,
    });
    const c = cands.find(function (x) { return x.i === pick; }) || cands[0];
    const d = KG.cardDef(state, c.h.id);
    const canFront = KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
    if (!canFront && p.support.length >= KG.RULES.supportMax) return;
    p.hand.splice(p.hand.indexOf(c.h), 1);
    const u = KG.makeUnit(state, d.id, p.idx);
    u.summonedTurn = state.turn;
    u.canAct = !!u.kws.blitz && false;
    if (a.loseBlitz !== false) { delete u.kws.blitz; }
    u.zone = canFront ? 'frontline' : 'support';
    if (canFront) state.frontline.push(u); else p.support.push(u);
    KG.applyAlpine(state, u);
    KG.log(state, p.name + ' 将手牌中的 ' + d.name + ' 放入' + (canFront ? '前线' : '支援阵线'), 'play');
  };
  // 「先让玩家选一张手牌，再对该张牌生效」→ 记录选择结果供后续动作引用
  // 「与一个敌方单位战斗」：双方按攻击力互伤（不消耗行动）
  OPS.fight = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target);
    const foe = units[0];
    // ★ 2026-09-22：攻击方**可以指定**（「将一张X加入支援阵线，并**使其**与一个随机敌方单位战斗」
    //   —— "其" = 刚召唤的那张，编译产出 `srcRef:'summoned'`）。不写就用效果来源（原行为不变）。
    const self = (a.srcRef ? refUnit(state, ctx, a.srcRef) : null) || ctx.unit || refUnit(state, ctx, 'self');
    if (!foe || !self) return;
    KG.log(state, self.name + ' 与 ' + foe.name + ' 战斗', 'play');
    const outgoing = KG.attackPowerAgainst(state, self, foe), incoming = KG.attackPowerAgainst(state, foe, self);
    state.__combatDamageBatch = [];
    await KG.damageUnit(state, foe, outgoing, self, { attacker: self, combat: true });
    await KG.damageUnit(state, self, incoming, foe, { attacker: foe, combat: true });
    await KG.flushCombatDamage(state);
    for (const u of [self, foe]) if (!u.dead) await KG.runTrigger(state, { trigger: 'friendlySurvived', owner: u.owner, source: u, survivor: u, chooser: ctx.chooser });
  };

  // 「使所有友方单位获得『亡计：…』」：把效果挂到单位上
  OPS.grantEffect = async function (state, ctx, a) {
    /* 手牌/卡组里的牌获得效果（2026-09-26）：写预挂 effects，出场/打出时挂到单位身上。
     *   hand → inst.mods.effects（playCard 打出时消费 → u.extraEffects）
     *   deck → cardMods[id].effects（makeUnit 出场时消费 → u.extraEffects）
     *   存储形态与 extraEffects 一致：{trigger, actions} */
    if (a.piles) {
      const p = state.players[sideIdx(state, ctx, a.side || 'self')];
      const blk = a.effect || { trigger: a.effectTrigger || 'death', actions: a.effects || [] };
      const hit = pileMatches(a.filter);
      /* 候选收集 → pickPiles 选取（全部/随机 N/玩家选手牌 N） */
      const handCand = a.piles === 'deck' ? [] : p.hand.filter(function (inst) { return hit(KG.cardDef(state, inst.id)); });
      const deckCand = a.piles === 'hand' ? [] : KG.materializeDeck(state,p).filter(function (id) { return hit(KG.cardDef(state, id)); });
      const sel = await pickPiles(state, ctx, a, p, handCand, deckCand);
      sel.hand.forEach(function (inst) {
        inst.mods = inst.mods || { attack: 0, defense: 0, keywords: [] };
        inst.mods.effects = inst.mods.effects || [];
        inst.mods.effects.push(blk);
      });
      sel.deck.forEach(function (id) {
        const m = id.mods = id.mods || { attack: 0, defense: 0, keywords: [] };
        m.effects = m.effects || [];
        m.effects.push(blk);
      });
      const n = sel.hand.length + sel.deck.length;
      KG.log(state, p.name + ' 的 ' + n + ' 张牌获得效果（' + (a.label || TRIGGER_CN[blk.trigger] || blk.trigger) + '）', 'keyword');
      return;
    }
    const units = selectUnits(state, ctx, a.target);
    units.forEach(function (u) {
      u.extraEffects = u.extraEffects || [];
      u.extraEffects.push(JSON.parse(JSON.stringify(a.effect || { trigger: a.effectTrigger || 'death', actions: a.effects || [] })));
    });
    KG.log(state, '授予 ' + units.length + ' 个单位效果：' + (a.label || ''), 'keyword');
  };

  // 「将随机数量的某阵营卡牌洗入卡组，最多 N 张」
  OPS.shuffleRandomSet = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const max = num(a.max == null ? 3 : a.max, state, ctx);
    const cnt = a.random !== false ? (1 + Math.floor(state.rng() * max)) : max;
    const pool2 = Object.keys(KG.pool || {}).map(function (id) { return KG.cardDef(state, id); })
      .filter(function (c) { return c.cardType === 'unit' && a.set && c.set === a.set && !c.referenceCard; });
    for (let i = 0; i < cnt && pool2.length; i++) {
      const c = pool2[Math.floor(state.rng() * pool2.length)];
      p.deck.push(c.id);
    }
    KG.log(state, p.name + ' 洗入了 ' + cnt + ' 张卡到卡组', 'draw');
    fireShuffle(state, p);
  };

  // 「获得随机对战词条」：从制作者给定的对战词条池里随机
  OPS.grantRandomCombatKw = async function (state, ctx, a) {
    const POOL = KG.COMBAT_KEYWORDS;
    const units = selectUnits(state, ctx, a.target);
    units.forEach(function (u) {
      const kw = POOL[Math.floor(state.rng() * POOL.length)];
      grantKw(state, u, kw, 1);
      KG.log(state, u.name + ' 获得随机对战词条：' + (KG.KEYWORDS[kw] ? KG.KEYWORDS[kw].cn : kw), 'keyword');
    });
  };

  // 「使手牌和卡组中的所有太空单位获得+3+3和随机对战词条」
  OPS.grantRandomKwToPiles = async function (state, ctx, a) {
    const POOL = KG.COMBAT_KEYWORDS;
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const bump = function (id) {
      const d = KG.cardDef(state, id);
      if (!d || d.cardType !== 'unit' || !KG.isType(d, 'space')) return;
      const kw = POOL[Math.floor(state.rng() * POOL.length)];
      p.pileKw = p.pileKw || {};
      (p.pileKw[id] = p.pileKw[id] || []).push(kw);
    };
    p.hand.forEach(function (h) { bump(h.id); });
    p.deck.forEach(function (x) { bump((x && x.id) ? x.id : x); });
    KG.log(state, p.name + ' 手牌与卡组中的太空单位获得随机对战词条', 'keyword');
  };
  // 「复制手牌」：把指定手牌复制一份加入手牌
  //   a.target 支持完整选择器：{sel:'random'|'all'|'chosen', side, filter:{maxCost,minCost,cardType,name}, count}
  // 「使<群体>行动花费 ±N」：按选择器作用于场上单位
  OPS.opCostModAll = async function (state, ctx, a) {
    const units = selectUnits(state, ctx, a.target || { sel: 'all', side: 'friendly' });
    units.forEach(function (u) {
      const amt = num(a.amount || 0, state, ctx);
      u.mods = u.mods || {};
      u.mods.opCostMod = (u.mods.opCostMod || 0) + amt;
      u.dynMods = u.dynMods || [];
      u.dynMods.push({ mod: 'opCostMod', value: amt });
    });
    if (units.length) KG.log(state, units.length + ' 个单位的行动花费 ' + (num(a.amount || 0, state, ctx) > 0 ? '+' : '') + num(a.amount || 0, state, ctx), 'cost');
  };

  // 「使一张手牌行动花费 ±N」：记在手牌实例上，部署后生效
  OPS.handOpCostMod = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    let cands = p.hand.slice();
    const f = a.filter || {};
    cands = cands.filter(function (h) {
      const d = KG.cardDef(state, h.id);
      if (!d) return false;
      if (f.cardType && d.cardType !== f.cardType) return false;
      if (f.maxCost != null && (d.cost || 0) > f.maxCost) return false;
      if (f.minCost != null && (d.cost || 0) < f.minCost) return false;
      return true;
    });
    if (!cands.length) return;
    const sel = (a.target && a.target.sel) || a.sel || 'random';
    const k = sel === 'all' ? cands.length : Math.max(1, num(a.count == null ? 1 : a.count, state, ctx));
    const picked = [];
    if (sel === 'all') picked.push.apply(picked, cands);
    else if (sel === 'chosen') picked.push(ctx.chosenHandInst || cands[0]);
    else for (let i = 0; i < k && cands.length; i++) picked.push(cands.splice(Math.floor(state.rng() * cands.length), 1)[0]);
    picked.slice(0, k).forEach(function (h) { h.opCostMod = (h.opCostMod || 0) + num(a.amount || 0, state, ctx); });
    KG.log(state, picked.length + ' 张手牌的行动花费 ' + (num(a.amount || 0, state, ctx) > 0 ? '+' : '') + num(a.amount || 0, state, ctx), 'cost');
  };

  OPS.copyHandCard = async function (state, ctx, a) {
    const spec = (a.target && typeof a.target === 'object') ? a.target : { sel: a.sel || 'random', side: a.side || 'self' };
    // ★ 来源手牌：`from:'chosen'` = 上一条 chooseHandCard 选中那张所在的**那个人**的手牌
    //   （「从敌方的三张手牌中选择一张，将其复制到友方手牌中」= 来源敌方、去处我方）。
    const srcOwner = (a.from === 'chosen' && ctx.chosenHandOwner != null) ? ctx.chosenHandOwner : null;
    const p = (srcOwner != null) ? state.players[srcOwner] : state.players[sideIdx(state, ctx, a.side || spec.side || 'self')];
    const dest = (srcOwner != null) ? state.players[sideIdx(state, ctx, a.side || 'self')] : p;
    const maxCost = (spec.filter && spec.filter.maxCost != null) ? spec.filter.maxCost : null;
    const minCost = (spec.filter && spec.filter.minCost != null) ? spec.filter.minCost : null;
    let cands = p.hand.slice();
    if (maxCost != null || minCost != null || (spec.filter && (spec.filter.cardType || spec.filter.name))) {
      cands = cands.filter(function (h) {
        const d = KG.cardDef(state, h.id);
        if (!d) return false;
        if (maxCost != null && (d.cost || 0) > maxCost) return false;
        if (minCost != null && (d.cost || 0) < minCost) return false;
        if (spec.filter.cardType && d.cardType !== spec.filter.cardType) return false;
        if (spec.filter.name && String(d.name).indexOf(spec.filter.name) < 0) return false;
        return true;
      });
    }
    if (!cands.length) { KG.log(state, '没有可复制的手牌', 'draw'); return; }
    const k = spec.sel === 'all' ? cands.length : Math.max(1, num(spec.count == null ? 1 : spec.count, state, ctx));
    const picked = [];
    if (spec.sel === 'all') picked.push.apply(picked, cands);
    else if (spec.sel === 'chosen') { const c0 = ctx.chosenHandInst; picked.push(c0 && cands.indexOf(c0) >= 0 ? c0 : cands[0]); }
    else for (let i = 0; i < k && cands.length; i++) picked.push(cands.splice(Math.floor(state.rng() * cands.length), 1)[0]);
    picked.slice(0, k).forEach(function (src) {
      const inst = KG.makeHandInst(src.id, { costMod: src.costMod });
      inst.costSet = src.costSet;
      inst.opCostMod = src.opCostMod;
      inst.trueCopy = true;                       // 复制体（供"复制"相关效果识别）
      if (dest.hand.length < KG.RULES.handMax) KG.pushToHand(state, dest, inst);
      // 让后续的「然后将其转换为X」作用在**复制体**上（复制体在 dest 手里）
      ctx.chosenHandInst = inst; ctx.chosenHandOwner = dest.idx;
      KG.log(state, dest.name + ' 复制了手牌中的 ' + KG.cardDef(state, src.id).name, 'draw');
    });
  };

  OPS.discardChosenHand = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const inst = ctx.chosenHandInst || p.hand[ctx.chosenHandIndex];
    if (!inst) return;
    const i = p.hand.indexOf(inst);
    if (i >= 0) p.hand.splice(i, 1);
    p.discard.push(inst.id);
    KG.log(state, p.name + ' 弃掉了手牌中的 ' + KG.cardDef(state, inst.id).name, 'discard');
  };
  OPS.chooseHandCard = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    if (!p.hand.length) return;
    // 「从敌方三张手牌中选择一张」：a.count = 拿给玩家看的候选张数（不足就全部），从中选一张。
    let idxs = p.hand.map(function (h, i) { return i; });
    const n = a.count == null ? idxs.length : Math.max(1, Math.min(num(a.count, state, ctx), idxs.length));
    if (n < idxs.length) {
      for (let i = 0; i < n; i++) {                       // 抽 n 张（不重复），再按手牌顺序给出去
        const j = i + Math.floor(state.rng() * (idxs.length - i));
        const t = idxs[i]; idxs[i] = idxs[j]; idxs[j] = t;
      }
      idxs = idxs.slice(0, n).sort(function (x, y) { return x - y; });
    }
    const pick = await KG.ask(ctx.chooser, {
      kind: 'target', from: 'hand', prompt: a.prompt || '选择一张手牌',
      options: idxs.map(function (i) { return { value: i, label: KG.cardDef(state, p.hand[i].id).name }; }),
      owner: p.idx, state: state,
    });
    // ⚠ 选中的必须是**候选里那一张**：越界/非法选择退回第一张候选（原来是硬取 0 = 手牌第一张）。
    ctx.chosenHandIndex = (typeof pick === 'number' && p.hand[pick] && idxs.indexOf(pick) >= 0) ? pick : idxs[0];
    ctx.chosenHandInst = p.hand[ctx.chosenHandIndex];
    // 记住"选的是谁的手牌"：后续的「将其转换为…」必须操作**同一个人**的手牌
    //   （「从敌方三张手牌中选择一张，将其转换为…」若不记，transformHandCard 会默认操作自己的手牌）
    ctx.chosenHandOwner = p.idx;
  };

  OPS.buffCardsInPiles = async function (state, ctx, a) {
    // 给手牌和/或卡组中的卡牌永久附魔（打出时生效）
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const atk = num(a.attack, state, ctx), def = num(a.defense, state, ctx);
    const kws = a.keyword ? [a.keyword] : (a.keywords || []);
    const matches = pileMatches(a.filter);   // 2026-09-26 抽公共（原内联版逻辑一致，含牌Q minCost）
    // ★ 「获得减1花费」「减1行动花费」：手牌直接改实例（costMod / opCostMod），卡组记进 cardMods，
    //   抽到/打出时由 drawCard / makeUnit 应用。以前只支持身材与词条 → 这两项被静默丢掉。
    const cost = a.cost == null ? 0 : num(a.cost, state, ctx);
    const opCost = a.opCost == null ? 0 : num(a.opCost, state, ctx);
    if (!atk && !def && !kws.length && !cost && !opCost) { KG.log(state, '没有可应用的强化', 'error'); return; }
    /* 候选收集 → pickPiles 选取（全部/随机 N/玩家选手牌 N，2026-09-26「随机 or 指向」） */
    const handCand = a.piles === 'deck' ? [] : p.hand.filter(function (inst) { return matches(KG.cardDef(state, inst.id)); });
    const deckCand = a.piles === 'hand' ? [] : KG.materializeDeck(state,p).filter(function (id) { return matches(KG.cardDef(state, id)); });
    const sel = await pickPiles(state, ctx, a, p, handCand, deckCand);
    sel.hand.forEach(function (inst) {
      inst.mods = inst.mods || { attack: 0, defense: 0, keywords: [] };
      inst.mods.attack += atk; inst.mods.defense += def;
      kws.forEach(function (k) { if (inst.mods.keywords.indexOf(k) < 0) inst.mods.keywords.push(k); });
      if (cost) inst.costMod = (inst.costMod || 0) + cost;
      if (opCost) inst.opCostMod = (inst.opCostMod || 0) + opCost;
    });
    sel.deck.forEach(function (id) {
      const m = id.mods = id.mods || { attack: 0, defense: 0, keywords: [] };
      m.attack += atk; m.defense += def;
      kws.forEach(function (k) { if (m.keywords.indexOf(k) < 0) m.keywords.push(k); });
      if (cost) id.costMod = (id.costMod || 0) + cost;
      if (opCost) id.opCostMod = (id.opCostMod || 0) + opCost;
    });
    KG.log(state, p.name + ' 的 ' + (sel.hand.length + sel.deck.length) + ' 张牌获得永久强化', 'buff');
  };
  OPS.buffDeckAndHand = OPS.buffCardsInPiles;

  OPS.chooseFromHand = async function (state, ctx, a) {
    // 让玩家/对手从手牌里选一张（可按过滤），然后执行 follow（如弃掉/放入阵线/复制）
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    const cands = [];
    p.hand.forEach(function (inst, i) {
      const d = KG.cardDef(state, inst.id);
      if (a.filter) {
        if (a.filter.cardType && d.cardType !== a.filter.cardType) return;
        if (a.filter.unitType && d.unitType !== a.filter.unitType) return;
        if (a.filter.maxCost != null && (d.cost || 0) > a.filter.maxCost) return;
        if (a.filter.name && String(d.name).indexOf(a.filter.name) < 0) return;
      }
      cands.push({ value: i, label: d.name + '（' + (d.cost == null ? '?' : d.cost) + 'K）' });
    });
    if (!cands.length) { KG.log(state, '手牌里没有符合条件的牌', 'error'); return; }
    const pick = await KG.ask(ctx.chooser, { kind: 'target', from: 'hand', prompt: a.prompt || '选择一张手牌', options: cands, owner: p.idx, state: state });
    const idx = (typeof pick === 'number' && p.hand[pick]) ? pick : cands[0].value;
    const inst = p.hand[idx];
    if (!inst) return;
    ctx = Object.assign({}, ctx);
    ctx.chosenHandIndex = idx;
    ctx.chosenHandInst = inst;
    ctx.chosenHandCard = KG.cardDef(state, inst.id);
    const mode = ctx.chosenHandInst ? 'chosen' : (a.mode || 'keep');
    if (mode === 'discard') {
      p.hand.splice(idx, 1);
      p.discard.push(inst.id);
      KG.log(state, p.name + ' 弃掉了 ' + KG.cardDef(state, inst.id).name, 'discard');
    } else if (mode === 'chosen') {
      const d0 = KG.cardDef(state, inst.id);
      const pool2 = Object.keys(KG.pool || {}).map(function (id) { return KG.cardDef(state, id); })
        .filter(function (c) { return c.cardType === 'unit' && c.set === (a.set || 'USG') && !c.referenceCard; });
      if (pool2.length) {
        const pick = pool2[Math.floor(state.rng() * pool2.length)];
        inst.id = pick.id;
        KG.log(state, p.name + ' 把 ' + d0.name + ' 转换为 ' + pick.name, 'keyword');
      }
    } else if (mode === 'toField') {
      const toFront = a.to === 'frontline' && KG.frontlineOf(state, 1 - p.idx).length === 0 && KG.frontlineOf(state, p.idx).length < KG.effectiveFrontlineMax(state, p.idx);
      if (!toFront && p.support.length >= KG.RULES.supportMax) return;
      p.hand.splice(idx, 1);
      const zone = toFront ? 'frontline' : 'support';
      const u = KG.makeUnit(state, inst.id, p.idx);
      u.zone = zone; u.summonedTurn = state.turn; u.canAct = !!u.kws.blitz;
      if (zone === 'frontline') state.frontline.push(u); else p.support.push(u);
      KG.applyAlpine(state, u);
      KG.log(state, p.name + ' 将手牌中的 ' + u.name + ' 放入' + (zone === 'frontline' ? '前线' : '支援阵线'), 'play');
    } else if (mode === 'duplicate') {
      if (p.hand.length < KG.RULES.handMax) KG.pushToHand(state, p, KG.makeHandInst(inst.id));
      KG.log(state, p.name + ' 复制了 ' + KG.cardDef(state, inst.id).name, 'draw');
    }
    if (a.actions) await FX.exec(state, ctx, [{ trigger: null, actions: a.actions }], ctx.chooser, null);
  };

  OPS.grantMod = async function (state, ctx, a) {
    const list = a.target ? selectUnits(state, ctx, a.target) : allUnits(state, a.side || 'self', ctx);
    list.forEach(function (u) {
      u.mods = u.mods || {};
      u.dynMods = u.dynMods || [];
      const g = { mod: a.mod, value: a.value, unitType: a.unitType, attack: a.attack ? num(a.attack, state, ctx) : 0, damage: a.damage ? num(a.damage, state, ctx) : 0, takeDamage: a.takeDamage ? num(a.takeDamage, state, ctx) : 0, double: a.double,
        hasKw: a.mod === 'condAtk' ? (a.keyword || a.hasKw) : undefined,
        amount: a.mod === 'condAtk' ? (a.amount != null ? num(a.amount, state, ctx) : (a.attack ? num(a.attack, state, ctx) : 0)) : undefined,
        // ★ 「本回合内」的时长也记进 dynMods 本身：engine 的回合清理要按它摘掉这一条，
        //   否则 recomputeAuras 会从 dynMods 把 u.mods 又恢复回来（清了个寂寞）。
        untilTurn: a.duration === 'turn' ? state.turn : null };
      u.dynMods.push(g);
      if (a.mod === 'condAtk') {
        // ★ 「具有X词条时+N攻击力」：声明式记录，attackPowerAgainst 每攻现算（词条没了加成自动消失）
        u.mods.condAtk = (u.mods.condAtk || []).concat({ hasKw: a.keyword || a.hasKw, amount: a.amount != null ? num(a.amount, state, ctx) : (a.attack ? num(a.attack, state, ctx) : 0) });
      } else if (a.mod === 'vsType') {
        u.mods.vsType = u.mods.vsType || {};
        // ⚠ 族词（「陆军」）会产出**数组** unitType —— 直接当对象 key 会变成
        //   "infantry,tank,artillery" 一个字符串，按单兵种查永远查不到（加成不触发）。
        //   必须展开成多个单兵种 key。
        const ts = Array.isArray(a.unitType) ? a.unitType : [a.unitType || 'infantry'];
        ts.forEach(function (t) {
          u.mods.vsType[t] = u.mods.vsType[t] || {};
          if (a.attack) u.mods.vsType[t].attack = (u.mods.vsType[t].attack || 0) + num(a.attack, state, ctx);
          if (a.damage) u.mods.vsType[t].damage = (u.mods.vsType[t].damage || 0) + num(a.damage, state, ctx);
          if (a.double) u.mods.vsType[t].double = true;
          if (a.takeDamage) u.mods.vsType[t].takeDamage = (u.mods.vsType[t].takeDamage || 0) + num(a.takeDamage, state, ctx);
        });
      } else if (a.mod === 'vsHq') {
        // 「对战…和总部 +N攻击力」→ 打总部时的攻击加成
        u.mods.vsHq = (u.mods.vsHq || 0) + (a.attack ? num(a.attack, state, ctx) : 0);
      } else if (a.mod === 'takeDoubleFrom') {
        u.mods.takeDoubleFrom = u.mods.takeDoubleFrom || {};
        u.mods.takeDoubleFrom[a.unitType || 'order'] = true;
      } else if (a.mod === 'extraTypes') {
        u.extraTypes = u.extraTypes || [];
        if (u.extraTypes.indexOf(a.unitType) < 0) u.extraTypes.push(a.unitType);
      } else {
        u.mods[a.mod] = a.value == null ? true : a.value;
        // ★ 「本回合内，…」的常驻修正：登记到期时间，由 beginTurn 清理。
        //   不记的话这类修正**永久生效**（「本回合内，本单位无法攻击总部」变成永远不能打总部）。
        if (a.duration === 'turn') {
          u.turnMods = u.turnMods || [];
          u.turnMods.push({ mod: a.mod, untilTurn: (state.players[u.owner].turnCount || state.turn) + 2 });
        }
      }
    });
  };

  /* ── 「本单位可以在同一回合内移动并攻击」─────────────────────────────
   * 这是一个**独立原语**（拆不开）：它描述的是一条结算规则（移动不消耗行动），
   * 无法由现有原语组合表达 —— 引擎的「移动是否保留行动」判据必须有人去读。
   * 过去的错误做法是给卡偷偷加 extraTypes:['tank']，借道坦克的全局规则；
   * 副作用是那张卡对所有"针对坦克"的效果都变成了坦克。现在改成显式声明。
   * 等价写法：{"op":"grantMod","mod":"canMoveAndAttack","target":"self"}
   */
  OPS.canMoveAndAttack = async function (state, ctx, a) {
    const spec = a.target || (ctx.unit ? { sel: 'self' } : { sel: 'all', side: a.side || 'self' });
    selectUnits(state, ctx, spec).forEach(function (u) {
      u.mods = u.mods || {};
      u.dynMods = u.dynMods || [];
      u.mods.canMoveAndAttack = true;
      u.dynMods.push({ mod: 'canMoveAndAttack', value: true });
    });
  };

  OPS.transformHandCard = async function (state, ctx, a) {
    // 手牌归属：① 明确写了 side → 用它；② 上一条 chooseHandCard 选过牌 → 用**那个人的**手牌
    //   （「从敌方三张手牌中选择一张，将其转换为“补给”」就是这个情形）；③ 否则默认自己。
    const p = (ctx.chosenHandInst && ctx.chosenHandOwner != null)
      ? state.players[ctx.chosenHandOwner]
      : deckOf(state, ctx, a.side || 'self');
    if (!p.hand.length) return;
    // ★ 「将其转换为…」= 换掉**上一条 chooseHandCard 选中的那张**。
    //   原实现完全忽略选择、随机换一张手牌（「选择一张手牌，将其随机转换为一个USG单位」会换错牌）。
    let idx = -1;
    if (ctx.chosenHandInst) {
      const i = p.hand.indexOf(ctx.chosenHandInst);
      if (i >= 0) idx = i;
    }
    if (idx < 0 && a.target && a.target.ref === 'chosenHandCard' && ctx.chosenHandIndex != null && p.hand[ctx.chosenHandIndex]) {
      idx = ctx.chosenHandIndex;
    }
    if (idx < 0) idx = (a.mode === 'first') ? 0 : Math.floor(state.rng() * p.hand.length);
    // 目标卡：cardId / 卡名 → 指定那张（「转换为“补给”」）；否则按 filter/set 从卡池随机
    let pickId = a.cardId || null;
    if (!pickId && a.name) pickId = pickCardIdByName(state, a.name, false);
    if (!pickId) {
      const pool = Object.keys(KG.pool || {}).map(function (id) { return KG.cardDef(state, id); })
        .filter(function (c) {
          if (c.referenceCard) return false;
          if (a.set && c.set !== a.set) return false;
          if (a.filter && a.filter.cardType && c.cardType !== a.filter.cardType) return false;
          if (a.filter && a.filter.set && c.set !== a.filter.set) return false;
          if (a.filter && a.filter.maxCost != null && (c.cost || 0) > a.filter.maxCost) return false;
          return true;
        });
      if (!pool.length) { KG.log(state, '⚠ 卡池里没有可转换成的牌，该效果未生效', 'error'); return; }
      pickId = pool[Math.floor(state.rng() * pool.length)].id;
    }
    p.hand[idx] = KG.makeHandInst(pickId);
    KG.log(state, p.name + ' 将一张手牌转换为 ' + KG.cardDef(state, pickId).name, 'draw');
  };
  OPS.copyToDeck = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const src = a.of === 'enemy' ? state.players[1 - ctx.owner] : p;
    const id = a.cardId || (ctx.card && ctx.card.id);
    if (!id) return;
    if (a.halve) {
      // 数值减半的复制：动态创建一个变体定义
      const base = KG.cardDef(state, id);
      const vid = id + '__half';
      KG.pool[vid] = Object.assign({}, base, {
        id: vid, name: base.name + '（半）',
        attack: Math.floor((base.attack || 0) / 2), defense: Math.max(1, Math.floor((base.defense || 0) / 2)),
        kwMap: {}, keywords: [], effects: [], opCost: Math.floor((base.opCost || 0) / 2),
      });
      p.deck.unshift(vid);
    } else {
      p.deck.unshift(id);
    }
    KG.log(state, p.name + ' 将一张复制置入卡组顶', 'deck');
  };
  OPS.orderDoubleTurn = async function (state, ctx, a) {
    const p = state.players[sideIdx(state, ctx, a.side || 'self')];
    p.flags.orderDoubleTurn = true;
    KG.log(state, p.name + ' 本回合的指令效果翻倍', 'keyword');
  };
  OPS.endTurnNow = async function (state, ctx) {
    // "结束回合"（如 古洛亚第32团 部署：结束回合）
    state.forceEndTurn = true;
    KG.log(state, '立即结束回合', 'turn');
  };
  // 「获得随机对战词条」的实现在文件上方（搜索 OPS.grantRandomCombatKw）。
  //   此处原本还有一个**重复定义**，会覆盖上方那份，且池子里同时放了 fury 和 valor
  //   两个同义词条、还混进了 tsekep（硬铝弹，不是对战词条），已删除。
  //   修改该池子时请只改上面那一份。
  OPS.discardAll = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const keep = [];
    p.hand.forEach(function (inst) {
      const d = KG.cardDef(state, inst.id);
      const match = !a.filter || (
        (a.filter.cardType ? d.cardType === a.filter.cardType : true) &&
        (a.filter.name ? String(d.name).indexOf(a.filter.name) >= 0 : true) &&
        (a.filter.cardId ? d.id === a.filter.cardId : true) &&
        (a.filter.unitType ? d.unitType === a.filter.unitType : true));
      if (match) p.discard.push(inst.id); else keep.push(inst);
    });
    p.hand = keep;
    KG.log(state, p.name + ' 弃掉了手牌', 'discard');
  };
  OPS.discardFromDeck = async function (state, ctx, a) {
    const p = deckOf(state, ctx, a.side || 'self');
    const kept = [];
    p.deck.forEach(function (id) {
      const d = KG.cardDef(state, id);
      const match = !a.filter || (
        (a.filter.cardType ? d.cardType === a.filter.cardType : true) &&
        (a.filter.name ? String(d.name).indexOf(a.filter.name) >= 0 : true) &&
        (a.filter.cardId ? d.id === a.filter.cardId : true) &&
        (a.filter.unitType ? d.unitType === a.filter.unitType : true));
      if (match) p.discard.push(id); else kept.push(id);
    });
    p.deck = kept;
    KG.log(state, p.name + ' 的卡组被削减至 ' + p.deck.length + ' 张', 'discard');
  };
  // 「平均分配」：把 total 点伤害平均分给"选中的目标们 + 可选的总部"，
  //   拆成两个可组合的原语：
  //     · OPS.damageSplit —— 动作原语，按 spec 选一组目标，把 total 均分（余数给总部）
  //     · 参数 evenTo: 'hq'  表示"分母里也算上敌方总部一份"（白鲸计划）
  //   这样"平均分配"就不是硬编码的 20 点白鲸计划，而是可复用的原语。
  OPS.damageSplit = async function (state, ctx, a) {
    const total = num(a.total != null ? a.total : a.amount, state, ctx);
    const spec = a.target || { sel: 'all', side: 'enemy' };
    const units = selectUnits(state, ctx, spec);
    const foeIdx = spec.side === 'friendly' ? ctx.owner : 1 - ctx.owner;
    const foe = state.players[foeIdx];
    // 敌方没有单位 → 全部打总部
    if (!units.length) {
      if (a.evenTo === 'hq' || a.target) await KG.damageHQ(state, foe, total, ctx.card && ctx.card.name, ctx.source);
      return;
    }
    // 分母：是否把总部也算作一个"目标"
    const denom = units.length + (a.evenTo === 'hq' ? 1 : 0);
    const each = Math.floor(total / denom);
    let rem = total - each * denom;
    for (const u of units.slice()) {
      let d = each; if (rem > 0) { d++; rem--; }
      if (d > 0) await KG.damageUnit(state, u, d, ctx.source, { fromOrder: !!ctx.fromOrder });
    }
    if (a.evenTo === 'hq') {
      let hqD = each; if (rem > 0) { hqD++; rem--; }
      await KG.damageHQ(state, foe, hqD, ctx.card && ctx.card.name, ctx.source);
    } else if (rem > 0) {
      // 不把总部算进分母时，余数没有合法去处 → 记一条日志，避免静默吞掉伤害
      KG.log(state, '「平均分配」余数 ' + rem + ' 点无处安放（未把总部计入目标）', 'info');
    }
  };

  // 兼容别名：旧数据里的 conditionalRandomSplit = damageSplit(evenTo:'hq')
  OPS.conditionalRandomSplit = async function (state, ctx, a) {
    await OPS.damageSplit(state, ctx, Object.assign({}, a, { evenTo: 'hq' }));
  };

  /* ★ 防御力归零 = 被消灭。
   *   凡是**直接给 u.defense 赋值**的效果都必须过这一关：它们绕开了 damageUnit 的死亡判定，
   *   否则会留下"卡面显示 0 防御力却还站在场上"的僵尸单位（「失去防御力」原语报的就是这个）。
   *   典型调用者：OPS.setStats（「防御力设为 N」/「防御力等同于攻击力」）、OPS.heal 的身材互换。
   *
   *   ⚠ 反过来，**减法**路径不要调用：applyBuff / debuffTemp 是"减少 N 点防御力"，
   *     两者都有 `defense < 1 → 1` 的下限保护 —— 按设计**不会**因此致死（这是有意的），
   *     把这里加进去会误杀一堆本该活着的单位。
   */
  async function killIfNoDefense(state, u, source) {
    if (!u || u.dead) return false;
    if (!(u.defense <= 0)) return false;
    await KG.killUnit(state, u, source || null);
    return true;
  }
  FX.killIfNoDefense = killIfNoDefense;

  function applyBuff(state, u, atk, def) {    if (atk) { u.permAtk = (u.permAtk || 0) + atk; u.attack += atk; }
    if (def) {
      u.permDef = (u.permDef || 0) + def;
      u.maxDefense += def;
      u.defense += def;
      if (u.defense < 1) u.defense = 1;
    }
  }
  FX.applyBuff = applyBuff;

  function grantKw(state, u, kw, value) {
    if (!kw) return;
    // ★ 「狂怒 / 奋战」是同一个词条：不管从哪条路径（原语 grant、随机词条、卡面 kwMap）
    //   拿到，一律归一为 fury，避免出现同一单位只带 valor 而某处只判 fury 的漏网情况。
    if (kw === 'valor') kw = 'fury';
    /* ★ 烟幕禁则（制作者 2026-09-29）：**前线单位**和**具有守护/固守的单位**不能拥有烟幕。
     *   这里是写入侧的闸门 —— 光环/效果/随机词条想给这两类单位发烟幕，直接发不出来。
     *   （战斗机那条同款禁则制作者明确说不做，别加。）
     *   已经戴着的烟幕由 engine 的 enforceSmokeBans（挂在 recomputeAuras 末尾）清扫，
     *   两侧配合才不会有漏网路径。 */
    if (kw === 'smokescreen' && KG.smokeBanned && KG.smokeBanned(u)) return;
    u.kws[kw] = true;
    // 行动次数由词条决定，授予当回合立即生效（例如「本回合获得奋战」）
    if (kw === 'fury') u.actionsLeft = Math.max(u.actionsLeft || 0, 2);
    if (kw === 'ambush') u.ambushReady = true;
    if (kw === 'shield') { u.shieldReady = true; u.shield = 1; }
    if (kw === 'magnetic') {
      u.magneticCharges = Math.max(u.magneticCharges || 0, value || 1);
      u.kwValues.magnetic = u.magneticCharges;
    }
    if (kw === 'blitz' && u.summonedTurn === state.turn) u.canAct = true;
    if ((kw === 'armor' || kw === 'lightArmor') && value) u.kwValues[kw] = (u.kwValues[kw] || 0) + value;
  }
  FX.grantKw = grantKw;

  function findCardIdByName(state, name) {
    if (!name || !KG.pool) return null;
    const clean = String(name).replace(/[“”"「」]/g, '').trim();
    // OCR 与卡图转写常漏掉型号与兵种之间的空格：
    // 「BS-20A1坦克」和卡池中的「BS-20A1 坦克」应视为同一明确卡名。
    const compact = clean.replace(/\s+/g, '').toLowerCase();
    if (KG.aliases && KG.aliases[clean] && KG.pool[KG.aliases[clean]]) return KG.aliases[clean];
    const ids = Object.keys(KG.pool);
    for (const id of ids) if (KG.pool[id].name === clean) return id;
    for (const id of ids) {
      const n = String(KG.pool[id].name || '').replace(/[“”"「」]/g, '');
      const nCompact = n.replace(/\s+/g, '').toLowerCase();
      if (n === clean || nCompact === compact || n.indexOf(clean) >= 0 || clean.indexOf(n) >= 0) return id;
    }
    // 别名表里可能存的是相对简称
    if (KG.aliases) {
      for (const k in KG.aliases) {
        if (clean.indexOf(k) >= 0 || k.indexOf(clean) >= 0) {
          const id = KG.aliases[k];
          if (KG.pool[id]) return id;
        }
      }
    }
    return null;
  }
  FX.findCardIdByName = findCardIdByName;

  // 「名称包含」匹配：卡面常写类别名（"黑盾单位""研发""Mk坦克"），精确/别名匹配找不到时用它兜底
  function findCardIdsContaining(state, pattern) {
    const clean = String(pattern == null ? '' : pattern).replace(/[“”"「」]/g, '').trim();
    if (!clean || !KG.pool) return [];
    const wantUnit = /单位$/.test(clean);
    const base = clean.replace(/(?:单位|卡牌|卡|牌)$/, '').trim() || clean;
    const ids = Object.keys(KG.pool);
    const compactBase = base.replace(/\s+/g, '').toLowerCase();
    let hits = ids.filter(function (id) {
      const name = String(KG.pool[id].name || '');
      return name.indexOf(base) >= 0 || name.replace(/\s+/g, '').toLowerCase().indexOf(compactBase) >= 0;
    });
    if (wantUnit) hits = hits.filter(function (id) { return KG.pool[id].cardType === 'unit'; });
    if (!hits.length && base !== clean) {
      hits = ids.filter(function (id) { return String(KG.pool[id].name || '').indexOf(clean) >= 0; });
    }
    return hits;
  }
  FX.findCardIdsContaining = findCardIdsContaining;

  // 按卡名取一张卡：精确/别名/模糊 → 名称包含；random=true 时在多个候选里随机取一张
  function pickCardIdByName(state, name, random) {
    if (name == null || name === '') return null;
    const cands = findCardIdsContaining(state, name);
    if (random && cands.length) return cands[Math.floor(state.rng() * cands.length)];
    return findCardIdByName(state, name) || cands[0] || null;
  }
  FX.pickCardIdByName = pickCardIdByName;

  // 找不到卡时记一条明确日志，避免"静默失效"
  function resolveCardId(state, ctx, name) {
    if (name == null || name === '') {
      KG.log(state, '⚠ 这条效果没写清楚是哪张卡（缺少卡名），该效果未生效', 'error');
      return null;
    }
    const id = findCardIdByName(state, name);
    if (!id) KG.log(state, '⚠ 卡池中找不到「' + name + '」，该效果未生效', 'error');
    return id;
  }
  FX.resolveCardId = resolveCardId;

  // 卡牌操作（加入手牌 / 召唤 / 洗入卡组）统一入口：
  //   cardId 优先 → a.self（"复制一张自己"）→ 卡名（精确/别名/模糊/名称包含）→ 记错误日志
  function opCardId(state, ctx, a) {
    if (a.cardId) return a.cardId;
    if (a.self && ctx.card && ctx.card.id) return ctx.card.id;
    // 兼容老数据：引号里的卡名有时被放进了 filter.name
    const nm = ((a.name == null || a.name === '') && a.filter && a.filter.name) ? a.filter.name : a.name;
    // ★ 没有具体卡名、但有 filter（「随机Mk坦克」退化成 {unitType:'tank', nameIncludes:'Mk'}）
    //   → 从卡池里按 filter 筛一张。这就是"给某个兵种/系列建一个卡池"的实现。
    if (nm == null || nm === '') {
      if (a.filter && Object.keys(a.filter).length) {
        const fid = pickCardIdByFilter(state, a.filter, !!a.random);
        if (fid) return fid;
        KG.log(state, '⚠ 卡池里没有符合「' + JSON.stringify(a.filter) + '」的卡，该效果未生效', 'error');
        return null;
      }
      KG.log(state, '⚠ 这条效果没写清楚是哪张卡（缺少卡名），该效果未生效', 'error');
      return null;
    }
    const id = pickCardIdByName(state, nm, !!a.random);
    if (!id) KG.log(state, '⚠ 卡池中找不到「' + nm + '」，该效果未生效', 'error');
    return id;
  }
  FX.opCardId = opCardId;

  // 按 filter 从卡池筛一张（随机 or 第一张）。filter 支持：
  //   cardType / unitType(单值或族数组) / set / setIn / nameIncludes / maxCost / minCost
  /* ★★ 「体系」（2026-09-22 制作者提出）——
   *   卡牌的一个**元数据字段** `system`，与 attack / cost / unitType **同级**，
   *   在制卡台里直接填写；**不进 DSL、也不进对战词条（keywords）**。
   *   它是"作战体系 / 家族"这一维度：比卡名粗、比兵种细。
   *   例：Mk坦克 / WgL战斗机 / 黑盾 / 航母 / 伞兵。
   *   ⚠ 卡面写「部署两辆 Mk坦克」时要**引用体系**（匹配所有属于该体系的卡），
   *     而不是按卡名去卡池里找一张叫"Mk坦克"的卡（那样根本找不到）。
   *   `system` 允许是字符串（单体系）或数组（多体系）。 */
  function cardHasSystem(c, sys) {
    if (!c || !sys) return false;
    const s = c.system;
    if (s == null || s === '') return false;
    return Array.isArray(s) ? s.indexOf(sys) >= 0 : String(s) === String(sys);
  }
  FX.cardHasSystem = cardHasSystem;
  // 卡池里出现过的所有体系名（编译器识别"体系词"时用；也供 UI 做下拉候选）
  FX.systemWords = function (pool) {
    const src = pool || KG.pool || {};
    const list = Array.isArray(src) ? src : Object.keys(src).map(function (k) { return src[k]; });
    const out = {};
    list.forEach(function (c) {
      if (!c || c.referenceCard || c.system == null || c.system === '') return;
      (Array.isArray(c.system) ? c.system : [c.system]).forEach(function (s) {
        if (s) out[String(s).trim()] = true;
      });
    });
    return Object.keys(out);
  };

  function pickCardIdByFilter(state, filter, random) {
    // ⚠ KG.pool / state.pool 是 **id→卡 的对象**（不是数组）——直接 .filter 会抛
    //   "pool.filter is not a function"（zbk1母舰的召唤曾因此整条效果失败）。
    const src = state.pool || KG.pool || {};
    const pool = Array.isArray(src) ? src : Object.keys(src).map(function (k) { return src[k]; });
    const list = pool.filter(function (c) {
      if (!c) return false;
      if (c.referenceCard) return false;      // ★ 词条参照卡（说明卡）不进任何"按兵种/系列"卡池
      if (filter.cardType && c.cardType !== filter.cardType) return false;
      if (filter.unitType) {
        const types = Array.isArray(filter.unitType) ? filter.unitType : [filter.unitType];
        if (!types.some(function (t) { return KG.isType(c, t); })) return false;
      }
      if (filter.set && c.set !== filter.set) return false;
      if (filter.setIn && filter.setIn.indexOf(c.set) < 0) return false;
      // ★ 体系（见 cardHasSystem 的说明）
      if (filter.system && !cardHasSystem(c, filter.system)) return false;
      if (filter.nameIncludes && String(c.name || '').indexOf(filter.nameIncludes) < 0) return false;
      if (filter.name && String(c.name || '').indexOf(filter.name) < 0) return false;
      if (filter.maxCost != null && (c.cost || 0) > filter.maxCost) return false;
      if (filter.minCost != null && (c.cost || 0) < filter.minCost) return false;
      return true;
    });
  if (!list.length) return null;
  return (random ? list[Math.floor(state.rng() * list.length)] : list[0]).id;
  }
  FX.pickCardIdByFilter = pickCardIdByFilter;

  /* 在**卡组里**按 filter 选一张（addCardToHand from:'deck' 用）。
   * 与 pickCardIdByFilter 的区别：候选来自 p.deck（内容与顺序在开局时由 seeded 洗牌锁定，
   * 两边一致），不依赖卡池序 —— 「从卡组抽一张X」语义上就该在卡组里挑，池里有不代表卡组有。 */
  function pickDeckCardIdByFilter(state, p, filter, random) {
    const seen = {}, uniq = [];
    (p.deck || []).forEach(function (id) {
      if (seen[id]) return;
      seen[id] = 1; uniq.push(id);
    });
    const list = uniq.filter(function (id) {
      const c = KG.cardDef(state, id);
      if (!c || c.referenceCard) return false;
      if (filter.cardType && c.cardType !== filter.cardType) return false;
      if (filter.unitType) {
        const types = Array.isArray(filter.unitType) ? filter.unitType : [filter.unitType];
        if (!types.some(function (t) { return KG.isType(c, t); })) return false;
      }
      if (filter.set && c.set !== filter.set) return false;
      if (filter.setIn && filter.setIn.indexOf(c.set) < 0) return false;
      if (filter.system && !cardHasSystem(c, filter.system)) return false;
      if (filter.nameIncludes && String(c.name || '').indexOf(filter.nameIncludes) < 0) return false;
      if (filter.name && String(c.name || '').indexOf(filter.name) < 0) return false;
      if (filter.maxCost != null && (c.cost || 0) > filter.maxCost) return false;
      if (filter.minCost != null && (c.cost || 0) < filter.minCost) return false;
      return true;
    });
    if (!list.length) return null;
    return random ? list[Math.floor(state.rng() * list.length)] : list[0];
  }
  FX.pickDeckCardIdByFilter = pickDeckCardIdByFilter;

  /* ------------------------------------------------------------------ 执行 */
  FX.performAction = async function (state, ctx, action) {
    const fn = OPS[action.op];
    if (!fn) throw new Error('未知动作：' + action.op);
    // Draw phases belong to each actual card draw and its recipient, rather
    // than the enclosing multi-card action or the effect's author.
    if (action.op === 'draw' || action.op === 'drawOne') return fn(state, ctx, action);
    return KG.performBehavior(state, 'op', action.op, { owner: ctx.owner, source: ctx.source || ctx.unit || null,
      hqOwner: ctx.hqOwner,
      unit: ctx.unit || ctx.source || null, card: ctx.card, played: ctx.card, chooser: ctx.chooser,
      action, amount: action.amount, value: action.value, targetSpec: action.target }, () => fn(state, ctx, action));
  };
  FX.exec = async function (state, ctx, effects, chooser, trigger) {
    ctx.chooser = chooser;
    const list = ctx._preFiltered ? (effects || []).slice() : (effects || []).filter(function (e) {
      if (!trigger) return !e.trigger || e.trigger === 'order' || e.trigger === 'passive';
      return e.trigger === trigger;
    });
    // 先解析目标（仅一次，覆盖所有同触发效果）
    const withTargets = list.filter(function (e) { return (e.targets || []).length; });
    if (withTargets.length && !ctx._targetsResolved) {
      await FX.resolveDeclaredTargets(state, ctx, list, chooser);
      ctx._targetsResolved = true;
    }
    for (const ef of list) {
      if (state.over) return;
      if (ef.firstEvent && (!ctx.event || (ef.firstEvent === 'turn' ? ctx.event.turnOrdinal : ctx.event.ordinal) !== 1)) continue;
      /* ★★ 充能门控（词条「充能X」的"有条件"形态）：
       *   `充能完毕后，<某事件>，…` 编译出来带 chargeGate —— 它不是"到点就执行"，
       *   而是**充能完毕后就绪，等那个事件发生**才执行**一次**；
       *   执行后该效果即失去，并**重新开始 X 回合的计时**。
       *   （无条件的 `充能完毕时，…` 走 chargeNow，由 engine 到点直接触发，不经过这里。） */
      const chargedEffect = ef.chargeGate || ef.trigger === 'chargeNow';
      const gateU = chargedEffect ? (ctx.source || ctx.unit) : null;
      const chargeEffects = gateU ? (gateU.silenced ? [] : ((gateU.def || {}).effects || [])).concat(gateU.extraEffects || []).filter(function (e) { return e.chargeGate || e.trigger === 'chargeNow'; }) : [];
      const chargeKey = chargeEffects.indexOf(ef) >= 0 ? String(chargeEffects.indexOf(ef)) : (ef.id || ef.trigger + '|' + list.indexOf(ef));
      if (chargedEffect) {
        /* ⚠ 用 `ctx.unit || ctx.source`：**监听型触发**（如"友方单位攻击时"）的 ctx.unit
         *   是**事件里那个单位**（攻击者），而充能挂在**效果所属的那张卡**上 ——
         *   之前只读 ctx.unit 就去查 chargeReady，永远读到 undefined → **门控永远挡住**
         *   （用户报的"充能完毕后触发不了效果"）。 */
        // ⚠ 必须**优先 ctx.source**（= 效果所属的那张卡）；
        //   ctx.unit 在监听型触发里是**事件里的单位**（比如攻击者），不是充能的那张卡。
        if (!gateU || !gateU.chargeReady) continue;   // 还没充能完 → 这次不执行
        if ((gateU.chargeConsumed || []).indexOf(chargeKey) >= 0) continue;
      }
      // 条件成立后才消耗次数与充能，未生效的监听不能锁死后续机会。
      const sub = Object.assign({}, ctx, { vars: Object.assign({}, ctx.vars) });
      if (ef.vars) Object.keys(ef.vars).forEach(function (k) { sub.vars[k] = num(ef.vars[k], state, sub); });
      if (!evalCond(state, sub, ef.condition)) {
        if (ef.else) await FX.exec(state, sub, [{ trigger: null, actions: ef.else }], chooser, null);
        continue;
      }
      // 每回合一次的限制
      const usageOwner = ctx.handInst || ctx.unit || ctx.enchant;
      if (ef.oncePerTurn && usageOwner) {
        usageOwner.turnFlags = usageOwner.turnFlags || {};
        const key = (ef.trigger || 'x') + '|' + (ef.id || (ctx._effectIndex ?? list.indexOf(ef)));
        if (usageOwner.turnFlags[key] === state.turn) continue;
        usageOwner.turnFlags[key] = state.turn;
      }
      // 整局只生效一次的限制（例如"第一次受伤后行动花费+3"）
      if (ef.once && usageOwner) {
        usageOwner.onceFlags = usageOwner.onceFlags || {};
        const key2 = (ef.trigger || 'x') + '|' + (ef.id || (ctx._effectIndex ?? list.indexOf(ef)));
        if (usageOwner.onceFlags[key2]) continue;
        usageOwner.onceFlags[key2] = true;
      }
      if (chargedEffect) {
        gateU.chargeConsumed = gateU.chargeConsumed || [];
        gateU.chargeConsumed.push(chargeKey);
        if (!gateU.chargeArmed && chargeEffects.every(function (_e, i) { return gateU.chargeConsumed.indexOf(String(i)) >= 0; })) {
          gateU.chargeReady = false;
          gateU.chargeConsumed = [];
          const cv0 = parseInt((gateU.kwValues || {}).charge, 10) || 1;
          gateU.chargeIn = cv0;
          if (KG.log) KG.log(state, gateU.name + ' 的充能效果全部触发，重新计时 ' + cv0 + ' 回合', 'keyword');
        }
      }
      const actions = ef.actions || [];
      const dbl = sub.orderDouble || (state.players[ctx.owner].flags.orderDoubleTurn && (trigger === 'order' || !trigger));
      const rounds = dbl ? 2 : 1;
      for (let r = 0; r < rounds; r++) {
        for (const a of actions) {
          if (state.over) return;
          // ── JS 脚本逃生舱：原语组合表达不了的效果，直接写 JS ──
          //    {"op":"script","script":"targets[0].attack = 0; log('...')"}
          //    也可写成 {"trigger":"attack","script":"..."}（没有 op 时按脚本处理）
          const scriptSrc = a.script || (a.op === 'script' ? a.code : null);
          if (scriptSrc) {
            try {
              await KG.performBehavior(state, 'op', 'script', { owner: sub.owner, source: sub.source || sub.unit || null,
                unit: sub.unit || sub.source || null, card: sub.card, chooser: sub.chooser, action: a },
                () => runEffectScript(state, sub, a, scriptSrc));
            } catch (err) {
              KG.log(state, '[脚本效果出错] ' + err.message, 'error');
            }
            continue;
          }
          const fn = OPS[a.op];
          if (!fn) { KG.log(state, '[未实现的效果: ' + a.op + ']', 'error'); continue; }
          try {
            await FX.performAction(state, sub, a);
          } catch (err) {
            KG.log(state, '[效果执行出错 ' + a.op + '] ' + err.message, 'error');
          }
        }
      }
    }
  };

  /* ------------------------------------------------- 被动光环（静态效果） */
  function freshMods() {
    return {
      vsType: {}, takeDoubleFrom: {}, noAttackHQ: false, noAttackAir: false,
      immuneOrder: false, immuneUnitEffects: false, ignoreEnemyEffects: false,
      noPin: false, noSuppress: false, noRetal: false, opCostMod: 0, orderDamageDouble: false,
      pinOnAttack: false, extraDamageTaken: 0, extraDamageDealt: 0, immune: false,
      pierce: false, ignoreCombatKw: false, vsArmor: 0,
      // 「本单位可以在同一回合内移动并攻击」——由卡牌自身声明，engine.move 会读它
      canMoveAndAttack: false,
    };
  }
  FX.freshMods = freshMods;

  /* ================== 「常驻 / 光环」里的动作（持续型） ==================
   * 制作者口径（2026-09-27）：「常驻/光环也必须能够执行动作」。
   * 做法：持续型动作在**每轮 recomputeAuras** 里重新执行一次，落点全部是「每轮重建」的地方：
   *   attack/defense → 记进 auraAtk/auraDef（第 1 步先减掉 → 不叠加、来源没了就消失）
   *   词条           → 记进 auraKws / auraRmKws（第 1 步按原生词条还原）
   *   常驻修正       → 直接写 u.mods（第 2 步刚 u.mods = freshMods() → 不叠加）
   *   extraTypes     → 记进 __pExtra（第 1 步收回）
   * 一次性动作（damage / draw / destroy / …）**不**在这里执行 —— 每轮重来一次是事故而不是语义，
   * 它们该走「部署时 / 回合开始时」；检查器会点名并可一键改触发。
   */
  const PASSIVE_CONT_OPS = {
    buff: 1, buffAll: 1, grantMod: 1, grant: 1, grantAll: 1,
    removeKeyword: 1, opCostMod: 1, opCostModAll: 1, canMoveAndAttack: 1,
  };
  FX.PASSIVE_CONT_OPS = PASSIVE_CONT_OPS;    // 检查器 / 文档共用这一张名单
  /* 一条「常驻修正」记录 → 单位（dynMods 重放 与 常驻动作 共用，避免两套逻辑漂移） */
  function applyDynMod(u, g) {
    if(g.mod==='primitiveField') {
      if(g.property && !['__proto__','prototype','constructor'].includes(g.property))u.mods[g.property]=g.value;
      return;
    }
    if (!u || !g) return;
    u.mods = u.mods || {};
    if (g.mod === 'vsType') {
      const ts = Array.isArray(g.unitType) ? g.unitType : [g.unitType || 'infantry'];
      ts.forEach(function (t) {
        u.mods.vsType[t] = u.mods.vsType[t] || {};
        const addField = (field, val) => {
          if (val == null) return;
          const cur = u.mods.vsType[t][field];
          if (cur == null) u.mods.vsType[t][field] = val;
          else if (typeof cur === 'number' && typeof val === 'number') u.mods.vsType[t][field] = cur + val;
          else u.mods.vsType[t][field] = [].concat(cur).concat(val);
        };
        addField('attack', g.attack);
        addField('takeDamage', g.takeDamage);
      });
    } else if (g.mod === 'takeDoubleFrom') {
      u.mods.takeDoubleFrom = u.mods.takeDoubleFrom || {};
      u.mods.takeDoubleFrom[g.unitType || 'order'] = true;
    } else if (g.mod === 'condAtk') {
      // dynMods 重放一致性：没有这分支，recomputeAuras 重建 mods 时 condAtk 会丢
      u.mods.condAtk = (u.mods.condAtk || []).concat({ hasKw: g.hasKw || g.keyword, amount: g.amount || 0 });
    } else if (g.mod === 'vsHq') {
      u.mods.vsHq = (u.mods.vsHq || 0) + (g.attack || 0);
    } else if (g.mod === 'extraTypes') {
      u.extraTypes = u.extraTypes || [];
      if (u.extraTypes.indexOf(g.unitType) < 0) u.extraTypes.push(g.unitType);
    } else if (g.mod === 'opCostAdd') {
      u.mods.opCostMod = (u.mods.opCostMod || 0) + (g.value || 0);
    } else if (g.mod) {
      u.mods[g.mod] = g.value == null ? true : g.value;
    }
  }
  /* 执行一条**持续型**常驻动作；返回是否执行（false = 一次性动作，不在这里跑） */
  function applyContinuousAction(state, src, a, inherited) {
    const ctx = inherited || { owner: src.owner, unit: src, source: src, vars: {} };
    if (a && FX.composition && FX.composition.continuousOps[a.op]) {
      FX.composition.continuous(a, state, ctx, (legacy, scope) => applyContinuousAction(state, src, legacy, scope));
      return true;
    }
    if (!a || !PASSIVE_CONT_OPS[a.op]) return false;
    if (a.op === 'buff' || a.op === 'buffAll') {
      const atk = num(a.attack, state, ctx) || 0, def = num(a.defense, state, ctx) || 0;
      selectUnits(state, ctx, a.target).forEach(function (u) {
        if (u.mods && u.mods.immuneUnitEffects && u.owner !== src.owner) return;
        if (atk) { u.attack += atk; u.auraAtk = (u.auraAtk || 0) + atk; }
        if (def) { u.maxDefense += def; u.defense += def; u.auraDef = (u.auraDef || 0) + def; }
        if (a.keyword) { grantKw(state, u, a.keyword, a.value); (u.auraKws = u.auraKws || []).push(a.keyword); }
      });
      return true;
    }
    if (a.op === 'grant' || a.op === 'grantAll') {
      selectUnits(state, ctx, a.target).forEach(function (u) {
        grantKw(state, u, a.keyword, a.value);
        (u.auraKws = u.auraKws || []).push(a.keyword);
      });
      return true;
    }
    if (a.op === 'removeKeyword') {
      selectUnits(state, ctx, a.target).forEach(function (u) {
        delete u.kws[a.keyword];
        (u.auraRmKws = u.auraRmKws || []).push(a.keyword);
      });
      return true;
    }
    if (a.op === 'opCostMod' || a.op === 'opCostModAll') {
      const n = num(a.amount, state, ctx) || 0;
      selectUnits(state, ctx, a.target).forEach(function (u) { applyDynMod(u, { mod: 'opCostAdd', value: n }); });
      return true;
    }
    if (a.op === 'canMoveAndAttack') {
      selectUnits(state, ctx, a.target || { sel: 'self' }).forEach(function (u) { applyDynMod(u, { mod: 'canMoveAndAttack', value: true }); });
      return true;
    }
    if (a.op === 'grantMod') {
      const list = a.target ? selectUnits(state, ctx, a.target) : allUnits(state, a.side || 'self', ctx);
      const g = { mod: a.mod, property: a.property, value: a.value, unitType: a.unitType, double: a.double,
        attack: a.attack ? num(a.attack, state, ctx) : 0,
        damage: a.damage ? num(a.damage, state, ctx) : 0,
        takeDamage: a.takeDamage ? num(a.takeDamage, state, ctx) : 0,
        hasKw: a.mod === 'condAtk' ? (a.keyword || a.hasKw) : undefined,
        amount: a.mod === 'condAtk' ? (a.amount != null ? num(a.amount, state, ctx) : (a.attack ? num(a.attack, state, ctx) : 0)) : undefined };
      list.forEach(function (u) {
        applyDynMod(u, g);
        // extraTypes 不在 mods 里 → 单独记账，来源没了由第 1 步收回
        if (a.mod === 'extraTypes' && a.unitType) (u.__pExtra = u.__pExtra || []).push(a.unitType);
      });
      return true;
    }
    return false;
  }

  FX.recomputeAuras = function (state) {
    if (!state || !state.players) return;
    // 1) 清理旧光环
    for (const p of state.players) {
      KG.allUnitsOf(state, p.idx).forEach(function (u) {
        if(FX.composition)FX.composition.clearAura(u);
        if (u.auraAtk) { u.attack -= u.auraAtk; u.auraAtk = 0; }
        if (u.auraDef) { u.maxDefense -= u.auraDef; u.defense -= u.auraDef; u.auraDef = 0; }
        // ★ aura 授予/剥除的**词条记账**（2026-09-24）：aura.keyword 给的守护，来源阵亡要收回；
        //   aura.removeKeyword 剥掉的烟幕，来源阵亡要还回去（若卡面原生带）。
        //   原生词条（u.def.kwMap 自带）不误删/不重复恢复。
        (u.auraKws || []).forEach(function (k) {
          if (u.kws[k] && !(u.def.kwMap && u.def.kwMap[k])) delete u.kws[k];
        });
        u.auraKws = [];
        (u.auraRmKws || []).forEach(function (k) {
          if (u.def.kwMap && u.def.kwMap[k] && !u.kws[k]) { u.kws[k] = true; }
        });
        u.auraRmKws = [];
        // ★ 「常驻动作」发的 extraTypes：来源阵亡/被沉默后收回（每轮重建，用 __pExtra 记账）
        (u.__pExtra || []).forEach(function (t) {
          const pi = (u.extraTypes || []).indexOf(t);
          if (pi >= 0) u.extraTypes.splice(pi, 1);
        });
        u.__pExtra = [];
      });
    }
    // 2) 重算 mods：被动规则 -> 动态授予 -> 光环
    for (const p of state.players) {
      KG.allUnitsOf(state, p.idx).forEach(function (u) {
        u.mods = freshMods();
        (u.silenced ? [] : (u.def.effects || [])).concat(u.extraEffects || []).forEach(function (e) {
          // 被沉默的单位：卡面上的被动规则失效（"沉默"的语义就是抹掉卡面文字）
          if (e.passiveRules) {
            const rc = { owner: u.owner, unit: u, source: u, vars: {} };
            if (e.condition && !evalCond(state, rc, e.condition)) return;   // 条件化被动规则
            Object.assign(u.mods, e.passiveRules);
          }
          // ★ 编译器产物把"常驻修正"写成 `actions` 里的 `grantMod(target:self)` /
          //   `canMoveAndAttack(target:self)`（与手写的 `passiveRules` **等价**）。
          //   引擎在此统一应用 —— 否则这两种写法行为不一致：deploy 触发只在实际"打出"时执行，
          //   而 `makeUnit`（put/直接创建）不触发 deploy，能力就丢了（canMoveAndAttack 曾因此失效）。
          //   只处理**幂等的自目标常驻修正**，不碰带 unitType/attack 等细节的（那些走 dynMods）。
          (e.actions || []).forEach(function (a) {
            if (!a || a.target !== 'self') return;
            if (a.op === 'grantMod' && a.mod && !a.unitType && !a.attack && !a.double && !a.damage && !a.takeDamage) {
              u.mods[a.mod] = a.value == null ? true : a.value;
            } else if (a.op === 'canMoveAndAttack') {
              u.mods.canMoveAndAttack = true;
            }
          });
          // 条件化光环：条件不满足时把这张卡的增益撤掉（每张卡单独记账，避免互相干扰）
          if (e.aura && e.condition) {
            const rc2 = { owner: u.owner, unit: u, source: u, vars: {} };
            if (!evalCond(state, rc2, e.condition)) {
              u.__auraOff = u.__auraOff || {};
              u.__auraOff[u.def.id + '|' + (u.def.effects || []).indexOf(e)] = true;
            }
          }
        });
        (u.dynMods || []).forEach(function (g) { applyDynMod(u, g); });
      });
    }
    // 3) 应用光环
    for (const p of state.players) {
      KG.allUnitsOf(state, p.idx).forEach(function (src) {
        (src.silenced ? [] : (src.def.effects || [])).concat(src.extraEffects || []).forEach(function (e) {
          if (e.trigger !== 'passive') return;
          const auraCtx = { owner: src.owner, unit: src, source: src, vars: {} };
          if (e.condition && !evalCond(state, auraCtx, e.condition)) return;   // 条件化光环
          if (e.aura) {
            const a = e.aura;
            const targets = selectUnits(state, { owner: src.owner, unit: src }, Object.assign({ sel: 'all' }, a.target || {}));
            targets.forEach(function (u) {
              // ★ 抑制是**一次性清除**（2026-10-02 Alan 定稿）：清的那一刻光环加成也没了，
              //   但光环是持续效果 —— 抑制过后照常重新罩上来（= "重新贴膜，包括光环"）。
              //   所以这里**不**跳过被抑制的目标；抑制时点的清除由 OPS.silence 里的
              //   auraAtk/auraDef 归零 + 重算完成。
              if (u.mods && u.mods.immuneUnitEffects && u.owner !== src.owner) return;
              const atk = a.attack || 0, def = a.defense || 0;
              if (atk) { u.attack += atk; u.auraAtk = (u.auraAtk || 0) + atk; }
              if (def) { u.maxDefense += def; u.defense += def; u.auraDef = (u.auraDef || 0) + def; }
              if (a.keyword) { grantKw(state, u, a.keyword, a.value); (u.auraKws = u.auraKws || []).push(a.keyword); }
              // ★ aura.removeKeyword：「获得X并**失去**Y」的剥除半边（2026-09-24）。
              //   光环是每次重算的 → 剥除也幂等：词条回来了（如烟幕攻击后重新获得？）下一轮再剥。
              //   制作者口径：「下一个加入战场的友方单位获得守护并失去烟幕」改常驻光环后，
              //   once 附魔监听 unitDeployed 对 **summon 拉进场**的单位不触发（卫星计划实测不贴），
              //   光环走 recomputeAuras，部署/召唤/拉进场一视同仁。
              if (a.removeKeyword) {
                const rkw = a.removeKeyword;
                if (u.kws && u.kws[rkw]) {
                  delete u.kws[rkw];
                  KG.log(state, u.name + ' 的「' + (KG.KEYWORDS && KG.KEYWORDS[rkw] ? KG.KEYWORDS[rkw].cn : rkw) + '」被剥除', 'keyword');
                }
                (u.auraRmKws = u.auraRmKws || []).push(rkw);
              }
              // ★ 「友方战斗机行动花费-1」类光环：aura.opCost → mods.opCostMod（effOpCost 读）。
              //   第 2 步刚把 mods 重置为 freshMods()，这里再加一次即可，不会跨轮累积；
              //   来源阵亡/被沉默后本轮不再施加，减费自然消失。
              // ★ opCost 支持**动态值**（「场上每有一个Mk坦克，本单位具有减1行动花费」）——
              //   每轮 recompute 时用 num() 现算，单位增减立刻反映。
              if (a.opCost) u.mods.opCostMod = (u.mods.opCostMod || 0) + num(a.opCost, state, auraCtx);
              // ★ aura.mod：把「具有无视敌方指令」这类**常驻修正**也做成光环
              //   （mods 每轮重置后再加，来源阵亡/被沉默即失效）。
              if (a.mod) u.mods[a.mod] = a.value == null ? true : a.value;
              // ★ 「友方单位造成的对战伤害+1」「敌方单位造成的伤害-2」「友方单位受到的伤害-1」类光环。
              //   落点（engine.js 唯一消费点）：
              //     extraDamageDealt → attackPowerAgainst（攻击结算加算，"造成的伤害"）
              //     extraDamageTaken → damageUnit（受击结算加算，"受到的伤害"）
              //   安全性：第 2 步刚把每个单位的 mods 重置为 freshMods()，这里再加不会跨回合累积；
              //   来源单位阵亡/被沉默后 recomputeAuras 重跑，加成自然消失。
              //   combatDamage / dealtDamage 同义（对战伤害 = 单位攻击造成的伤害；指令伤害不在此列）。
              if (a.combatDamage || a.dealtDamage) {
                u.mods.extraDamageDealt = (u.mods.extraDamageDealt || 0) + (a.combatDamage || a.dealtDamage);
              }
              if (a.takenDamage) {
                u.mods.extraDamageTaken = (u.mods.extraDamageTaken || 0) + a.takenDamage;
              }
            });
          }
          /* ★★ 常驻 / 光环**也能执行动作**（制作者 2026-09-27：「常驻/光环也必须能够执行动作」）。
           *   持续型动作每轮重算重新执行一次（新进场单位自动吃到；来源阵亡/被沉默自动撤销、不叠加）；
           *   一次性动作（伤害/抽牌/消灭…）不在这里跑 —— 检查器会点名并可一键改成「部署时」。 */
          if (e.actions && e.actions.length) {
            const continuousCtx = {owner:src.owner,unit:src,source:src,vars:{}};
            e.actions.forEach(function (a) { applyContinuousAction(state, src, a, continuousCtx); });
          }
        });
      });
    }
    // HQ effects remain active after the card that attached them leaves play.
    for(const p of state.players)for(const enchant of p.hqEnchants||[])for(const e of enchant.effects||[]){
      if(e.trigger!=='passive')continue;
      const context={owner:p.idx,hqOwner:p.idx,enchant,source:null,card:enchant.card,vars:{}};
      if(e.condition&&!evalCond(state,context,e.condition))continue;
      for(const action of e.actions||[])applyContinuousAction(state,{owner:p.idx},action,context);
    }

  };

  if (typeof module !== 'undefined' && module.exports) module.exports = FX;
})(typeof window !== 'undefined' ? window : globalThis);
