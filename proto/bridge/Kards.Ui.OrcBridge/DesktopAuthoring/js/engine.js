/* ==========================================================================
 * KG Engine v2 —— KARDS 风格 DIY 对战引擎（浏览器 + Node 双跑）
 * v2 新增：手牌实例化(费用修正)、单位战斗修正(mods)、总部附魔(hqEnchants)、
 *          反制措施(counters)、临时增益、行动花费修正、老兵升级、卡组操作
 * ========================================================================== */
(function (global) {
  'use strict';

  const KG = global.KG = global.KG || {};

  /* ---------------------------------------------------------------- 规则常量 */
  const RULES = KG.RULES = {
    hqHp: 20,
    kreditCap: 12,          // 原版自然增长上限；卡牌效果仍可提高上限
    kreditSlotHardCap: 36,  // 卡牌效果把槽加上去的硬上限（可超过自然上限，但不超过 36）
    handP1: 4,
    handP2: 5,                 // 后手多一张（准备阶段补偿）
    drawPerTurn: 1,
    drawOnFirstTurn: true,   // 双方首回合都抽一张，保证先手有节奏、后手不多牌
    deckSize: 40,
    maxCopies: 4,
    supportMax: 6,
    frontlineMax: 5,          // 前线是双方共抢的一条线：每方最多 5 个（双方可同时在场，谁独占谁控制）
    handMax: 9,
    fatigue: true,
    // 「可从任意阵线直攻敌方总部」的兵种（火炮/空军/太空系）。
    // 注意：**只**用于 canAttack 的总部分支（engine.js 约 1350 行），不参与任何"对空军"的判定。
    // artillery 曾在名单里漏掉 → 火炮在支援阵线无法攻击总部，与自身报错文案和 lineRules 注释矛盾。
    hqRaidTypes: ['artillery', 'fighter', 'spacefighter', 'bomber', 'landcruiser', 'cruiser'],
    airTypes: ['fighter', 'spacefighter', 'bomber', 'landcruiser', 'cruiser'],
    noRetaliationTypes: ['artillery'],
    // 官方规则开关（详见 game/data/RULES_REF.md）
    lineRules: true,          // 支援线近战单位只能打敌方前线；炮兵/空军/太空可打任意战线
    opCostOnMove: true,       // 移动也要付行动花费
    tankMoveAndAttack: true,  // 坦克可以同回合先移动再攻击（各付一次行动花费）
    // 无视固守的兵种（制作者 2026-09-19 口径）：**只有炮兵 / 轰炸机 / 巡地舰**。
    //   ⚠ 巡航舰、太空战机**不能**无视固守 —— 旧名单按"太空单位"把这两个也放行了，是错的。
    //     两者仍然可以跨越阵线攻击 / 从支援线直攻总部（那由 isMelee 与 hqRaidTypes 决定，互不影响）。
    guardIgnoreTypes: ['artillery', 'bomber', 'landcruiser'],
    // 太空单位攻打陆军（步兵/坦克/火炮）时不会受到反击伤害
    spaceNoRetalVsLand: true,
    landTypes: ['infantry', 'tank', 'artillery'],
    guardSameLineOnly: true,  // 固守只保护"同一阵线"的目标（总部算支援阵线目标）
    guardAdjacentOnly: true,  // 固守只守护**左右相邻**的目标（制作者确认）：总部在支援线最左端，只有第一个固守守得住它
    // 不受反击的规则（RULES_REF 8.4 + 结算顺序）
    bomberNoRetalVsNonFighter: true,  // 轰炸机攻击非战斗机目标时不吃反击
    bomberDefenderNoRetal: true,      // 守方是轰炸机且攻方不是战斗机 → 守方不反击
    artilleryDefenderNoRetal: false,  // 炮兵防守时【照常反击】（制作者确认）
    secondPlayerBonusKredit: 0, // 后手首回合额外指挥点（默认不开；用 UI 的"先手/AI 难度"调节）
  };

  /* ------------------------------------------------------------------ 词条表 */
  const KEYWORDS = KG.KEYWORDS = {
    blitz:       { cn: '闪击',   desc: '可以在部署的当回合行动。' },
    guard:       { cn: '固守',   desc: '保护同一阵线左右相邻的单位或总部；炮兵、轰炸机和巡地舰可以无视。' },
    sponge:      { cn: '海绵装甲', desc: '受到伤害时减免（海绵装甲）。' },
    magnetic:    { cn: '磁反应装甲', desc: '抵挡一次攻击。' },
    //   ★ 制作者 2026-09-24 口径：烟幕**只是不能被攻击**。它不阻止被指定为目标 ——
    //     敌方指令、单位效果照样能指向它（消灭/造成伤害/压制…），唯独攻击打不到它。
    // ★ 禁则（制作者 2026-09-29，RULES_REF 12.4）：**前线单位**与**具有守护/固守的单位**
    //   不能拥有烟幕（已有的立刻失效）；战斗机那条同款禁则制作者明确不做。
    smokescreen: { cn: '烟幕',   desc: '不能被敌方攻击（但仍可被指令或单位效果指定）。它攻击或移动后烟幕解除。**位于前线**或**具有守护/固守**的单位不能拥有烟幕。' },
    intel:       { cn: '情报', desc: '让敌方 X 张手牌变成明牌（你能看到）；X 最多为 3。' },
    ambush:      { cn: '伏击',   desc: '每回合首次被攻击时，先对攻击者造成伤害（每回合最多触发一次）。' },
    mobilize:    { cn: '动员',   desc: '己方回合开始时获得 +1/+1；实际受到伤害后失去动员，保留已获得的增益。' },
    fury:        { cn: '狂怒',   desc: '此单位每回合可以攻击两次（与“奋战”同义），每次支付操作费；移动是否保留行动取决于兵种或明确的能力。' },
    deployment:  { cn: '部署',   desc: '从手牌部署本单位时触发；直接加入战场不会触发。' },
    deathrattle: { cn: '亡计',   desc: '此单位被消灭时触发。' },    repair:      { cn: '维修',   desc: '恢复单位的防御力。' },
    salvage:     { cn: '打捞',   desc: '在己方回合消灭敌方单位后，将其 1/1 副本加入手牌，部署费用最多为 3。' },
    intel:       { cn: '情报',   desc: '查看敌方手牌。' },
    pin:         { cn: '压制',   desc: '不能攻击或移动，持续到拥有者下一个回合结束。' },
    veteran:     { cn: '老兵',   desc: '满足条件后升级为老兵形态。' },
    // ★ 充能X：部署后 X 回合触发「充能完毕」。
    //   充能完毕会给单位贴一个**一次性效果**：效果触发一次后失去，然后重新计时 X 回合。
    //   效果写法：`充能完毕时，…` = 充能完毕**立即**执行（无条件）；
    //            `充能完毕后，<某事件>，…` = 充能完毕后**等该事件发生**才执行（有条件）。
    charge:      { cn: '充能',   desc: '部署 X 回合后「充能完毕」；充能完毕的效果触发一次后失去，并重新计时 X 回合。' },
    armor:       { cn: '重甲',   desc: '受到的**对战伤害**减少 N 点（指令与效果造成的伤害不减）。卡面特别声明「受到指令重甲减伤」的单位，其重甲对指令伤害同样生效。' },
    lightArmor:  { cn: '轻甲',   desc: '受到**步兵**造成的伤害减少 N 点。' },
    exile:       { cn: '流亡',   desc: '可按卡牌标注的流亡国家参与构筑；死亡或使用后正常进入弃牌堆。' },
    valor:       { cn: '奋战',   desc: '此单位每回合可以攻击两次（与“狂怒”同义），每次支付操作费；移动是否保留行动取决于兵种或明确的能力。' },
    impact:      { cn: '冲击',   desc: '首次攻击不会受到反击伤害（攻击后失去此词条）。' },
    guerrilla:   { cn: '游击',   desc: '可以返回上一阵线 / 返回手牌，且移动后仍可行动。' },
    shield:      { cn: '强磁护盾', desc: '受到的伤害 -1；单位攻击后失去此效果。' },
    magnetic:    { cn: '磁反应装甲', desc: '具有免疫层数 x，被攻击后 x-1，x 为 0 时失去（最多 2 层）。' },
    confiscate:  { cn: '收缴',   desc: '消灭敌方单位后转化为我方资源。' },
    revealed:    { cn: '明牌',   desc: '此牌对对手可见。' },
    airdrop:     { cn: '空投',   desc: '可以直接加入前线。' },
    sponge:      { cn: '海绵装甲', desc: '吸收伤害的装甲（DIY 词条）。' },
    tsekep:      { cn: '硬铝弹', desc: '攻击时无视被攻击单位的对战词条。' },
    // 原版词条：山地也在直接加入战场时结算；协力在打出时结算。
    alpine:    { cn: '山地', desc: '部署或加入战场时，每个其他友方山地单位使其获得 +1/+1。' },
    synergy:     { cn: '协力', desc: '本回合开始时场上没有同国家的友方单位时，打出本单位后总部受到一次疲劳（士气）伤害（与空牌库抽牌共用从 1 起递增的计数）。' },
    conceal:     { cn: '隐蔽', desc: '卡背入场，不受指令、反制和单位能力影响；操作费为 1；攻击或被攻击时揭露。' },
  };
  KG.COMBAT_KEYWORDS = Object.freeze(['ambush', 'blitz', 'fury', 'guard', 'armor', 'impact', 'smokescreen']);

  /* -------------------------------------------------------------------- 工具 */
  let _uid = 0;
  function nextUid(pfx) { return (pfx || 'u') + (++_uid); }
  KG.nextUid = nextUid;

  /* ★ 联机锁步要求 uid"同种子同卡组 → 逐字相同"。
   *   原来 _uid 是进程级自增：两个客户端各自加载页面、各自可能先玩过别的局，
   *   计数器起点不同 → 同一张牌在两边拿到不同的 uid（实测 u27 vs u28）→
   *   而动作包是靠 uid 指定"打谁/移动谁"的，对方就会指向一个不存在的单位。
   *   所以每局按种子把计数器归位：同种子 + 同动作序列 = 同 uid 序列。
   *   （再乘一个种子派生的偏移，避免同一页面里"重开一局"与上一局的 uid 撞车，
   *     否则 DOM/动画里残留的旧 uid 可能被新局的单位误命中。） */
  function resetUidBase(seed) {
    _uid = Math.abs((seed | 0) % 900000) * 10;
  }
  KG.resetUidBase = resetUidBase;

  function mulberry32(a) {
    return function () {
      a |= 0; a = a + 0x6D2B79F5 | 0;
      let t = Math.imul(a ^ a >>> 15, 1 | a);
      t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
      return ((t ^ t >>> 14) >>> 0) / 4294967296;
    };
  }
  KG.rng = { mulberry32 };

  function log(state, text, kind) {
    state.log.push({ turn: state.turn, text: text, kind: kind || 'info' });
    if (state.log.length > 500) state.log.shift();
  }
  KG.log = log;

  /* ★ 演出事件队列（给 UI 用）：把"非对战伤害"（单位效果 / 指令 / 疲劳造成的伤害）
   *   记下来，UI 渲染时消费它播"攻击动画 + 命中 + 飘伤害"。
   *   · 对战伤害（opts.combat）**不记** —— 那条路 doAttack/playAiAttackFX 已经播了冲刺，
   *     再记一次会播两遍。
   *   · 与 state.burnPops 同一套路；**不进 stateDigest**，联机双方各自消费、互不影响。 */
  function fxPush(state, ev) {
    if (!state || !ev) return;
    state.fxPops = state.fxPops || [];
    state.fxPops.push(ev);
    if (state.fxPops.length > 240) state.fxPops.splice(0, state.fxPops.length - 240);
  }
  KG.fxPush = fxPush;

  /* ---------------------------------------------------------------- 卡池访问 */
  KG.pool = null;
  /* 卡面级字段提升（2026-09-22「大规模轰炸」报）：
   *   落盘链（build/merge 脚本）会把 effects[].cardFields **固化到卡顶层**，但运行时
   *   没有任何代码做这件事 —— 制卡台保存的自定义卡，cardFields 只活在 effects 条目里
   *   （instCost 读 def.costModByStat 读不到 → 「降低等同于场上友方轰炸机最高攻击力的
   *   费用」这类动态费用**静默失效**；upgradeOn / targetsNeeded / drawOnTurn 等同理）。
   * 在进池时统一提升：effects[].cardFields 覆盖到顶层（卡面文本是唯一事实源，
   * 编辑器重新解析后的新值自然胜过旧的固化值）。幂等（__pf 标记），重复调用无害。 */
  function promoteCardFields(c) {
    if (!c || c.__pf || !Array.isArray(c.effects)) return c;
    c.effects.forEach(function (e) {
      if (e && e.cardFields && typeof e.cardFields === 'object') {
        Object.keys(e.cardFields).forEach(function (k) { c[k] = e.cardFields[k]; });
      }
    });
    c.__pf = true;
    return c;
  }
  KG.promoteCardFields = promoteCardFields;
  KG.setPool = function (list) {
    const map = {};
    // ★ 联机确定性（2026-09-25）：对象键序 = 插入序，而两边卡池的**数组序天然不同**
    //   （各自的 DIY 卡 / 联机拉取卡的追加顺序不同）→ 任何「遍历卡池取第一张 /
    //   随机第 k 张」的效果（pickCardIdByFilter / pickCardIdByName / byName 同名首选…）
    //   在两边会选出**不同的卡** —— 同一步直接分叉（实测：同一筛选池，我方选中
    //   「召集令」av76/command/-6、对方选中「红色盟国」UN/-9，第 61 步真分叉）。
    //   这里把池按 id 排序后插键：两边 Object.keys(KG.pool) 完全同序，与各自池序无关。
    //   只影响引擎内部遍历序（按 id 查找都是 O(1) 不受影响）；UI 卡池列表（S.pool 数组）顺序不变。
    const sorted = (list || []).slice().sort(function (a, b) {
      const x = String(a && a.id), y = String(b && b.id);
      return x < y ? -1 : x > y ? 1 : 0;
    });
    sorted.forEach(function (c) { promoteCardFields(c); map[c.id] = c; });
    KG.pool = map;
    KG.buildIndexes();
    return map;
  };
  KG.buildIndexes = function () {
    if (!KG.pool) return;
    KG.byName = {};
    Object.keys(KG.pool).forEach(function (id) {
      const c = KG.pool[id];
      if (!c.name) return;
      (KG.byName[c.name] = KG.byName[c.name] || []).push(id);
      // 支持“名称前缀”模糊匹配（卡面常省略副标题）
      const base = String(c.name).split(/[\s/（(]/)[0];
      if (base && base !== c.name) (KG.byName[base] = KG.byName[base] || []).push(id);
    });
  };
  KG.card = function (id) { return (KG.pool && KG.pool[id]) || null; };
  function cardDef(state, id) {
    if(id&&typeof id==='object')id=id.id;
    // promoteCardFields 兜底：绕过 setPool 直塞进 state.pool 的路径也保证
    // effects[].cardFields 已提升到顶层（幂等，进过池的卡直接返回）。
    return promoteCardFields((state && state.pool && state.pool[id]) || (KG.pool && KG.pool[id]) || {
      id: id, name: id, cost: 1, attack: 1, defense: 1, cardType: 'unit', unitType: 'infantry', keywords: [], text: '', effects: [],
    });
  }
  KG.cardDef = cardDef;

  /* -------------------------------------------------------------- 单位与玩家 */
  function newMods() {
    return {
      vsType: {},          // { tank: {attack: +2, damage: +1, double: true} }
      takeDoubleFrom: {},  // { order: true, artillery: true }
      noAttackHQ: false,
      noAttackAir: false,      // 无法攻击空军
      immuneOrder: false,      // 无法被指令指向
      immuneUnitEffects: false,
      ignoreEnemyEffects: false,
      noPin: false,
      noRetal: false,          // 不受到反击伤害
      opCostMod: 0,
      orderDamageDouble: false,
      pinOnAttack: false,
      extraDamageTaken: 0,
      extraDamageDealt: 0,
      keywordsLost: {},
    };
  }

  /* ★ 词条 key 归一（2026-09-24）：制卡台存的自定义卡 kwMap 可能是**中文名**（「老兵」「流亡」
   *   —— 早年 saveEditor 的 CN2KEY 反查是死代码，中文 key 原样落库），而引擎只认英文 key
   *   （hasKw(u,'veteran') / isVeteran: !!kwMap.veteran）→ 这些词条**全部静默失效**。
   *   在 makeUnit 入口统一归一：cn → key（查 KEYWORDS 表），kwValues 的 key 同步换。
   *   内置卡本来就是英文 key，归一是无害 no-op。**不修改 def 本体**（def 是共享的）。 */
  function kwCn2Key(name) {
    for (const k in KEYWORDS) { if (KEYWORDS[k] && KEYWORDS[k].cn === name) return k; }
    return name;
  }
  function normalizeKwMap(map) {
    const out = {};
    Object.keys(map || {}).forEach(function (k) { out[kwCn2Key(k)] = map[k]; });
    return out;
  }
  function rekeyKwValues(vals) {
    const out = {};
    Object.keys(vals || {}).forEach(function (k) { out[kwCn2Key(k)] = vals[k]; });
    return out;
  }

  function makeUnit(state, cardId, owner) {
    const cardInstance=cardId&&typeof cardId==='object'?cardId:null;if(cardInstance)cardId=cardInstance.id;
    const def = cardDef(state, cardId);
    const kwMap = normalizeKwMap(def.kwMap);
    // ★ 「狂怒」与「奋战」是同一个词条：卡面无论写哪个，统一归一为 fury，
    //   这样引擎里只需判一种，不会出现"某张卡只带 valor 于是漏判"的情况。
    //   （卡面仍可两行同时印「狂怒」「奋战」，归一后就是同一个 key。）
    if (kwMap.valor) { kwMap.fury = true; delete kwMap.valor; }
    const u = {
      uid: nextUid(),
      cardId: cardId,
      def: def,
      name: def.name,
      owner: owner,
      baseAttack: def.attack || 0,
      baseDefense: def.defense || 1,
      attack: def.attack || 0,
      defense: def.defense || 1,
      maxDefense: def.defense || 1,
      permAtk: 0, permDef: 0, auraAtk: 0, auraDef: 0,
      tempBuffs: [],
      cardType: def.cardType,
      unitType: def.unitType || null,
      extraTypes: [],
      kws: kwMap,
      kwValues: rekeyKwValues(def.kwValues),
      mods: newMods(),
      zone: 'support',
      canAct: false,
      actionsLeft: 1,
      summonedTurn: state.turn,
      attackedThisTurn: 0,
      pinnedTurns: 0,
      pinImmuneTurns: 0,
      ambushReady: !!kwMap.ambush,
      shieldReady: !!kwMap.shield,
      magneticCharges: kwMap.magnetic ? (def.kwValues && def.kwValues.magnetic || 1) : 0,
      revealed: false,
      dead: false,
      kills: 0,
      isVeteran: !!kwMap.veteran,
      silenced: false,
      grantedEffects: [],
      opCostPaidTurns: [],
    };
    // 卡组/手牌附魔（防弹涂料、弗拉基米尔之类）
    const pm = state && state.players && state.players[owner] && state.players[owner].cardMods && state.players[owner].cardMods[cardId];
    if (pm) {
      u.attack += pm.attack || 0;
      u.permAtk = (u.permAtk || 0) + (pm.attack || 0);
      u.maxDefense += pm.defense || 0;
      u.defense += pm.defense || 0;
      (pm.keywords || []).forEach(function (k) {
        u.kws[k] = true;
        if (k === 'ambush') u.ambushReady = true;
        if (k === 'shield') { u.shieldReady = true; }
      });
      /* 卡组预挂的「获得效果」（grantEffect piles，2026-09-26）：出场挂成 extraEffects，
       *   与 OPS.grantEffect 挂在场上单位上的形态一致（{trigger, actions}） */
      (pm.effects || []).forEach(function (ef) {
        if (!ef) return;
        u.extraEffects = u.extraEffects || [];
        u.extraEffects.push(JSON.parse(JSON.stringify(Object.assign({trigger: 'death', actions: []}, ef))));
      });
    }
    if(cardInstance)applyCardInstance(state,u,cardInstance);
    return u;
  }
  KG.makeUnit = makeUnit;

  // 兵种改称：太空 → 巡地舰(landcruiser)、舰船 → 巡航舰(cruiser)、三角图标 = 太空战机(spacefighter)
  // 'space' = "太空单位"族（巡地舰 + 巡航舰 + 太空战机），卡面写"太空单位"的效果都按这个族判定
  // 'air'   = "空军"族（战斗机 + 太空战机 + 轰炸机）—— 天气「狂风」与「对战空军」都按这个族判定
  //   ⚠ 原来 'air' 没进别名表 → `isType(u,'air')` 恒为 false（没有任何单位的 unitType 叫 'air'），
  //     于是「狂风：空中单位无法攻击」整条静默失效（effects.js 里只能各自补一遍显式兵种表）。
  const TYPE_ALIAS = { space: ['landcruiser', 'cruiser', 'spacefighter'], air: ['fighter', 'spacefighter', 'bomber'] };
  // ★ 「地面单位」= **非**太空单位（卡面口径，见 cards.json 的 UNTED-LPD-7）。
  //   这是否定语义，用不了 TYPE_ALIAS 的"任一命中"结构，故在 isType 里单独判：
  //   凡是"不是 space 族"的单位都算地面单位（含 unknown / structure，避免枚举漏项）。
  const NONSPACE_TYPES = ['nonspace', 'nonspaceunit', 'ground', '地面', '地面单位'];
  function isNonSpace(u) { return !isType(u, 'space'); }
  function isType(u, t) {
    if (!u) return false;
    // 允许 t 是"族数组"（如陆军 = [infantry, tank, artillery]）——任一命中即可
    if (Array.isArray(t)) return t.some(function (x) { return isType(u, x); });
    if (NONSPACE_TYPES.indexOf(t) >= 0) return isNonSpace(u);
    if (TYPE_ALIAS[t]) return TYPE_ALIAS[t].some(function (x) { return isType(u, x); });
    return u.unitType === t || (u.extraTypes || []).indexOf(t) >= 0;
  }
  KG.isType = isType;

  // 兵种名匹配（含族别名与"多个兵种"数组）：用于总部附魔等按兵种过滤的监听
  function typeMatch(want, got) {
    if (!want) return true;
    if (!got) return false;
    const list = Array.isArray(want) ? want : [want];
    return list.some(function (w) {
      if (w === got) return true;
      if (TYPE_ALIAS[w] && TYPE_ALIAS[w].indexOf(got) >= 0) return true;
      if (TYPE_ALIAS[got] && TYPE_ALIAS[got].indexOf(w) >= 0) return true;
      return false;
    });
  }
  KG.typeMatch = typeMatch;

  function makeHandInst(id, mods) {
    if(id&&typeof id==='object'){const saved=JSON.parse(JSON.stringify(id));return Object.assign(makeHandInst(saved.id,mods),saved,{uid:nextUid('h')});}
    return { uid: nextUid('h'), id: id, costMod: (mods && mods.costMod) || 0, revealed: false, opCostSetTo0: false, costSet: null };
  }
  KG.makeHandInst = makeHandInst;
  KG.materializeDeck = function(state,p){
    p.deck=p.deck.map(entry=>{if(typeof entry==='object')return entry;const inst=makeHandInst(entry),pm=p.cardMods&&p.cardMods[entry];if(pm){inst.costMod+=pm.cost||0;inst.opCostMod=pm.opCost||0;}return inst;});
    return p.deck;
  };
  function applyCardInstance(state,u,inst){
    if(inst.opCostMod)u.mods.opCostMod=(u.mods.opCostMod||0)+inst.opCostMod;
    const mods=inst.mods;if(!mods)return;
    if(mods.attackSet!=null){u.attack=mods.attackSet;u.permAtk=u.attack-u.baseAttack;}
    if(mods.defenseSet!=null){u.maxDefense=u.defense=mods.defenseSet;u.permDef=u.maxDefense-u.baseDefense;}
    u.attack+=mods.attack||0;u.permAtk=(u.permAtk||0)+(mods.attack||0);
    u.maxDefense+=mods.defense||0;u.defense+=mods.defense||0;u.permDef=(u.permDef||0)+(mods.defense||0);
    (mods.keywords||[]).forEach(k=>{if(KG.effects?.grantKw)KG.effects.grantKw(state,u,k,mods.kwValues?.[k]??1);else u.kws[k]=true;});
    (mods.effects||[]).forEach(ef=>{if(ef)(u.extraEffects||(u.extraEffects=[])).push(JSON.parse(JSON.stringify(Object.assign({trigger:'death',actions:[]},ef))));});
  }
  KG.applyCardInstance=applyCardInstance;

  // 把牌放进手牌：记录"进场时机"的计数器快照
  // —— 「若在手牌中，友方每使用一张指令，获得-2花费」这类效果**只从它进入手牌之后**开始算，
  //    否则一张后来才抽到的牌会把之前打出的指令也算进去（一上手就已经减过费）
  function pushToHand(state, p, inst) {
    if (inst.ordersAtEntry === undefined) inst.ordersAtEntry = p.ordersPlayedThisGame || 0;
    if (inst.intelAtEntry === undefined) inst.intelAtEntry = p.intelCardsPlayed || 0;
    inst.kwAtEntry = inst.kwAtEntry || {};
    p.hand.push(inst);
    state.__lastAdded = state.__lastAdded || {};
    state.__lastAdded[p.idx] = (state.__lastAdded[p.idx] || []).concat([inst]).slice(-6);
    // ★ 「抽取：…」= **抽到这张牌时**触发它自己的效果（自身语义，和亡计一样：
    //   只有这张牌自己的 drawn 效果会跑，不会触发别人身上的）。
    //   与 killUnit 里的亡计同一个写法（同步派发、异步执行）。
    const dHit = cardDef(state, inst.id);
    const drawnFx = (dHit && dHit.effects ? dHit.effects : []).filter(function (e) { return e.trigger === 'drawn'; });
    if (drawnFx.length) {
      execEffects(state, { owner: p.idx, source: null, unit: null, card: dHit, cardId: inst.id, inst: inst, vars: {} }, drawnFx, null, 'drawn');
    }
    return inst;
  }
  KG.pushToHand = pushToHand;

  function makePlayer(state, idx, name, deck) {
    return {
      idx: idx,
      name: name || ('玩家' + (idx + 1)),
      hq: RULES.hqHp,
      hqMax: RULES.hqHp,
      hqArmor: 0,
      // 总部在**支援线视觉线**里的位置（含单位与总部混排的视觉下标）。
      //   null = 还没排过 → 渲染时按"居中"初始化（见 ui.js defaultHqSlot）。
      //   单位既能在它左边、也能在它右边；被挤动后停在这里。
      hqSlot: null,
      kredits: 0,
      maxKredits: 0,
      deck: deck.slice(),
      hand: [],
      support: [],
      discard: [],
      removed: [],
      counters: [],
      hqEnchants: [],
      cardMods: {},
      fatigue: 0,
      hqKws: {},
      costMod: 0,
      opCostModGlobal: 0,
      playedThisTurn: 0,
      unitsDeployedThisTurn: 0,
      ordersPlayedThisTurn: 0,
      ordersPlayedThisGame: 0,
    intelCardsPlayed: 0,      // 本局打出的情报牌数量（GMs-Mk3 等按此减费）
      unitsLostThisGame: 0,
      countersPlayedThisTurn: 0,
      countersPlayedThisGame: 0,
      nextTurnKredits: 0,
      nextTurnKreditSlots: 0,
      nextTurnDraws: 0,          // 「下个友方回合开始时额外抽N张」（联合工业政策）
      noDrawNextTurn: false,
      deployToFrontline: false,
      intelSeen: [],
      flags: {},
    };
  }

  /* ---------------------------------------------------------------- 卡牌种类 */
  // 单位 / 指令 / 反制。反制是独立种类：花指挥点后**挂起**，条件满足时触发，然后进弃牌堆。
  // 卡面图标：单位=兵种图标，指令="!"，反制="?"（tools/counter-scan.js 可自动识别）
  KG.CARD_TYPES = {
    unit: { cn: '单位' },
    order: { cn: '指令' },
    counter: { cn: '反制' },
  };
  KG.cardTypeCn = function (t) { return (KG.CARD_TYPES[t] || {}).cn || t || ''; };

  /* ---------------------------------------------------------- 稀有度与配额 */  // 金=1 张 / 银=2 张 / 铜=3 张 / 铁=4 张；没有标志的卡（token）不能进构筑
  KG.RARITY_INFO = {
    gold:   { cn: '金', limit: 1 },
    silver: { cn: '银', limit: 2 },
    bronze: { cn: '铜', limit: 3 },
    iron:   { cn: '铁', limit: 4 },
    token:  { cn: '特殊', limit: 0 },
  };
  KG.copyLimit = function (card) {
    if (!card) return RULES.maxCopies;
    if (card.rarity && KG.RARITY_INFO[card.rarity]) return KG.RARITY_INFO[card.rarity].limit;
    if (card.token) return 0;
    return RULES.maxCopies;
  };
  KG.canAddToDeck = function (card) { return KG.copyLimit(card) > 0; };

  /* ---------------------------------------------------------------- 建局 */
  KG.createGame = function (opts) {
    opts = opts || {};
    const seed = opts.seed == null ? (Math.random() * 1e9) | 0 : opts.seed;
    resetUidBase(seed);                 // ★ uid 序列必须可复现（联机锁步靠它定位单位）
    const state = {
      seed: seed,
      rng: mulberry32(seed),
      pool: opts.pool || KG.pool,
      turn: 1,
      active: 0,
      players: [],
      frontline: [],
      log: [],
      winner: null,
      over: false,
      pending: null,
      version: 0,
      phase: 'mulligan',                 // ★ 阶段：mulligan（准备）→ play（对局）
      mulligan: { done: [false, false] }, // 双方是否已完成换牌
    };
    const decks = opts.decks || [[], []];
    const names = opts.names || ['你', '对手'];
    state.players.push(makePlayer(state, 0, names[0], shuffleDeck(state, decks[0] || [])));
    state.players.push(makePlayer(state, 1, names[1], shuffleDeck(state, decks[1] || [])));
    // ★ 起始牌（startInHand / autoUse）：「在第一回合抽取，抽取：使用」——
    //   开局自动加入手牌（不占正常起手抽牌数）；autoUse 再自动使用（同步执行 order 效果 + 进弃牌堆）。
    for (let i = 0; i < 2; i++) {
      const p = state.players[i];
      const autoUseList = [];
      p.deck.slice().forEach(function (id) {
        const d = cardDef(state, id);
        if (!d || !(d.startInHand || d.autoUse)) return;
        const idx = p.deck.indexOf(id);
        if (idx < 0) return;
        p.deck.splice(idx, 1);
        const inst = makeHandInst(id);
        pushToHand(state, p, inst);
        if (d.autoUse) autoUseList.push({ id: id, inst: inst, def: d });
      });
      autoUseList.forEach(function (sc) {
        const d = sc.def;
        (d.effects || []).forEach(function (e) {
          if (e.trigger && e.trigger !== 'order') return;   // 只执行即时 order 效果
          (e.actions || []).forEach(function (a) {
            const fn = KG.effects && KG.effects.OPS && KG.effects.OPS[a.op];
            if (fn) { try { fn(state, { owner: i, source: null, card: d, inst: sc.inst }, a); } catch (err) {} }
          });
        });
        const hi = p.hand.indexOf(sc.inst);
        if (hi >= 0) p.hand.splice(hi, 1);
        p.discard.push(sc.id);
        log(state, p.name + ' 开局自动使用：' + d.name, 'system');
      });
    }
    for (let i = 0; i < 2; i++) {
      const p = state.players[i];
      const n = i === 0 ? RULES.handP1 : RULES.handP2;
      for (let k = 0; k < n; k++) drawCard(state, p, true);
      p.maxKredits = 0;
    }
    log(state, '对局开始 · ' + names[0] + ' vs ' + names[1], 'system');
    log(state, '准备阶段：选择要替换的牌（先手 4 张 / 后手 5 张），替换后正式开始', 'system');
    // ★ 准备阶段不 beginTurn —— 双方都确认换牌后，由 KG.startMulligan 进入第一回合
    return state;
  };

  // ★ 准备阶段：玩家 pi 替换指定下标的手牌（被替换的返回牌库并重洗，抽等量新牌）
  //   返回替换的数量；未到准备阶段 / 已确认过 / 下标非法 时返回 -1。
  KG.mulliganReplace = function (state, pi, handIndexes) {
    if (!state || state.phase !== 'mulligan') return -1;
    if (state.mulligan.done[pi]) return -1;
    const p = state.players[pi];
    const idxs = Array.isArray(handIndexes) ? handIndexes : [handIndexes];
    const valid = idxs.filter(function (i) { return i >= 0 && i < p.hand.length; });
    if (!valid.length) return -1;
    // 被替换的牌返回牌库（先收集、再放回、再洗牌，最后抽等量）
    const removed = valid.sort(function (a, b) { return b - a; }).map(function (i) {
      const inst = p.hand[i];
      p.hand.splice(i, 1);
      return inst.id;
    });
    removed.forEach(function (id) { p.deck.push(id); });
    // 洗牌（返回的牌不留在牌库顶部）
    p.deck = shuffleDeck(state, p.deck);
    for (let k = 0; k < removed.length; k++) drawCard(state, p, true);
    log(state, p.name + ' 替换了 ' + removed.length + ' 张牌', 'system');
    return removed.length;
  };
  KG.mulliganReplace = KG.mulliganReplace;

  // ★ 玩家 pi 确认换牌（换完后调用）；双方都确认后正式开始对局
  KG.mulliganDone = function (state, pi) {
    if (!state || state.phase !== 'mulligan') return false;
    state.mulligan.done[pi] = true;
    if (state.mulligan.done[0] && state.mulligan.done[1]) {
      state.phase = 'play';
      log(state, '准备阶段结束，对局正式开始', 'system');
      beginTurn(state, 0, true);
    }
    return true;
  };
  KG.mulliganDone = KG.mulliganDone;

  // 洗牌：起手与后续抽牌顺序都必须是随机的（之前直接按卡组列表顺序抽，等于顺序固定）
  function shuffleDeck(state, deck) {
    const a = deck.slice();
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(state.rng() * (i + 1));
      const t = a[i]; a[i] = a[j]; a[j] = t;
    }
    return a;
  }
  KG.shuffleDeck = shuffleDeck;

  // 情报X：把敌方随机 X 张手牌变成明牌（X 上限 3）
  async function applyIntelKeyword(state, pi, def) {
    const kw = def && def.kwMap && def.kwMap.intel;
    if (!kw) return;
    state.players[pi].intelCardsPlayed = (state.players[pi].intelCardsPlayed || 0) + 1;   // 供"每使用一张情报牌"类效果计数
    const mep = state.players[pi];
    mep.kwCardsPlayed = mep.kwCardsPlayed || {};
    Object.keys(def.kwMap || {}).forEach(function (kw) { mep.kwCardsPlayed[kw] = (mep.kwCardsPlayed[kw] || 0) + 1; });
    let x = (def.kwValues && def.kwValues.intel) || 1;
    x = Math.max(1, Math.min(3, x));                       // 最多 3
    const foe = state.players[1 - pi];
    // ★ 只从**还不是明牌**的手牌里随机（2026-09-24 制作者口径：情报不可能随机到已经是明牌的牌）
    const hidden = foe.hand.filter(function (h) { return !h.revealed; });
    for (let i = 0; i < x && hidden.length; i++) {
      const k = Math.floor(state.rng() * hidden.length);
      const inst = hidden.splice(k, 1)[0];
      inst.revealed = true;
      log(state, (pi === 0 ? '你' : '对手') + ' 的情报：看到对手手牌中的 ' + cardDef(state, inst.id).name, 'keyword');
    }
    const me = state.players[pi];
    me.intelSeen = me.intelSeen || [];
  }
  KG.applyIntelKeyword = applyIntelKeyword;

  function drawCard(state, p, silent, options = {}) {
    const deck = options.deck || p.deck;
    const extra = options.extra ?? (!p.__inTurnStartDraw || (p.__turnDrawCount || 0) >= 1);
    const drawing = deck.length > 0 && (options.entry === undefined || deck.includes(options.entry));
    const behaviorAliases = drawing && state.phase === 'play' ? ['op/draw', ...(extra ? ['op/drawOne'] : [])] : [];
    return KG.performImmediateBehavior(state, 'game', 'drawCard', { owner: p.idx, source: null, amount: 1, extra, behaviorAliases },
      () => drawCardBody(state, p, silent, { ...options, deck, extra }));
  }
  function drawCardBody(state, p, silent, options) {
    const deck = options.deck;
    if (deck.length === 0) {
      if (RULES.fatigue) {
        p.fatigue++;
        const fatigue = damageHQ(state, p, p.fatigue, '疲劳', null);
        log(state, p.name + ' 牌库已空，受到 ' + p.fatigue + ' 点疲劳伤害', 'damage');
        if (fatigue && typeof fatigue.then === 'function') return fatigue.then(() => null);
      }
      return null;
    }
    const index = options.entry === undefined ? 0 : deck.indexOf(options.entry);
    if (index < 0) return false;
    const entry = deck.splice(index, 1)[0],id=typeof entry==='object'?entry.id:entry;
    p.__turnDrawCount = (p.__turnDrawCount || 0) + 1;
    // 手牌满了也要把牌从牌库取走（烧牌）：否则牌库永不耗尽、疲劳不触发，对局可能无限拖下去
    if (p.hand.length >= RULES.handMax) {
      if (!silent) log(state, p.name + ' 手牌已满，「' + cardDef(state, id).name + '」被烧掉', 'draw');
      p.discard.push(id);
      // ★ 给界面留个打点：烧掉的是哪张（UI 会播"从手牌区最右边滑进来 → 焦化进弃牌堆"的演出）
      state.burnPops = state.burnPops || [];
      state.burnPops.push({ pi: p.idx, id: id });
      return null;
    }
    const inst = makeHandInst(entry);
    // ★ 卡组附魔里的**花费/行动花费**修正（「使友方卡组里所有单位获得减1花费」）在抽到这张牌时应用：
    //   身材/词条走 makeUnit，费用只能在这里落（以前只记了攻击/防御/词条，费用静默丢失）。
    const pm0 = p.cardMods && p.cardMods[id];
    if (pm0 && typeof entry!=='object') {
      if (pm0.cost) inst.costMod = (inst.costMod || 0) + pm0.cost;
      if (pm0.opCost) inst.opCostMod = (inst.opCostMod || 0) + pm0.opCost;
    }
    // 「额外抽牌」= 非回合开始抽牌，或回合开始抽的第 2 张及以后
    const isExtraDraw = options.extra;
    const def = cardDef(state, id);
    if (def.kwMap && def.kwMap.revealed) inst.revealed = true;
    pushToHand(state, p, inst);
    if (!silent) log(state, p.name + ' 抽了一张牌（牌库剩余 ' + deck.length + '）', 'draw');
    if (isExtraDraw && !silent) return runTrigger(state, { trigger: 'extraDraw', owner: p.idx, source: null, amount: 1 }).then(() => inst);
    return inst;
  }
  KG.drawCard = drawCard;

  /* ------------------------------------------------------------------ 回合 */
  function beginTurn(state, idx, first) {
    return KG.performImmediateBehavior(state, 'game', 'beginTurn', { owner: idx, source: null },
      () => beginTurnBody(state, idx, first));
  }
  async function beginTurnBody(state, idx, first) {
    const p = state.players[idx];
    state.active = idx;
    if (!first) state.turn++;
    // 自然增长到 12 为止；额外槽位由具体卡牌效果处理。
    const extraSlots = p.nextTurnKreditSlots || 0;
    p.nextTurnKreditSlots = 0;
    // ★ 「下个回合无法增加指挥点槽」：跳过本回合的自然增长（卡牌给的 extraSlots 仍生效）
    if (p.noKreditSlotNextTurn) { p.noKreditSlotNextTurn = false; }
    // ★ 牌Q 卡包（压迫）：「接下来两回合内无法增加指挥点槽」
    else if (p.noKreditSlotTurns > 0) {
      p.noKreditSlotTurns--;
      log(state, p.name + ' 处于"无法增加指挥点槽"状态（剩 ' + p.noKreditSlotTurns + ' 回合）', 'keyword');
    }
    else if (p.maxKredits < RULES.kreditCap) p.maxKredits = Math.min(RULES.kreditCap, p.maxKredits + 1);
    p.maxKredits = Math.min(RULES.kreditSlotHardCap, p.maxKredits + extraSlots);
    // AI 难度加成
    if (state.aiBonusKredit && idx === 1
      && (!state.aiBonusEvery || state.turn % state.aiBonusEvery === 0)
      && (!state.aiBonusFromTurn || state.turn >= state.aiBonusFromTurn)) {
      p.maxKredits += state.aiBonusKredit;
      p.kredits += state.aiBonusKredit;
    }
    // 后手补偿：第二位玩家的首个回合多 1 点指挥点
    if (!first && state.turn === 2 && RULES.secondPlayerBonusKredit && !p.__bonusGiven) {
      p.__bonusGiven = true;
      p.maxKredits += RULES.secondPlayerBonusKredit;
      log(state, p.name + ' 获得后手补偿：+1 指挥点', 'cost');
    }
    if (p.nextTurnKreditSlots) { log(state, p.name + ' 获得 ' + p.nextTurnKreditSlots + ' 个额外指挥点槽', 'cost'); p.nextTurnKreditSlots = 0; }
    p.kredits = p.maxKredits + (p.nextTurnKredits || 0);
    if (p.nextTurnKredits) { log(state, p.name + ' 获得 ' + p.nextTurnKredits + ' 个额外指挥点', 'cost'); p.nextTurnKredits = 0; }
    p.__turnDrawCount = 0;
    p.__inTurnStartDraw = true;
    p.playedThisTurn = 0;
    p.__inTurnStartDraw = false;
    p.unitsDeployedThisTurn = 0;
    p.ordersPlayedThisTurn = 0;
    p.deployToFrontline = false;
    p.flags.orderDoubleTurn = false;
    // ★ 反制挂起到期（制作者口径）：挂起后经过一个敌方回合仍未触发的反制，
    //   在**己方下一个回合开始时取消挂起**（回到普通手牌，返还埋设花费）。
    //   标了 persistent 的（混合卡，如北极星计划的附魔部分）不参与到期。
    (p.counters || []).slice().forEach(function (c) {
      if (!c || !c.inst || c.persistent) return;
      if (c.inst.suspendedTurn === state.turn) return;      // 本回合刚埋下的，保留
      KG.unsuspendCounter(state, idx, c.inst);
    });

    // 临时总部词条（例如戒严令给总部的免疫：只到该玩家下个回合开始）
    if (p.hqKwsTemp) {
      Object.keys(p.hqKwsTemp).forEach(function (k) {
        if (p.hqKws && p.hqKws[k]) { delete p.hqKws[k]; log(state, p.name + ' 总部的「' + k + '」已到期', 'keyword'); }
      });
      p.hqKwsTemp = {};
    }

    // 总部附魔到期（"本回合"）
    p.hqEnchants = (p.hqEnchants || []).filter(function (e) {
      if (e.untilTurn == null) return true;
      if (state.turn >= e.untilTurn) { log(state, p.name + ' 的总部效果「' + e.name + '」已到期', 'keyword'); return false; }
      return true;
    });
    const all = allUnitsOf(state, idx);
    // ★ 协力：快照「本回合开始时场上有哪些同国友方单位」——协力在部署时才结算，
    //   那时场面已被本回合新部署的单位改动，必须在这里取（部署的协力单位自身不算"开始时已有"）。
    p.nationsAtTurnStart = {};
    all.forEach(function (u) { const _st = (u.def && u.def.set) || ''; if (_st) p.nationsAtTurnStart[_st] = true; });
    all.forEach(function (u) {
      u.actionsLeft = (u.kws.valor || u.kws.fury) ? 2 : 1;
      u.attackedThisTurn = 0;
      u.canAct = true;
      // 伏击每回合重新就绪（每回合最多触发一次）
      if (u.kws.ambush) u.ambushReady = true;
      if (u.summonedTurn === state.turn && !u.kws.blitz && !u.kws.guerrilla) u.canAct = false;
      // ★ 「压制」持续到**拥有者的下一个回合开始时**才解除。
      //   这里 u 的拥有者正是刚进入回合的这方（allUnitsOf(state, idx)），所以本行就是
      //   「下一友方回合开始」这个时点：先由 pinnedTurns 结算"这一整个回合都不能行动"，
      //   再在回合开始的这一刻把它清掉（清掉后本回合仍因 canAct=false 无法行动）。
      //   ⚠ 历史 bug：原来在同一行里 `if (pinnedTurns === 0) delete kws.pin`，
      //   结果"已被压制"类判定（hasKeyword:'pin'）在被打压的这一整个回合里读到的都是 false，
      //   而卡面/效果语义要求"压制期间"始终成立。改为：压制生效期间 kws.pin 一直为 true，
      //   只在真正解除的那次 beginTurn 里删除。
      if (u.pinnedTurns > 0) {
        if (u.pinnedUntilTurn == null) u.pinnedUntilTurn = state.turn + 2 * (u.pinnedTurns - 1);
        u.canAct = false;
      } else if (u.kws.pin) {
        // 兜底：pinnedTurns 已为 0 但 kws.pin 残留（例如被 unpin 之外的方式清零）
        delete u.kws.pin;
      }
      if (u.pinImmuneTurns > 0) u.pinImmuneTurns--;
      if (hasKw(u, 'mobilize')) {
        u.attack++; u.permAtk = (u.permAtk || 0) + 1;
        u.defense++; u.maxDefense++; u.permDef = (u.permDef || 0) + 1;
        log(state, u.name + ' 动员：获得 +1/+1', 'keyword');
      }
      // 清除临时增益
      if (u.tempBuffs && u.tempBuffs.length) {
        u.tempBuffs.forEach(function (b) {
          u.attack -= b.attack || 0;
          u.maxDefense -= b.defense || 0;
          u.defense -= b.defense || 0;
          if (u.defense < 1) u.defense = 1;
          if (b.keyword) delete u.kws[b.keyword];
          if (b.opCostMod) u.mods.opCostMod -= b.opCostMod;
        });
        u.tempBuffs = [];
      }
      // 清除临时减益（「-N-M，直至下一友方回合开始」这类，OPS.debuffTemp 打的标记）。
      //   ★ 这个字段以前**只写不读**（死代码）：用 debuffTemp 编码的卡会永久掉身材。
      //     时点与 tempBuffs 一致 —— 拥有者下个回合开始时归还。
      if (u.tempDebuff) {
        const d = u.tempDebuff;
        u.attack = Math.max(0, u.attack + (d.attack || 0));
        if (d.defense) { u.maxDefense += d.defense; u.defense += d.defense; }
        u.tempDebuff = null;
      }
      // ★ 清除「本回合内，…」的常驻修正（grantMod + duration:'turn'，见 effects.js）。
      //   以前这类修正**永久留着**：「本回合内，本单位无法攻击总部」会让单位再也打不了总部。
      //   ⚠ 必须**连 dynMods 一起摘**：recomputeAuras 会从 dynMods 重建 u.mods，
      //     只 delete u.mods 的话下一个回合它又回来了（清了个寂寞）。
      if (u.turnMods && u.turnMods.length) u.turnMods = [];      // 旧字段，兼容清空
      // ★★ 充能计时（词条「充能X」）：部署后 X 回合触发「充能完毕」。
      //   到点后把 u.chargeReady 打开：
      //     · 无条件的「充能完毕时，…」→ 本回合立即执行（见下面 chargeNow 的触发），
      //       执行完照常重新计时（它每 X 回合来一次）。
      //     · 有条件的「充能完毕后，<事件>，…」→ 挂着等事件；事件到了由 effects.js 的
      //       exec 门控执行**一次**，然后关掉 chargeReady 并重新计时。
      // ★★ 充能计时（词条「充能X」）：部署后 X 回合触发「充能完毕」。
      //   判据放宽成三层（任一成立即视为有充能），因为**卡面写了充能但卡数据缺值**很常见：
      //     ① kwValues.charge（正常路径）  ② kwMap.charge  ③ 卡面文本里出现「充能」
      //   ⚠ 早期编辑器只写 kwMap 不写 kwValues（Round 78 才修）—— 那之前保存的自定义卡
      //     数值是空的 → 引擎拿不到 x → **充能永远不触发**（用户报的充能效果无法触发）。
      //   ★ 第 4 层兜底：卡的 `keywords` 数组（编辑器保存时最先写、最不容易丢的那个字段）
      //     里若写着「充能1」，也能抠出数值 —— 前面几层全落空时才用。
      const _CN = { '一':1,'二':2,'两':2,'三':3,'四':4,'五':5,'六':6,'七':7,'八':8,'九':9,'十':10 };
      const _chargeFromText = function (txt) {
        const dm = String(txt || '').match(/充能\s*([一二两三四五六七八九十\d]+)/);
        if (!dm) return 0;
        return /^\d+$/.test(dm[1]) ? parseInt(dm[1], 10) : (_CN[dm[1]] || 0);
      };
      const _kwArr = (u.def && Array.isArray(u.def.keywords)) ? u.def.keywords : [];
      const _hasCharge = (u.kwValues && u.kwValues.charge != null)
        || (u.kws && u.kws.charge)                                      // 单位身上的词条表是 u.kws（原写 u.kwMap 恒为 undefined，这一层是死代码）
        || (!u.silenced && (/充能/.test(String((u.def && u.def.text) || ''))
          || _kwArr.some(function (k) { return /充能/.test(String(k)); })));
      if (_hasCharge) {
        let cv = u.kwValues ? parseInt(u.kwValues.charge, 10) : 0;
        if (!cv) cv = _chargeFromText((u.def && u.def.text) || '');     // 从卡面文本抠
        if (!cv) {                                                      // 从 keywords 数组抠
          for (let _i = 0; _i < _kwArr.length; _i++) { cv = _chargeFromText(_kwArr[_i]); if (cv) break; }
        }
        if (cv > 0) {
          // ★ 把兜底抠出的 x **归一写回单位**：上面几层兜底只决定了"有充能、x 是几"，
          //   但下面「充能完毕时，…」的执行判据、以及 effects.js 的 chargeGate 重新计时，
          //   读的都是 kwValues.charge —— 不写回的话：词条/卡面兜底认出了充能、计时也到点，
          //   真正的效果却**永远不执行**（用户报的"充能无法触发效果"正是这个）。
          if (!u.kwValues) u.kwValues = {};
          u.kwValues.charge = cv;
          if (u.chargeIn == null) u.chargeIn = cv;
          // ★ 复合充能武装期间（chargeArmed）：倒计时**暂停** —— "充能效果必须全部触发
          //   （=这次攻击消费掉整个武装）才会进入下一次充能"，攻击后由 dischargeChargeArm 重置。
          if (!u.chargeArmed && !u.chargeReady) u.chargeIn -= 1;
          if (u.chargeIn <= 0) {
            if (!u.chargeReady) u.chargeConsumed = [];
            u.chargeReady = true;
            u.chargeIn = cv;                 // 重新计时
            log(state, u.name + ' 充能完毕', 'keyword');
            chargeArmIfAny(state, u);        // ★ 复合型充能：有武装效果 → 到点立即挂上（见下）
          }
        }
      }
      if (Array.isArray(u.dynMods) && u.dynMods.length) {
        const expired = u.dynMods.filter(function (g) { return g && g.untilTurn != null && state.turn >= g.untilTurn; });
        if (expired.length) {
          u.dynMods = u.dynMods.filter(function (g) { return !(g && g.untilTurn != null && state.turn >= g.untilTurn); });
          expired.forEach(function (g) {
            if (u.mods && g.mod && g.mod !== 'vsType' && g.mod !== 'vsHq') delete u.mods[g.mod];
          });
        }
      }
    });
    log(state, '—— 第 ' + state.turn + ' 回合 · ' + p.name + '（' + p.kredits + ' 指挥点 / 上限 ' + p.maxKredits + '）——', 'turn');
    // 回合结束销毁的临时单位
    for (const u of allUnitsOf(state, idx).slice()) {
      if (u.killAtTurnEnd) { u.killAtTurnEnd = false; await killUnit(state, u, null); }
    }
    recomputeAuras(state);
    if (!first || RULES.drawOnFirstTurn) {
      if (p.noDrawNextTurn) { p.noDrawNextTurn = false; log(state, p.name + ' 本回合无法抽牌', 'draw'); }
      else await drawCard(state, p, false, { extra: false });
    }
    /* ★★ 「在第N回合抽取」：牌组里带 `drawOnTurn === 当前回合` 的牌 → **这一回合直接抽到手**。
     *   （卡面字段，由 compiler 抠出来；不是对战效果原语）
     *   这几个回合计数用 state.turn（全局回合序号），与卡面"第N回合"口径一致。 */
    if (p.deck && p.deck.length) {
      const due = p.deck.filter(function (id) {
        const d = KG.cardDef(state, id);
        return d && d.drawOnTurn != null && state.turn >= d.drawOnTurn;
      });
      for (const id of due) {
        const i = p.deck.indexOf(id);
        if (i < 0) continue;
        await drawCard(state, p, false, { entry: id, extra: true });
        const d = KG.cardDef(state, id) || {};
        log(state, '第 ' + state.turn + ' 回合抽取：' + (d.name || id), 'draw');
      }
    }
    // 「下个友方回合开始时额外抽N张」——此时 __inTurnStartDraw 已为 false，会计入「额外抽牌」
    if (p.nextTurnDraws) {
      const nd = p.nextTurnDraws;
      p.nextTurnDraws = 0;
      log(state, p.name + ' 触发延迟效果：额外抽 ' + nd + ' 张牌', 'draw');
      for (let i = 0; i < nd; i++) { if (state.over) break; await drawCard(state, p, false); }
    }
    // ★ 充能完毕（**无条件**）：「充能完毕时，…」到点后立即执行一次。
    //   （有条件的 chargeGate 效果不在这里 —— 它们挂着等事件，由 exec 的门控负责。）
    allUnitsOf(state, idx).slice().forEach(function (u) {
      // chargeReady 只会在"确实解析出充能 x"时被打开（见上面的兜底 + 归一），
      // 这里若再要求 kwValues.charge，走兜底的卡就会被静默挡掉 = 效果永不执行。
      if (!u.chargeReady) return;
      const hasNow = (u.silenced ? [] : ((u.def && u.def.effects) || [])).concat(u.extraEffects || []).some(function (e) {
        return e && e.trigger === 'chargeNow';
      });
      if (hasNow) runTrigger(state, { trigger: 'chargeNow', owner: idx, source: u });
    });
    // ★ 2026-09-22：快照「上一回合被消灭的单位数」——供 turnStart 的效果读。
    //   卡面：「友方回合开始时，获得等同于**上一回合被消灭的单位数量**的指挥点」
    //   （制作者报：这句原来被解析成 destroy + 选一个单位）。
    //   ⚠ 必须在 turnStart **之前**快照：此时 unitsLostThisTurn 累计的是
    //     "从上次这一方回合开始到现在"（含敌方那一个回合）的损失，正是"上一回合"。
    {
      const pp = state.players[idx];
      if (pp) {
        pp.unitsLostLastTurn = pp.unitsLostThisTurn || 0;
        pp.unitsLostThisTurn = 0;
      }
    }
    // ★ 「下一个XX回合开始时」控制流原语（OPS.nextTurnStart）的结算点。
    //   只结算本玩家的、且在**之前**排队的（queuedTurn === state.turn 的不当场触发）。
    //   放在 runTrigger('turnStart') 之前：卡面语义"回合开始时"先于其它回合开始监听。
    //   ⚠ 与上面的 runTrigger 一样是 fire-and-forget（beginTurn 是同步函数，保持既有风格）。
    {
      const pend = state.pendingTurnStarts;
      if (pend && pend.length) {
        const due = pend.filter(function (q) { return q.player === idx && q.queuedTurn !== state.turn; });
        if (due.length) {
          state.pendingTurnStarts = pend.filter(function (q) { return !(q.player === idx && q.queuedTurn !== state.turn); });
          due.forEach(function (q) {
            KG.effects.exec(state, { owner: q.ctxOwner, source: null, card: null, event: { trigger: 'turnStart' } },
              [{ trigger: null, actions: q.actions }], null, null);
          });
        }
      }
    }
    runTrigger(state, { trigger: 'turnStart', owner: idx, source: null });
    checkWin(state);
  }
  KG.beginTurn = beginTurn;

  KG.endTurn = async function (state, chooser) {
    if (state.over || state.phase !== 'play') return false;
    // 落雪：每回合结束时所有单位受 1 点伤害（制作者委托我设计的天气效果）
    if (weatherOf(state) === 'snow') {
      const all = [];
      [0, 1].forEach(function (oi) { allUnitsOf(state, oi).slice().forEach(function (u) { if (!u.dead) all.push(u); }); });
      for (const u of all) await damageUnit(state, u, 1, null, {});
      log(state, '落雪：所有单位受到 1 点伤害', 'keyword');
      if (state.over) return;
    }
    if (state.over) return;
    const p = state.players[state.active];
    await runTrigger(state, { trigger: 'turnEnd', owner: state.active, source: null });
    // 只在本回合存活的临时单位（"回合结束时消灭"）
    for (const u of allUnitsOf(state, state.active).slice()) {
      if (u.killAtTurnEnd && !u.dead) { u.killAtTurnEnd = false; await killUnit(state, u, null); }
      if (u.pinnedUntilTurn != null && u.pinnedUntilTurn <= state.turn) {
        u.pinnedTurns = 0; delete u.kws.pin; delete u.pinnedUntilTurn;
        runTrigger(state, { trigger: 'unpinned', owner: u.owner, source: u, victim: u, chooser: chooser });
      }
    }
    [0, 1].forEach(function (oi) {
      allUnitsOf(state, oi).forEach(function (u) {
        if(KG.effects && KG.effects.composition)KG.effects.composition.expire(state,u);
        u.tempBuffs = (u.tempBuffs || []).filter(function (b) {
          if (b.untilTurn == null || b.untilTurn > state.turn) return true;
          u.attack -= b.attack || 0; u.maxDefense -= b.defense || 0;
          u.defense = Math.max(1, u.defense - (b.defense || 0));
          if (b.keyword) delete u.kws[b.keyword];
          return false;
        });
        u.dynMods = (u.dynMods || []).filter(function (g) { return g.untilTurn == null || g.untilTurn > state.turn; });
      });
    });
    recomputeAuras(state);
    log(state, p.name + ' 结束回合', 'turn');
    await beginTurn(state, 1 - state.active, false);
  };

  /* ------------------------------------------------------------ 单位与查询 */
  // 前线是共享区：state.frontline 里同时装着双方的单位
  function frontlineOf(state, idx) {
    return state.frontline.filter(function (u) { return u.owner === idx; });
  }
  KG.frontlineOf = frontlineOf;

  // 某玩家在某个阵线上的单位（zone: 'support' | 'frontline'）
  function unitsInZone(state, idx, zone) {
    if (zone === 'frontline') return frontlineOf(state, idx);
    return state.players[idx].support.slice();
  }
  KG.unitsInZone = unitsInZone;

  function allUnitsOf(state, idx) {
    return state.players[idx].support.concat(frontlineOf(state, idx));
  }
  KG.allUnitsOf = allUnitsOf;

  // Physical neighbors share one rule across queries, auras and death snapshots.
  // HQ occupies a support slot and therefore separates the units on either side.
  KG.unitNeighbors = function(state, u) {
    const z = KG.zoneOf(state, u.uid);
    if (!z) return (u.__adjacentUids || []).map(id => KG.unitByUid(state, id)).filter(Boolean);
    const line = z.zone === 'frontline' ? state.frontline.slice() : z.player.support.slice();
    if (z.zone === 'support') line.splice(Math.max(0, Math.min(line.length, z.player.hqSlot ?? 0)), 0, null);
    const i = line.findIndex(x => x && x.uid === u.uid);
    return i < 0 ? [] : [line[i - 1], line[i + 1]].filter(Boolean);
  };

  // 把单位从场上移除（自动处理共享前线）
  //   opts.keepHq：reposition/move 的**内部搬移**专用 —— 搬走马上插回来，总部不应滑动；
  //   真离场（死亡/回手/移走/换线）不传它，总部左侧少人时 hqSlot 左滑补位。
  function removeUnitFromBoard(state, u, opts) {
    const z = KG.zoneOf(state, u.uid);
    if (!z) return;
    /* ★ 2026-10-01：离场前记下**左右邻居的 uid**。
     *   为什么需要：`killUnit` 的顺序是 `u.dead=true` → `removeUnitFromBoard` → **然后**才派发
     *   `death`（亡计）。而 `matchFilter` 的 `adjacentTo:'self'` 是拿 `zoneOf(state, 来源单位)`
     *   找来源所在的那条阵线、再比下标 —— 单位已经移出场 → `zoneOf` 返回 null → **恒 false**。
     *   表现是「亡计：对相邻单位造成X点伤害」整张卡**静默失效**（不报错、不打日志）。
     *   ⚠ 存 **uid 而不是下标**：移除后同一条阵线上排在后头的单位下标全部前移一位，
     *     再用下标比对会错位，把"隔一个"的单位误判成相邻。uid 不受位置变化影响。
     *   只在**真离场**时记；`opts.keepHq` 是 reposition/move 的内部搬移（搬走马上插回），不留快照。 */
    u.__adjacentUids = KG.unitNeighbors(state, u).map(x => x.uid);
    if (z.zone === 'frontline') state.frontline = state.frontline.filter(function (x) { return x.uid !== u.uid; });
    else {
      const p = z.player;
      const i = p.support.indexOf(u);
      p.support = p.support.filter(function (x) { return x.uid !== u.uid; });
      // ★ hqSlot 维护（2026-09-26 Alan）：p.hqSlot = 总部视觉格位 = 总部左侧单位数。
      //   左侧单位真离场 → 总部左滑一格补位；并 clamp 防越界（合法范围 0..support.length）。
      //   hqSlot 为 null 时不动（UI 首次渲染才初始化，渲染端自愈兜底）。
      if (!(opts && opts.keepHq) && p.hqSlot != null) {
        if (i >= 0 && i < p.hqSlot) p.hqSlot -= 1;
        if (p.hqSlot > p.support.length) p.hqSlot = p.support.length;
      }
    }
  }
  KG.removeUnitFromBoard = removeUnitFromBoard;

  /* ------------------------------------------------------------------ 槽位 */
  // 单位在"自己那条阵线"里的位置（0 = 最左，紧挨总部）。总部算作支援线最左端（位置 -1）。
  function slotOf(state, u) {
    if (!u) return -1;
    const line = u.zone === 'frontline' ? frontlineOf(state, u.owner) : state.players[u.owner].support;
    return line.findIndex(function (x) { return x.uid === u.uid; });
  }
  KG.slotOf = slotOf;
  KG.lineOf = function (state, idx, zone) {
    return zone === 'frontline' ? frontlineOf(state, idx) : state.players[idx].support.slice();
  };
  // 把单位插到某条阵线的指定位置（clamp 到合法范围；前线是共享数组，需换算成整条数组里的下标）
  //   opts.hqDisplace === 'left'：UI「丢到总部上」的部署语义 —— 插到数组 at === hqSlot
  //   （= 总部正上那格）时把总部顶开右滑；默认（summon/move/reposition 的数组坐标语义）
  //   at === hqSlot 是「总部右邻」，不挤。
  function insertUnitAt(state, idx, u, zone, slot, opts) {
    const arr = zone === 'frontline' ? state.frontline : state.players[idx].support;
    let at = slot == null || slot < 0 ? arr.length : slot;
    if (zone === 'frontline') {
      const own = frontlineOf(state, idx);
      const anchor = (at < own.length) ? own[at] : null;
      at = anchor ? arr.findIndex(function (x) { return x.uid === anchor.uid; }) : arr.length;
      if (at < 0) at = arr.length;
    } else {
      at = Math.max(0, Math.min(at, arr.length));
    }
    arr.splice(at, 0, u);
    // ★ hqSlot 维护（2026-09-26 Alan）：p.hqSlot = 总部视觉格位 = 总部左侧单位数。
    //   插到总部左侧（at < hqSlot）→ 总部被挤右滑一格；at === hqSlot 仅在显式
    //   hqDisplace:'left'（UI 部署丢总部上）时挤。hqSlot 为 null 时不动（渲染端自愈负责初始化）。
    if (zone === 'support') {
      const p = state.players[idx];
      if (p.hqSlot != null && (at < p.hqSlot || (at === p.hqSlot && opts && opts.hqDisplace === 'left'))) {
        p.hqSlot += 1;
      }
    }
    return at;
  }
  KG.insertUnitAt = insertUnitAt;

  KG.unitByUid = function (state, uid) {
    for (const p of state.players) {
      const u = p.support.find(function (x) { return x.uid === uid; });
      if (u) return u;
    }
    return state.frontline.find(function (x) { return x.uid === uid; }) || null;
  };

  KG.zoneOf = function (state, uid) {
    for (const p of state.players) {
      if (p.support.some(function (x) { return x.uid === uid; })) return { player: p, zone: 'support' };
    }
    const u = state.frontline.find(function (x) { return x.uid === uid; });
    if (u) return { player: state.players[u.owner], zone: 'frontline', shared: true };
    return null;
  };

  function hasKw(u, kw) { return !!(u && u.kws && u.kws[kw]); }
  KG.hasKw = hasKw;

  /* ★ 烟幕的禁则（制作者 2026-09-29 口径，RULES_REF 12.4 confirmed）：
   *   单位**位于前线**、或**具有守护/固守**时，**不能拥有烟幕** —— 已经有的也立刻失效
   *   （"若单位移动到前线或获得固守，烟幕失效"）。
   *   ⚠ 战斗机那条同款禁则制作者明确说**不要做**，别自作主张加进去。
   *
   *   两道保险，缺一不可：
   *   ① 写入侧 —— effects.js 的 grantKw 遇到这两类单位直接不授予（根本不写进 u.kws）；
   *   ② 清扫侧 —— enforceSmokeBans() 挂在 recomputeAuras 末尾：卡面原生 kwMap 自带烟幕、
   *      直接赋值 u.kws、以及"先有烟幕后拿到守护"这些路径都从这里兜住。
   *   读侧**一律走 inSmoke()** 而不是 hasKw(u,'smokescreen')：万一某条路径没跑到重算，
   *   前线的/带守护的单位身上的残留也不会被当成"在烟幕里"。 */
  function smokeBanned(u) {
    return !!u && (u.zone === 'frontline' || hasKw(u, 'guard'));
  }
  function inSmoke(u) {
    return hasKw(u, 'smokescreen') && !smokeBanned(u);
  }
  KG.smokeBanned = smokeBanned;
  KG.inSmoke = inSmoke;

  /* 清扫：把所有"不该有烟幕"的单位身上的烟幕摘掉（每次 recomputeAuras 后跑一遍）。 */
  function enforceSmokeBans(state) {
    if (!state || !state.players) return;
    for (const p of state.players) {
      allUnitsOf(state, p.idx).forEach(function (u) {
        if (!u || u.dead) return;
        if (hasKw(u, 'smokescreen') && smokeBanned(u)) {
          u.kws.smokescreen = false;
          log(state, u.name + ' 的烟幕失效（' + (u.zone === 'frontline' ? '位于前线' : '具有守护/固守') + '的单位不能拥有烟幕）', 'keyword');
        }
      });
    }
  }
  KG.enforceSmokeBans = enforceSmokeBans;

  KG.controlsFrontline = function (state, idx) { return frontlineOf(state, idx).length > 0; };
  KG.frontlineControl = function (state, idx) {
    const me = frontlineOf(state, idx).length;
    const foe = frontlineOf(state, 1 - idx).length;
    if (me > 0 && foe === 0) return 1;
    if (foe > 0 && me === 0) return -1;
    return 0;
  };

  /* ------------------------------------------------------------------ 伤害 */
  function armorOf(u) {
    if (!u) return 0;
    const v = u.kwValues || {};
    let a = (v.armor || 0) + (v.lightArmor || 0) + (u.armorBonus || 0);
    if (u.kws && u.kws.shield && u.shieldReady) a += 1;  // 强磁护盾：受到伤害 -1
    if (u.kws && u.kws.sponge) a += 1;
    return a;
  }
  KG.armorOf = armorOf;

  /* ★ 护甲**按伤害来源**分别生效（制作者规则）：
   *   - 重甲 armor      ：只减**对战伤害**（攻击造成的；指令/效果伤害不减）
   *   - 轻甲 lightArmor ：只减**步兵**造成的伤害 -x
   *   - 强磁护盾/海绵装甲/armorBonus：不区分来源，一律生效
   *   - ★ 例外（制作者 2026-09-28）：卡面写「本单位受到指令重甲减伤」的单位，
   *     mods.armorVsOrder = true → 该单位**自己**的重甲对指令伤害也生效。
   *     普通重甲单位完全不受影响。
   *
   * 判定依据：`opts.combat`（或 `opts.attacker`）—— KG.attack 传 {attacker, combat:true}；
   * 指令伤害带 `opts.fromOrder`。
   * 兵种取自来源单位（attacker 优先，其次 source）。
   */
  function armorAgainst(u, opts, source) {
    if (!u) return 0;
    opts = opts || {};
    const v = u.kwValues || {};
    const isCombat = !!(opts.combat || opts.attacker);
    const src = opts.attacker || source || null;
    const srcType = src ? src.unitType : null;
    let a = (u.armorBonus || 0);
    if (u.kws && u.kws.shield && u.shieldReady) a += 1;   // 强磁护盾：受到伤害 -1
    if (u.kws && u.kws.sponge) a += 1;
    if (isCombat || (opts.fromOrder && u.mods && u.mods.armorVsOrder)) a += (v.armor || 0);
    if (srcType === 'infantry') a += (v.lightArmor || 0); // 轻甲：只挡步兵的伤害
    return a;
  }
  KG.armorAgainst = armorAgainst;

  function damageUnit(state, u, amount, source, opts) {
    if (!u || u.dead || amount <= 0) return damageUnitBody(state, u, amount, source, opts);
    return KG.performImmediateBehavior(state, 'game', 'damageUnit', { owner: source?.owner ?? u.owner,
      source: source || u, unit: u, victim: u, amount }, () => damageUnitBody(state, u, amount, source, opts));
  }
  function damageUnitBody(state, u, amount, source, opts) {
    opts = opts || {};
    if (!u || u.dead || amount <= 0) return 0;
    let dmg = amount;
    // 免疫 / 无敌
    if (u.mods && u.mods.immune) { log(state, u.name + ' 具有免疫，伤害被抵消', 'keyword'); return 0; }
    if (!opts.combat && hasKw(u, 'conceal') && (!source || source.uid !== u.uid)) return 0;
    // 无视指令：完全不受指令影响（连群体指令也不行）；「隐蔽」同样无视指令（制作者词条 2026-09-24）
    if (opts.fromOrder && u.mods && (u.mods.ignoreOrders || u.mods.immuneOrders) || (opts.fromOrder && hasKw(u, 'conceal'))) {
      log(state, u.name + ' 无视指令，未受影响', 'keyword');
      return 0;
    }
    // 单位效果免疫：只挡"单位技能造成的伤害/效果"，**不挡战斗伤害与反击**
    if (!opts.combat && source && source.uid && u.mods && (u.mods.immuneUnitEffects || u.mods.ignoreEnemyEffects)
      && source.owner != null && source.owner !== u.owner && !opts.fromOrder) {
      log(state, u.name + ' 无视敌方单位效果', 'keyword');
      return 0;
    }
    // 磁反应装甲：免疫层消耗
    if (u.magneticCharges > 0 && !opts.pierce) {
      u.magneticCharges--;
      log(state, u.name + ' 的磁反应装甲抵挡了这次伤害（剩余 ' + u.magneticCharges + ' 层）', 'keyword');
      if (u.magneticCharges <= 0) delete u.kws.magnetic;
      return 0;
    }
    if (opts.fromOrder && u.mods && u.mods.orderDamageDouble) dmg *= 2;
    // ★ 牌Q 卡包：「使敌方所有指令造成的伤害-1」→ 按**受伤方玩家**记账的指令伤害修正
    if (opts.fromOrder) {
      const _op = state.players[u.owner];
      if (_op && _op.orderDamageMod) dmg = Math.max(0, dmg + _op.orderDamageMod);
    }
    if (opts.attacker && u.mods && u.mods.takeDoubleFrom) {
      const t = opts.attacker.unitType;
      if (t && u.mods.takeDoubleFrom[t]) dmg *= 2;
    }
    if (opts.attacker && u.mods && u.mods.vsType && u.mods.vsType[opts.attacker.unitType]) {
      const rule = u.mods.vsType[opts.attacker.unitType];
      if (rule.takeDamage) dmg += rule.takeDamage;
    }
    dmg += (u.mods && u.mods.extraDamageTaken) || 0;
    // ★ 用 armorAgainst（按来源区分）：重甲只挡对战伤害（armorVsOrder 卡面例外可挡指令）、轻甲只挡步兵伤害
    //   ⚠ **硬铝弹（tsekep）/无视对战词条（ignoreCombatKw）**的攻击：
    //   无视重甲、轻甲、强磁护盾、海绵装甲这四类对战词条（armorBonus 是卡牌数值加成，不属词条，保留）。
    if (!opts.pierce) {
      const atkU = opts.attacker || null;
      const pierceArmorKw = !!(opts.ignoreCombatKw || ignoresCombatKeywords(atkU));
      dmg = Math.max(0, dmg - (pierceArmorKw ? (u.armorBonus || 0) : armorAgainst(u, opts, source)));
    }
    if (dmg <= 0) { log(state, u.name + ' 的护甲完全抵挡了伤害', 'damage'); return 0; }
    // 偏转护盾：将**致命**伤害转移给另一个目标（反制卡「偏转护盾」，或 mod deflect）
    if (!opts.noDeflect && dmg >= u.defense) {
      const byMod = !!(u.mods && u.mods.deflect);
      const byCounter = byMod ? null : consumeInterrupt(state, u.owner, 'fatalDamage', { victim: u, amount: dmg });
      if (byMod || byCounter) {
        const others = [0, 1].reduce(function (acc, i) { return acc.concat(allUnitsOf(state, i)); }, [])
          .filter(function (x) { return x.uid !== u.uid && !x.dead; });      // 不分敌我
        if (others.length) {
          const pick = others[Math.floor(state.rng() * others.length)];
          log(state, u.name + ' 的偏转护盾把 ' + dmg + ' 点致命伤害转给了 ' + pick.name, 'keyword');
          return damageUnit(state, pick, dmg, source, Object.assign({}, opts, { noDeflect: true }));
        }
      }
    }
    u.defense -= dmg;
    u.lastDamageTaken = dmg;
    if (hasKw(u, 'mobilize')) { delete u.kws.mobilize; log(state, u.name + ' 受伤，失去动员', 'keyword'); }
    if (source && source.uid) {
      u.damagedBy = u.damagedBy || {};
      u.damagedBy[source.uid] = true;
    }
    log(state, u.name + ' 受到 ' + dmg + ' 点伤害（剩余 ' + Math.max(0, u.defense) + '/' + u.maxDefense + '）', 'damage');
    // ★ 演出：非对战伤害（单位效果 / 指令 / 相互伤害）记一条，UI 会播"来源冲刺 + 命中 + 飘伤害"。
    if (!opts.combat && !opts.silentFx) {
      fxPush(state, {
        k: 'dmg', tgt: 'unit', uid: u.uid, amount: dmg,
        src: (source && source.uid != null) ? source.uid : null,
        srcType: (source && source.unitType) || null,
      });
    }
    const notify = function () {
      if (!opts.silentEvent) runTrigger(state, { trigger: 'friendlyDamaged', owner: u.owner, source: source, victim: u, amount: dmg });
      runTrigger(state, { trigger: 'damaged', owner: u.owner, source: u, amount: dmg, bySource: source, attacker: opts.attacker });
      if (u.defense <= 0) return killUnit(state, u, source);
    };
    if (state.__combatDamageBatch) state.__combatDamageBatch.push(notify);
    else { const pending = notify(); if (pending && typeof pending.then === 'function') return pending.then(() => dmg); }
    return dmg;
  }
  KG.damageUnit = damageUnit;

  function ignoresCombatKeywords(u) {
    return !!(u && ((u.kws && (u.kws.tsekep || u.kws.ignoreCombatKw)) || (u.mods && u.mods.ignoreCombatKw)));
  }
  KG.ignoresCombatKeywords = ignoresCombatKeywords;

  function revealCovert(state, u) {
    if (!u || !hasKw(u, 'conceal')) return;
    delete u.kws.conceal;
    u.revealed = true;
    log(state, u.name + ' 揭露', 'keyword');
    runTrigger(state, { trigger: 'revealed', owner: u.owner, source: u, unit: u });
  }
  KG.revealCovert = revealCovert;

  async function flushCombatDamage(state) {
    const notifications = state.__combatDamageBatch || [];
    delete state.__combatDamageBatch;
    state.__combatDeaths = [];
    // 先记录双方伤害/死亡，再运行亡计，防止亡计插入两段互伤之间。
    notifications.forEach(function (notify) { notify(); });
    const deaths = state.__combatDeaths;
    delete state.__combatDeaths;
    for (const entry of deaths) await killUnit(state, entry.unit, entry.source);
  }
  KG.flushCombatDamage = flushCombatDamage;

  function healUnit(state, u, amount) {
    if (!u || u.dead) return healUnitBody(state, u, amount);
    return KG.performImmediateBehavior(state, 'game', 'healUnit', { owner: u.owner, source: u, unit: u, amount },
      () => healUnitBody(state, u, amount));
  }
  function healUnitBody(state, u, amount) {
    if (!u || u.dead) return;
    const before = u.defense;
    u.defense = Math.min(u.maxDefense, u.defense + amount);
    if (u.defense !== before) log(state, u.name + ' 恢复 ' + (u.defense - before) + ' 点防御力', 'heal');
  }
  KG.healUnit = healUnit;

  function killUnit(state, u, source) {
    if (!u || u.dead || state.__combatDeaths) return u && killUnitBody(state, u, source);
    return KG.performImmediateBehavior(state, 'game', 'killUnit', { owner: source?.owner ?? u.owner,
      source: source || u, unit: u, victim: u, deadUnit: u }, () => killUnitBody(state, u, source));
  }
  function killUnitBody(state, u, source) {
    if (u.dead) return;
    if (state.__combatDeaths) {
      if (!state.__combatDeaths.some(function (entry) { return entry.unit === u; })) state.__combatDeaths.push({ unit: u, source: source });
      return;
    }
    u.dead = true;
    removeUnitFromBoard(state, u);
    const p = state.players[u.owner];
    p.unitsLostThisGame = (p.unitsLostThisGame || 0) + 1;
    // ★ 2026-09-22：「本回合累计」——供「上一回合被消灭的单位数量」这类效果取值。
    //   在 beginTurn 里快照成 unitsLostLastTurn 再清零（见 turnStart 之前那段）。
    p.unitsLostThisTurn = (p.unitsLostThisTurn || 0) + 1;
    log(state, u.name + ' 被消灭', 'death');
    if (source && source.owner != null && source.owner !== u.owner) {
      const killer = KG.unitByUid(state, source.uid);
      if (killer) {
      killer.kills = (killer.kills || 0) + 1;
      if (hasKw(source, 'salvage') && state.active === source.owner) {
        const recovered = makeHandInst(u.cardId);
        recovered.costSet = Math.min(3, (u.def && u.def.cost) || 0);
        recovered.mods = { attackSet: 1, defenseSet: 1 };
        if (state.players[source.owner].hand.length < RULES.handMax) pushToHand(state, state.players[source.owner], recovered);
        else state.players[source.owner].discard.push(u.cardId);
        log(state, source.name + ' 打捞了 ' + u.name + ' 的 1/1 副本', 'keyword');
      }
      tryVeteran(state, killer);
      // 「本单位消灭一个敌方单位后…」
      runTrigger(state, { trigger: 'afterKill', owner: killer.owner, source: killer, killedUnit: u, defender: u });
      // ★ 2026-09-22：「**友方**消灭**敌方**单位时…」——**监听语义**（同一方任意单位消灭敌方单位都算），
      //   与 afterKill 的"本单位"自身语义不同。卡面「友方消灭敌方单位时，获得等同于该敌方单位
      //   的花费的指挥点」用的就是这条。
      //   ⚠ 事件必须带 `deadUnit`：效果的 `{stat:'cost', of:'eventUnit'}` 走 refUnit，
      //     而 refUnit 对 eventUnit 是**优先读 ev.deadUnit** 的（取"被消灭单位的花费"）。
      runTrigger(state, { trigger: 'enemyKilled', owner: killer.owner, source: killer, deadUnit: u, killedUnit: u, defender: u });
    }
    }
    // 亡计 = 只有本单位死亡时触发（trigger:'death'）
    let suppressed = u.deathSuppressed || (u.mods && u.mods.suppressDeathrattle);
    // 反制「突袭」：一个单位的亡计**触发前**将其抑制（先问对手，再问自己）
    if (!suppressed && (u.def.effects || []).some(function (e) { return e.trigger === 'death'; })) {
      if (consumeInterrupt(state, 1 - u.owner, 'deathrattle', { victim: u })) suppressed = true;
      else if (consumeInterrupt(state, u.owner, 'deathrattle', { victim: u })) suppressed = true;
    }
    if (suppressed) u.deathSuppressed = true;
    const dr = (suppressed ? [] : (u.silenced ? [] : (u.def.effects || [])))
      .concat(u.extraEffects || []).filter(function (e) { return e.trigger === 'death'; });
    if (dr.length) execEffects(state, { owner: u.owner, source: u, unit: u, card: u.def }, dr, null, 'death');
    else if (suppressed && (u.def.effects || []).some(function (e) { return e.trigger === 'death'; })) {
      log(state, u.name + ' 的亡计被抑制', 'keyword');
    }
    p.discard.push(u.cardId);
    // 「友方单位被消灭时」这类监听效果用 trigger:'friendlyDeath'，不会互相触发亡计
    runTrigger(state, { trigger: 'friendlyDeath', owner: u.owner, source: u, deadUnit: u, unitType: u.unitType, cardType: u.cardType });
    recomputeAuras(state);
    checkWin(state);
  }
  KG.killUnit = killUnit;

  function tryVeteran(state, u, force) {
    if (!u || u.dead) return;
    const def = u.def;
    /* ⚠ 守卫只认 isVeteran，**别加 u.veteran**：OPS.upgradeSelf 在条件未满足时会先把
     *   u.veteran 标上（星盟/u/-23：unitDeployed 触发 upgradeSelf → 先标记 →
     *   同一事件里 upgradeCheck 计满再 force 调进来）→ 加了就永远换不了卡。 */
    if (u.isVeteran) return;
    const ok = force || (def.upgradeKills && u.kills >= def.upgradeKills);
    if (!ok) return;
    /* ★ 老兵形态怎么找（2026-09-24 制作者定版口径）：
     *   ① def.upgradeTo 手写指针优先（星盟/u/-23→-24、鲨鱼师→-6、空投师→_9b）。
     *   ② 没写指针 → **去卡池找「同名 + 老兵词条」的卡**（这才是老兵形态的通用定义：
     *      数据里三对形态卡全是"同名 + kwMap.veteran"模式，upgradeTo 只是把同一件事写死了一遍）。
     *   ③ 池里连同名老兵形态都没有 → 这张卡本来就没有老兵形态，什么都不发生
     *      （**不要 +1+1**——那不是 KARDS 的老兵，制作者已否决）。 */
    if (!def.upgradeTo) {
      const pool = (state && state.pool) || KG.pool;
      /* ⚠ 池里的 def 没经过 makeUnit 的词条归一，自定义卡可能是中文 key（「老兵」）→
       *   对候选卡的 kwMap 整个归一后再判（别只查一个 key —— kwCn2Key('老兵') 本身就返回
       *   'veteran'，查 c.kwMap[kwCn2Key('老兵')] 等于又查了一遍英文 key，白搭）。 */
      const vet = pool && Object.keys(pool).map(function (id) { return pool[id]; }).filter(function (c) {
        return c && c.id !== def.id && c.name === def.name && normalizeKwMap(c.kwMap).veteran;
      });
      if (vet && vet.length) {
        def.upgradeTo = vet[0].id; /* 记在 def 上，下次直接走换卡路径（运行时缓存，不落盘） */
      } else {
        return; /* 没有老兵形态卡：升不了就是升不了 */
      }
    }
    {
      const up = cardDef(state, def.upgradeTo);
      log(state, u.name + ' 升为老兵：' + up.name, 'keyword');
      const z = KG.zoneOf(state, u.uid);
      const nu = makeUnit(state, def.upgradeTo, u.owner);
      nu.zone = z ? z.zone : 'support';
      nu.canAct = false;
      nu.isVeteran = true;
      nu.kills = u.kills;
      if (z) {
        if (z.zone === 'frontline') {
          const i = state.frontline.findIndex(function (x) { return x.uid === u.uid; });
          if (i >= 0) state.frontline[i] = nu;
        } else {
          const arr = z.player.support;
          const i = arr.findIndex(function (x) { return x.uid === u.uid; });
          if (i >= 0) arr[i] = nu;
        }
      }
      u.dead = true;
      return nu;
    }
  }
  KG.tryVeteran = tryVeteran;

  // 事件驱动的老兵升级（卡面："友方部署一辆Mk坦克后，升为老兵"）
  function upgradeCheck(state, ev) {
    for (const oi of [0, 1]) {
      allUnitsOf(state, oi).slice().forEach(function (u) {
        const up = u.def && u.def.upgradeOn;
        if (!up || u.dead) return;
        if (up.trigger && up.trigger !== ev.trigger) return;
        if (up.side === 'self' && ev.owner !== u.owner) return;
        if (up.side === 'enemy' && ev.owner === u.owner) return;
        if (up.nameIncludes && !(ev.name && String(ev.name).indexOf(up.nameIncludes) >= 0)) return;
        if (up.unitType && ev.unitType !== up.unitType) return;
        // ★ 「友方部署一辆游击单位后升为老兵」这类**词条限定**：交给 matchFilter（同一套口径）。
        //   ⚠ 原来只认 nameIncludes / unitType：词条写法会退化成"名字里含『游击单位』"
        //     （名字里根本没有这五个字）→ 永远不升级。
        if (up.filter && ev.source && !matchFilter(state, ev.source, up.filter, u.owner)) return;
        if (up.excludeSelf && ev.source && ev.source.uid === u.uid) return;
        u.upgradeProgress = (u.upgradeProgress || 0) + 1;
        if (u.upgradeProgress >= (up.count || 1)) tryVeteran(state, u, true);
      });
    }
  }
  // 前线容量：默认 5，可被场上单位的 passiveRules.frontlineMaxOverride 覆盖（取最小值）
  KG.effectiveFrontlineMax = function (state, pi) {
    let n = RULES.frontlineMax;
    allUnitsOf(state, pi).forEach(function (u) {
      const v = u.mods && u.mods.frontlineMaxOverride;
      if (v != null) n = Math.min(n, v);
    });
    return n;
  };

  KG.upgradeCheck = upgradeCheck;

  // 总部免疫可以来自：总部自身的词条，或**场上友方单位**提供的持续效果（例如戒严令）
  // —— 单位被消灭后，这份免疫自然就没了
  function hqImmuneSource(state, pi) {
    const p = state.players[pi];
    if (p.hqKws && p.hqKws.immune) return '总部词条';
    const u = allUnitsOf(state, pi).find(function (x) { return !x.dead && (hasKw(x, 'hqImmune') || (x.mods && x.mods.hqImmune)); });
    return u ? u.name : null;
  }
  KG.hqImmuneSource = hqImmuneSource;

  // 偏转护盾：致命伤害转给另一个目标 —— 实际结算在 damageUnit 里
  // （反制卡「偏转护盾」= 打断型反制 interrupt:'fatalDamage'；单位 mod 'deflect' 也走同一条路）

  function damageHQ(state, p, amount, reason, source, opts) {
    if (!p || amount <= 0) return;
    return KG.performImmediateBehavior(state, 'game', 'damageHQ', { owner: source?.owner ?? p.idx,
      source: source || null, amount, player: p.idx }, () => damageHQBody(state, p, amount, reason, source, opts));
  }
  function damageHQBody(state, p, amount, reason, source, opts) {
    if (amount <= 0) return;
    const imm = hqImmuneSource(state, p.idx);
    if (imm) { log(state, p.name + ' 的总部具有免疫（' + imm + '）', 'keyword'); return; }
    const dmg = Math.max(0, amount - (p.hqArmor || 0));
    p.hq -= dmg;
    log(state, p.name + ' 的总部受到 ' + dmg + ' 点伤害（剩余 ' + Math.max(0, p.hq) + '）' + (reason ? ' · ' + reason : ''), 'hq');
    // ★ 演出：总部挨打也要有特效（冲击 + 飘伤害）。对战攻击已由 doAttack 播冲刺，标 combat 跳过。
    if (!(opts && opts.combat) && !(opts && opts.silentFx)) {
      fxPush(state, {
        k: 'dmg', tgt: 'hq', player: p.idx, amount: dmg,
        src: (source && source.uid != null) ? source.uid : null,
        srcType: (source && source.unitType) || null,
      });
    }
    // ★ 2026-09-22：把**伤害来源单位**带进事件 —— 卡面「友方总部受到来自陆军的伤害时」
    //   靠它判"这一下是不是陆军打的"（条件 CONDS.damageFromType 读 ev.source 的 unitType）。
    //   以前这里写死 source: null → 这类**来源限定**根本无从判定，就算编译出来也永远不触发。
    runTrigger(state, { trigger: 'hqDamaged', owner: p.idx, source: source || null, amount: dmg });
    checkWin(state);
  }
  KG.damageHQ = damageHQ;

  function healHQ(state, p, amount) {
    if (!p) return;
    return KG.performImmediateBehavior(state, 'game', 'healHQ', { owner: p.idx, source: null, amount, player: p.idx },
      () => healHQBody(state, p, amount));
  }
  function healHQBody(state, p, amount) {
    p.hq = Math.min(p.hqMax, p.hq + amount);
    log(state, p.name + ' 的总部恢复 ' + amount + ' 点防御力', 'heal');
  }
  KG.healHQ = healHQ;

  function checkWin(state) {
    if (state.over) return;
    const a = state.players[0].hq <= 0, b = state.players[1].hq <= 0;
    if (a && b) { state.over = true; state.winner = -1; log(state, '双方总部同时被摧毁 —— 平局', 'system'); }
    else if (a) { state.over = true; state.winner = 1; log(state, state.players[1].name + ' 获胜！', 'system'); }
    else if (b) { state.over = true; state.winner = 0; log(state, state.players[0].name + ' 获胜！', 'system'); }
  }
  KG.checkWin = checkWin;

  /* ------------------------------------------------------------- 目标与选择 */
  async function ask(chooser, req) {
    if (!chooser) return req.options && req.options.length ? req.options[0].value : null;
    const v = await chooser(req);
    return v;
  }
  KG.ask = ask;

  /* 卡面级**动态**费用数值：按"场上单位筛选 + 统计量"算一个数字。
   *   spec = { side:'friendly'(默认)|'enemy', filter:{…} | unitType, stat:'maxAttack'|'maxDefense'|'sumAttack'|'count', times? }
   * 用途：「降低等同于场上友方轰炸机最高攻击力的费用」→ cardFields.costModByStat。 */
  function boardStatValue(state, pi, spec) {
    if (!spec) return 0;
    const sides = spec.side === 'both' ? [0, 1] : [spec.side === 'enemy' ? 1 - pi : pi];
    let list = [];
    sides.forEach(function (si) { allUnitsOf(state, si).forEach(function (u) { list.push(u); }); });
    const f = spec.filter || (spec.unitType ? { unitType: spec.unitType } : null);
    if (f && Object.keys(f).length) list = list.filter(function (u) { return matchFilter(state, u, f, pi); });
    const stat = spec.stat || 'maxAttack';
    let v = 0;
    if (stat === 'count') v = list.length;
    else if (stat === 'sumAttack') v = list.reduce(function (s, u) { return s + (u.attack || 0); }, 0);
    else {
      v = list.reduce(function (mx, u) {
        const cur = stat === 'maxDefense' ? (u.maxDefense || 0) : stat === 'defense' ? (u.defense || 0) : (u.attack || 0);
        return cur > mx ? cur : mx;
      }, 0);
    }
    return spec.times != null ? v * spec.times : v;
  }
  KG.boardStatValue = boardStatValue;

  /* ------------------------------------------------------------------ 出牌 */
  function instCost(state, pi, inst, def) {
    if (inst.costSet != null) return inst.costSet;
    let c = def.cost == null ? 0 : def.cost;
    c += (inst.costMod || 0);
    const p = state.players[pi];
    c += (p.costMod || 0) + (p.flags.handCostMod || 0);
    // 「友方补给具有减一花费」：场上友方单位带 supplyCostMod 时，名为"补给"的牌减费
    if (def.name && def.name.indexOf('补给') >= 0) {
      allUnitsOf(state, pi).forEach(function (x) {
        const m2 = x.mods && x.mods.supplyCostMod;
        if (m2) c += m2;
      });
    }
    if (def.costModPerOrder) {
      if (inst.ordersAtEntry === undefined) inst.ordersAtEntry = p.ordersPlayedThisGame || 0;   // 兜底：首次计算时快照
      const n = Math.max(0, (p.ordersPlayedThisGame || 0) - inst.ordersAtEntry);
      c -= def.costModPerOrder * n;
    }
    if (def.costModPerIntel) {
      if (inst.intelAtEntry === undefined) inst.intelAtEntry = p.intelCardsPlayed || 0;
      const n = Math.max(0, (p.intelCardsPlayed || 0) - inst.intelAtEntry);
      c -= def.costModPerIntel * n;
    }
    // 通用："在手牌中时，友方每使用一张<某词条>牌，获得-N花费"（原语组合器输出 cardFields）
    if (def.costModPerKeyword && def.costModPerKeyword.kw) {
      const kk = def.costModPerKeyword.kw;
      inst.kwAtEntry = inst.kwAtEntry || {};
      if (inst.kwAtEntry[kk] === undefined) inst.kwAtEntry[kk] = (p.kwCardsPlayed && p.kwCardsPlayed[kk]) || 0;
      const played = (p.kwCardsPlayed && p.kwCardsPlayed[kk]) || 0;
      c += (def.costModPerKeyword.amount || -1) * Math.max(0, played - inst.kwAtEntry[kk]);
    }
    // 动态费用：「降低等同于场上友方轰炸机最高攻击力的费用」这类（原语组合器输出 cardFields.costModByStat）
    // ★ 牌Q 卡包（2026-09-25）：「（若在手中）友方每被消灭一个单位，花费 +N（最多 M）」
    //   —— p.unitsLostThisGame 引擎早就在记，直接拿来用。
    if (def.costModPerFriendlyDeath) {
      c += def.costModPerFriendlyDeath * (state.players[pi].unitsLostThisGame || 0);
      if (def.costModMax != null) c = Math.min(c, def.costModMax);
    }
    if (def.costModByStat) {
      const sp = def.costModByStat;
      // 带前置条件时先求值（「若你控制轰炸机，降低等同于场上友方轰炸机最高攻击力的费用」）
      const condOk = !sp.condition || !(KG.effects && KG.effects.evalCond) ||
        KG.effects.evalCond(state, { owner: pi, card: def, vars: {} }, sp.condition);
      if (condOk) c -= boardStatValue(state, pi, sp);
    }
    return Math.max(0, c);
  }
  KG.instCost = instCost;

  KG.canPlayCard = function (state, pi, handIdx) {
    const p = state.players[pi];
    if (state.over || state.active !== pi) return { ok: false, why: '不是你的回合' };
    const inst = p.hand[handIdx];
    if (!inst) return { ok: false, why: '没有这张牌' };
    const def = cardDef(state, inst.id);
    // 挂起的反制卡**不能再被打出**：它已经在等待触发（原版行为）
    if (inst.suspended) return { ok: false, why: '这张反制已挂起，正在等待触发（点它可以取消）' };
    // ★ 牌Q 卡包（撒塔罗斯28师）：「若本回合额外获得指挥点不大于N，无法部署」
    if (def.deployGateExtraKreditsMax != null && (p.extraKreditsThisTurn || 0) <= def.deployGateExtraKreditsMax) {
      return { ok: false, why: '本回合额外获得指挥点不超过 ' + def.deployGateExtraKreditsMax + '，无法部署' };
    }
    const cost = instCost(state, pi, inst, def);
    if (cost > p.kredits) return { ok: false, why: '指挥点不足（需要 ' + cost + '）' };
    // 支援阵线已满就不能再下单位；但**空投**单位不受此限（卡面明写可直接进场）。
    if (def.cardType === 'unit' && p.support.length >= RULES.supportMax && !(def.kwMap && def.kwMap.airdrop)) {
      return { ok: false, why: '支援阵线已满（' + RULES.supportMax + '）' };
    }
    if (def.targetsNeeded && def.targetsNeeded.length && !hasValidTargets(state, pi, def)) {
      return { ok: false, why: '没有合法目标' };
    }
    return { ok: true, cost: cost };
  };

  function hasValidTargets(state, pi, def) {
    const fromOrder = def.cardType === 'order' || def.cardType === 'counter';
    for (const s of (def.targetsNeeded || [])) {
      const spec = Object.assign({}, s, { fromOrder: fromOrder, unitSource: !fromOrder });
      if (KG.enumerateTargets(state, pi, spec).length === 0 && !s.optional) return false;
    }
    return true;
  }
  KG.hasValidTargets = hasValidTargets;

  function enumerateTargets(state, pi, spec) {
    const out = [];
    const side = spec.side || 'any';
    const zones = spec.zone === 'frontline' ? ['frontline'] : spec.zone === 'support' ? ['support'] : ['support', 'frontline'];
    const idxs = side === 'enemy' ? [1 - pi] : side === 'friendly' ? [pi] : [pi, 1 - pi];
    if (spec.kind !== 'hq' && spec.sel !== 'hqOnly') {
      idxs.forEach(function (oi) {
        zones.forEach(function (z) {
          unitsInZone(state, oi, z).forEach(function (u) {
            if (u.dead) return;
            if (!matchFilter(state, u, spec.filter, pi)) return;
            if ((spec.fromOrder || spec.orderSource || spec.unitSource) && hasKw(u, 'conceal')) return;
            // ★★ 烟幕**不再**阻止被指定为目标（制作者 2026-09-24：「烟幕只是不能被攻击」）。
            //    这里**故意不写** hasKw(u,'smokescreen') 过滤 —— 加了等于让烟幕单位
            //    免疫一切指向性效果（消灭/伤害/压制…），比卡面口径大得多。
            //    唯一的限制在 canAttack：「目标处于烟幕中，无法被攻击」。
            if (oi !== pi && u.mods) {
              // 无法被指令指向 / 无视指令：都不能成为指令的"指定目标"
              if ((spec.fromOrder || spec.orderSource) && (u.mods.immuneOrder || u.mods.ignoreOrders || u.mods.immuneOrders || hasKw(u, 'conceal'))) return;
              // 单位效果免疫：敌方单位的效果无法指定它
              if (spec.unitSource && (u.mods.immuneUnitEffects || u.mods.ignoreEnemyEffects)) return;
            }
            out.push({ kind: 'unit', value: u.uid, unit: u, label: u.name + '（' + u.attack + '/' + u.defense + '）' });
          });
        });
      });
    }
    if (spec.kind === 'hq' || spec.kind === 'any' || spec.allowHQ) {
      idxs.forEach(function (oi) {
        if(spec.filter && spec.filter.where && KG.effects && KG.effects.composition) {
          const candidate=Object.assign({},state.players[oi],{kind:'player',owner:oi,zone:'hq'});
          if(!KG.effects.composition.test(spec.filter.where,state,state.__compositionCtx||{owner:pi,source:state.__ctxUnit,vars:{}},candidate))return;
        }
        out.push({ kind: 'hq', value: 'hq' + oi, player: oi, label: state.players[oi].name + '的总部（' + state.players[oi].hq + '）' });
      });
    }
    return out;
  }
  KG.enumerateTargets = enumerateTargets;

  function matchFilter(state, u, f, pi) {
    if (f && f.where && KG.effects && KG.effects.composition && !KG.effects.composition.test(f.where, state, state.__compositionCtx || {owner: pi, source: state.__ctxUnit, vars: {}}, u)) return false;
    if (!f) return true;
    if (f.adjacentTo) {
      // 相邻：与指定单位在同一条阵线且位置相邻
      const src = f.adjacentTo === 'self' ? (state.__ctxUnit || null) : KG.unitByUid(state, f.adjacentTo);
      const ref = src || (state.__ctxUnit || null);
      if (ref) {
        if (!KG.unitNeighbors(state, ref).some(x => x.uid === u.uid)) return false;
      }
    }
    if (f.damagedBySelf) {
      const src = state.__ctxUnit;
      if (!src || !(u.damagedBy && u.damagedBy[src.uid])) return false;
    }
    if (f.hasMod) {
      // 按单位的战斗修正筛选（例如"无法被指令指向的单位"）
      // hasMod:'immuneOrder' 同时匹配 immuneOrder（无法被指令指向）与 ignoreOrders（无视指令）
      const parts = String(f.hasMod).split('.');
      const readMod = (obj) => { let v = obj; for (const p of parts) { v = v && v[p]; } return v; };
      let v = readMod(u.mods);
      if (!v && f.hasMod === 'immuneOrder') v = readMod(u.mods) || (u.mods && (u.mods.ignoreOrders || u.mods.immuneOrders));
      if (!v) return false;
    }
    if (f.notMod) {
      const parts = String(f.notMod).split('.');
      let v = u.mods;
      for (const p of parts) { v = v && v[p]; }
      if (v) return false;
    }
    if (f.unitType) {
      const types = Array.isArray(f.unitType) ? f.unitType : [f.unitType];
      if (!types.some(function (t) { return isType(u, t); })) return false;
    }
    if (f.notUnitType && isType(u, f.notUnitType)) return false;
    if (f.anyUnitType && !(Array.isArray(f.anyUnitType) ? f.anyUnitType : [f.anyUnitType]).some(function (t) { return isType(u, t); })) return false;
    if (f.keyword && !hasKw(u, f.keyword)) return false;
    if (f.notKeyword && hasKw(u, f.notKeyword)) return false;
    if (f.maxAttack != null && u.attack > f.maxAttack) return false;
    if (f.minAttack != null && u.attack < f.minAttack) return false;
    if (f.maxCost != null && (u.def.cost || 0) > f.maxCost) return false;
    if (f.minCost != null && (u.def.cost || 0) < f.minCost) return false;
    if (f.maxDefense != null && u.defense > f.maxDefense) return false;
    if (f.minDefense != null && u.defense < f.minDefense) return false;
    if (f.zone && u.zone !== f.zone) return false;
    if (f.damaged && u.defense >= u.maxDefense) return false;
    if (f.name && String(u.name).indexOf(f.name) < 0) return false;
    if (f.set && u.def.set !== f.set) return false;
    if (f.setIn && (Array.isArray(f.setIn) ? f.setIn : [f.setIn]).indexOf(u.def.set) < 0) return false;
    // ★ 「体系」（system）：与 set / 兵种同级的"作战体系"维度（Mk坦克 / WgL战斗机 / 黑盾 / 航母 / 伞兵）。
    //   定义在卡牌元数据里（制卡台直接填写，不进 DSL、不进对战词条）。
    //   一张卡可以有**多个**体系 → system 支持数组；卡面写「所有 Mk坦克」时编译成 f.system。
    if (f.system) {
      const sys = u.def && u.def.system;
      const list = Array.isArray(sys) ? sys : ((sys == null || sys === '') ? [] : [sys]);
      const want = Array.isArray(f.system) ? f.system : [f.system];
      if (!want.some(function (w) { return list.indexOf(w) >= 0; })) return false;
    }
    if (f.cardId && u.cardId !== f.cardId) return false;
    // ★ 「其他单位 / 其它单位」= 排除效果来源自己（selectUnits 会把来源放在 state.__ctxUnit）
    if (f.excludeSelf) {
      const src0 = state.__ctxUnit;
      if (src0 && src0.uid === u.uid) return false;
    }
    return true;
  }
  KG.matchFilter = matchFilter;

  // 指令里若写了 counter 触发的效果，则这条指令会埋伏成反制措施（如「远程打击」）。
  // 这类"指令式反制"会让**指令卡本身**转为挂起态，不立即进弃牌堆。
  /* ★ 词条「山地 / 协力」的部署结算（制作者 2026-09-24；协力 2026-09-25 二次修订）。
   *   「部署时」= **打出**（playCard）这一刻；summon / deckToField 直接进场不触发。
   *   山地：场上每有一个友方山地单位（含自身，因为结算时本单位已进场）→ +1+1 一次。
   *   协力：**本回合开始时**场上没有同国家（同 def.set）的友方单位时，总部受一次「疲劳（士气）伤害」。
   *     ★ 判定读的是**回合开始那一刻的快照** p.nationsAtTurnStart（见 beginTurn），不是部署时的场面：
   *       部署时本单位已进场，若按当场数同国单位会永远 ≥1 —— 旧实现把词条整个做反了。
   *     伤害与 drawCard 的牌库疲劳**共用同一个递增计数**（RULES 12.27）：
   *       受 = (p.fatigue + 1) 点，然后 p.fatigue++（即"现在抽一张牌会受的点数"）。 */
  async function applyDeployTraits(state, u) {
    applyAlpine(state, u);
    await applySynergy(state, u);
  }

  function applyAlpine(state, u) {
    if (u.kws && u.kws.alpine) {
      const n = allUnitsOf(state, u.owner).filter(function (x) { return x.uid !== u.uid && hasKw(x, 'alpine'); }).length;
      if (n > 0) {
        u.attack += n; u.permAtk = (u.permAtk || 0) + n;
        u.maxDefense += n; u.defense += n; u.permDef = (u.permDef || 0) + n;
        log(state, u.name + ' 触发山地：场上友方山地 ×' + n + '，获得 +' + n + '+' + n, 'keyword');
      }
    }
    /* Alpine also applies when a unit is added; deployment-only traits stay
       in applyDeployTraits. */
  }
  KG.applyAlpine = applyAlpine;

  async function applySynergy(state, u) {
    if (u.kws && u.kws.synergy) {
      const set = (u.def && u.def.set) || '';
      const p = state.players[u.owner];
      // 只认「本回合开始时的快照」；快照缺失（没经过 beginTurn 的构造局）按"开始时没有"处理
      const had = !!(p.nationsAtTurnStart && p.nationsAtTurnStart[set]);
      if (!had) {
        p.fatigue = (p.fatigue || 0) + 1;
        await damageHQ(state, p, p.fatigue, '疲劳', null);
        log(state, u.name + ' 触发协力：本回合开始时没有同国（' + (set || '无系列') + '）友方单位，总部受 ' + p.fatigue + ' 点疲劳伤害', 'keyword');
      }
    }
  }

  KG.playCard = async function (state, pi, handIdx, chooser, opts) {
    opts = opts || {};
    const chk = KG.canPlayCard(state, pi, handIdx);
    if (!chk.ok) { log(state, '无法打出：' + chk.why, 'error'); return false; }
    const p = state.players[pi];
    const inst = p.hand[handIdx];
    const id = inst.id;
    const def = cardDef(state, id);
    const cost = chk.cost;
    p.kredits -= cost;
    // ★ 牌Q 卡包：「每使用过一张“X”，额外加入一张」→ 按卡名统计本局打出次数
    p.playedCount = p.playedCount || {};
    p.playedCount[def.name] = (p.playedCount[def.name] || 0) + 1;

    // ── 反制卡：挂起模型 ─────────────────────────────────────────────
    // 原版 KARDS 的反制是"埋设后留在手上（挂起态），被触发时才真正离手"。
    // 所以反制分支**不 splice 出手牌**，只标记 inst.suspended；
    // 真正移出手牌发生在 counterOnMatch / consumeInterrupt 命中那一刻。
    if (def.cardType === 'counter') {
      inst.suspended = true;
      inst.suspendedTurn = state.turn;
      const entry = {
        cardId: id, inst: inst, effects: def.effects || [],
        handIndexAtSet: handIdx,           // 埋设时刻的手牌下标（仅用于 UI 展示/排序参考）
        suspended: true,
        paidCost: cost,                    // ★ 埋设时实际支付的花费（取消挂起时要**返还**）
      };
      p.counters.push(entry);
      p.countersPlayedThisTurn = (p.countersPlayedThisTurn || 0) + 1;
      p.countersPlayedThisGame = (p.countersPlayedThisGame || 0) + 1;
      p.playedThisTurn++;
      log(state, p.name + ' 埋设了反制卡：' + def.name, 'play');
      await runTrigger(state, { trigger: 'counterSet', owner: pi, source: null, played: def, chooser: chooser });
      checkWin(state);
      if (state.forceEndTurn && !state.over) { state.forceEndTurn = false; await KG.endTurn(state, chooser); }
      return true;
    }

    p.hand.splice(handIdx, 1);
    // 上面 splice 掉后，若手牌下标有变化，同步已挂起反制卡记录的 handIndexAtSet（仅供参考，容错处理）
    syncSuspendedIndexes(p);

    const ctx = { owner: pi, source: null, card: def, cardId: id, inst: inst, vars: {}, chosen: {} };

    if (def.cardType === 'unit') {
      const toFront = (opts.toFrontline) || p.deployToFrontline;
      let zone = 'support';
      if (toFront && frontlineOf(state, 1 - pi).length === 0 && frontlineOf(state, pi).length < KG.effectiveFrontlineMax(state, pi)) zone = 'frontline';
      if (zone === 'support' && p.support.length >= RULES.supportMax) {
        p.hand.splice(handIdx, 0, inst); p.kredits += cost; log(state, '支援阵线已满', 'error'); return false;
      }
      const u = makeUnit(state, id, pi);
      applyCardInstance(state,u,inst);
      u.zone = zone;
      u.summonedTurn = state.turn;
      u.canAct = !!u.kws.blitz || (zone === 'frontline' && !!u.kws.airdrop);
      // 摆放位置：默认放在最右边，也可以由玩家指定槽位（opts.slotIndex）
      //   opts 原样透传（含 UI 部署的 hqDisplace:'left' —— 联机动作包同步携带）
      insertUnitAt(state, pi, u, zone, opts.slotIndex, opts);
      ctx.source = u; ctx.unit = u; ctx.uid = u.uid;
      log(state, p.name + ' 部署了 ' + def.name + '（' + u.attack + '/' + u.defense + '）到' + (zone === 'frontline' ? '前线' : '支援阵线'), 'play');
      p.unitsDeployedThisTurn++;
      p.playedThisTurn++;
      p.ordersPlayedThisGame = p.ordersPlayedThisGame;
      // ★ 打断型反制（如「远程打击」）：在单位**部署效果结算之前**拦截 ——
      //   单位一进场就被消灭，"部署：X"效果不结算（卡面："部署效果触发时将其反制，消灭该单位"）。
      const remote = hasKw(u, 'conceal') ? null : consumeInterrupt(state, 1 - pi, 'unitDeployed', { victim: u });
      if (remote) {
        // 执行这条反制的效果（destroy eventUnit），目标指向刚部署的单位
        const ctx2 = { owner: 1 - pi, source: null, counter: remote, card: cardDef(state, remote.cardId), event: { trigger: 'unitDeployed', owner: pi, source: u, unit: u }, _preFiltered: true, fromOrder: true };
        await execEffects(state, ctx2, remote.effects || [], chooser, 'counter');
      } else {
        await execEffects(state, ctx, (u.silenced ? [] : (def.effects || [])).concat(u.extraEffects || []), chooser, 'deploy');
        // ★ 手牌附魔「使用时」效果（grantEffectToHand 预挂 h.playEffects，2026-09-26 接通消费）：
        //   部署效果结算后执行；被反制（remote 分支）则不结算
        if (Array.isArray(inst.playEffects) && inst.playEffects.length) {
          await execEffects(state, ctx, inst.playEffects, chooser, 'play');
        }
      }
      await applyIntelKeyword(state, pi, def);
      // 「部署：」只属于刚部署的这个单位（上面已执行）。
      // 不能再派发一次全局 deploy 事件，否则别人身上的"部署："效果会被重复触发。
      await applyDeployTraits(state, u);          // ★ 山地 / 协力：部署时结算的一次性词条
      await runTrigger(state, { trigger: 'unitDeployed', owner: pi, source: u, chooser: chooser, unitType: u.unitType, name: u.name });
      recomputeAuras(state);
    } else {
      log(state, p.name + ' 使用了指令：' + def.name, 'play');
      p.ordersPlayedThisTurn++;
      p.ordersPlayedThisGame++;
      await applyIntelKeyword(state, pi, def);
      ctx.fromOrder = true;                       // 指令来源：用于"无法被指令指向 / 无视指令"的判定
      if (p.hqKws && p.hqKws.orderDoubleOnce) { ctx.orderDouble = true; p.hqKws.orderDoubleOnce = false; }
      /* ★ 牌Q（坐标）：「敌方指令指向友方单位时，将其反制」——**指令型打断**。
       *   反制被消费 → 这张指令的效果**整段不结算**（牌照常进弃牌堆），与 fatalDamage/deathrattle 同款同步消费。 */
      const counterOrder = consumeInterrupt(state, 1 - pi, 'order', { order: def });
      if (counterOrder) {
        const ctxc = { owner: 1 - pi, source: null, counter: counterOrder, card: cardDef(state, counterOrder.cardId),
          event: { trigger: 'orderPlayed', owner: pi }, _preFiltered: true, fromOrder: true };
        await execEffects(state, ctxc, counterOrder.effects || [], chooser, 'counter');
        log(state, p.name + ' 的指令「' + def.name + '」被反制，效果未结算', 'keyword');
        p.discard.push(id);
        return true;
      }
      await execEffects(state, ctx, def.effects || [], chooser, 'order');
      // ★ 手牌附魔「使用时」效果（grantEffectToHand 预挂 h.playEffects，2026-09-26 接通消费）：
      //   指令效果结算后执行；被反制（上面 counterOrder 早 return）则不结算
      if (Array.isArray(inst.playEffects) && inst.playEffects.length) {
        await execEffects(state, ctx, inst.playEffects, chooser, 'play');
      }
      // 指令里若写了 counter 触发的效果，则这条指令会埋伏成反制措施（如「远程打击」）
      const counterFx = (def.effects || []).filter(function (e) { return e.trigger === 'counter'; });
      if (counterFx.length) {
        // 挂起模型：指令卡留在手上（挂起态），触发（或到期取消）时才离手
        inst.suspended = true;
        inst.suspendedTurn = state.turn;
        inst.counterOnly = true;                  // 标记：这张牌以"反制"身份挂起
        // ★ 制作者口径：牌面"只有反制触发"的卡（如解体）就是反制措施 —— 打出后留在手上挂起，
        //   玩家看得见、能取消；下一个友方回合开始若仍未触发，自动取消挂起（回到普通手牌）。
        //   混合卡（还有当场生效的部分，如北极星计划）不塞回手牌，避免每回合白拿一次当场效果。
        const pureCounter = (def.effects || []).every(function (e) { return e.trigger === 'counter'; });
        if (pureCounter) {
          p.hand.splice(Math.max(0, Math.min(handIdx, p.hand.length)), 0, inst);
          syncSuspendedIndexes(p);
        }
        p.counters.push({ cardId: id, inst: inst, effects: counterFx, handIndexAtSet: handIdx,
          suspended: true, paidCost: cost, persistent: !pureCounter });
        log(state, p.name + ' 将 ' + def.name + ' 埋设为反制措施', 'keyword');
      } else {
        p.discard.push(id);
      }
      await runTrigger(state, { trigger: 'orderPlayed', owner: pi, source: null, played: def, chooser: chooser });
      await runTrigger(state, { trigger: 'order', owner: pi, source: null, played: def, chooser: chooser });
    }
    checkWin(state);
    if (state.forceEndTurn && !state.over) {
      state.forceEndTurn = false;
      await KG.endTurn(state, chooser);
    }
    return true;
  };

  /* ------------------------------------------------------------------ 攻击 */
  function effOpCost(state, pi, u) {
    if (hasKw(u, 'conceal')) return 1;
    // 行动花费光环：场上单位给的 opCostMod（例如"友方战斗机行动花费减1"）
    let auraOp = 0;
    allUnitsOf(state, pi).forEach(function (x) {
      (x.def.effects || []).forEach(function (e) {
        if (!e.aura || e.aura.opCostMod == null) return;
        if (e.condition) {
          const rc = { owner: x.owner, unit: x, source: x, vars: {} };
          if (KG.effects && KG.effects.evalCondProxy && !KG.effects.evalCondProxy(state, rc, e.condition)) return;
        }
        if (e.aura.target && e.aura.target.sel === 'all') {
          const f = e.aura.target.filter || {};
          if (f.unitType) {
            const types = Array.isArray(f.unitType) ? f.unitType : [f.unitType];
            if (!types.some(function (t) { return isType(u, t); })) return;
          }
          if (e.aura.target.excludeSelf && x.uid === u.uid) return;
          auraOp += e.aura.opCostMod;
        } else if (!e.aura.target || e.aura.target.sel === 'self') {
          if (x.uid === u.uid) auraOp += e.aura.opCostMod;
        }
      });
    });
    const def = u.def;
    let c = def.opCost || 0;
    if (c <= 0 && !(def.opCost > 0)) c = def.opCost || 0;
    c += (u.mods.opCostMod || 0);
    const p = state.players[pi];
    c += (p.opCostModGlobal || 0);
    if (u.instOpCostSet != null) c = u.instOpCostSet;
    return Math.max(0, c);
  }
  KG.effOpCost = effOpCost;

  /* ★ 牌Q 卡包（2026-09-25 制作者口径，二次修订）：天气是**两层**结构 ——
   *   ①「预报」给 4 张 0 费**引子**（蓝天/薄雾/狂风/落雪，功能相同：开发一张天气附加牌）；
   *   ②**天气附加牌**（2K 薄雾 / 4K 狂风 / 6K 落雪）才是真正的效果位置，打出后设置战场天气。
   *   天气是**战场级**状态（双方共享），薄雾=所有单位攻击-1 · 狂风=空军无法攻击 · 落雪=每回合结束全体 1 伤。
   *   ⚠ 昼/夜（白天黑夜）是另一套，制作者明确「昼夜转换不管」，别混。 */
  function weatherOf(state) { return (state && state.weather) || null; }
  KG.weatherOf = weatherOf;

  function attackPowerAgainst(state, a, target) {
    const m = a.mods || {};
    let doubleDamage=!!m.damageDouble;
    // 基础攻击力：巡航舰类有两套攻击值（卡面上下两个数字）
    let atk;
    const dual = a.def && a.def.dualAttack;
    if (dual && target && target !== 'hq') {
      const types = Array.isArray(dual.vsType) ? dual.vsType : [dual.vsType];
      const hit = types.some(function (t) { return isType(target, t); });
      atk = hit ? dual.value : dual.normal;
    } else if (dual) {
      // 敌方总部不是太空单位 → 走"否则"那一档（卡面 y）
      atk = dual.normal;
    } else {
      atk = a.attack;
    }
    // 薄雾：所有单位攻击力 -1（视野不清）
    if (weatherOf(state) === 'mist') atk -= 1;
    // 通常 attack 已包含永久/临时/光环增益；双攻击值从印刷值读取，补齐实例增益。
    if (dual) atk += a.attack - (a.baseAttack || 0);
    atk += (a.chargeAtkBonus || 0);   // ★ 复合充能武装：「充能完毕后…本单位攻击时，具有+N攻击力」（攻击后失去）
    // ★ 「具有X词条时，+N攻击力」（2026-09-26）：声明在 mods.condAtk = [{hasKw,amount}]
    //   （来自 passiveRules / grantMod）。**每次攻击现算**——词条在就有、失去即消失，
    //   无需任何撤销记账（与 vsType 同一套 stateless 思路）。
    if (m.condAtk) {
      (Array.isArray(m.condAtk) ? m.condAtk : [m.condAtk]).forEach(function (c) {
        if (c && c.hasKw && hasKw(a, c.hasKw)) atk += (c.amount || c.attack || 0);
      });
    }
    if (target && target !== 'hq') {
      const t = target;
      // ★ 「对战X」加成：**不能**只按 t.unitType 精确查表 ——
      //   key 可能是族别名（'space'）或否定族（'nonspace' = 地面单位），
      //   直接查会整条静默失效（LPD-7「对地面单位造成的伤害+4」曾经查不到）。
      //   统一用 isType 遍历判定：一个单位的多个 vsType 条目可以叠加。
      if (m.vsType) {
        const ctxA = { owner: a.owner };
        // 「每有…」会产生**动态值**（{count:spec,times:N}），普通加成是数字 ——
        //   这里内联求值（⚠ engine.js 里**没有** num()，那是 effects.js/primitives.js 的东西，
        //   不能直接调，否则 ReferenceError: num is not defined）。
        const addUp = (v) => {
          if (v == null) return 0;
          if (typeof v === 'number') return v;
          if (Array.isArray(v)) return v.reduce(function (s, x) { return s + addUp(x); }, 0);
          if (typeof v === 'object') {
            if (v.count != null) {
              const spec = Object.assign({ sel: 'all' }, v.count);
              const n = KG.effects.selectUnits(state, ctxA, spec).length;
              return v.times != null ? n * addUp(v.times) : n;
            }
            return 0;
          }
          const p = parseInt(v, 10);
          return isNaN(p) ? 0 : p;
        };
        Object.keys(m.vsType).forEach(function (k) {
          if (!isType(t, k)) return;
          const e = m.vsType[k] || {};
          // ★ attack = 对战该兵种时**攻击力**加成；damage = 对战该兵种时**造成的伤害**加成。
          //   两者都表现为打得更疼，但语义不同：attack 会被"攻击力"相关判定读到，damage 不会。
          //   （以前编译器把"伤害加1"也写成 attack → 「对战坦克时伤害加1」被落成 +1攻击力）
          atk += addUp(e.attack);
          atk += addUp(e.damage);
          if(e.double)doubleDamage=true;
        });
      }
      // 目标的 takeDamage（"对战坦克伤害减1"）只在 damageUnit 里结算一次，这里不再重复扣
    } else if (target === 'hq' && m.vsHq != null) {
      // ★ 「对战…和总部 +N攻击力 / +N伤害」：打总部时读专门的 vsHq 加成
      //   （编译时「和总部」产出一条 grantMod(mod:'vsHq')）
      //   ⚠ vsHq 兼容两种写法：数字（旧数据 = 纯攻击力）或对象 {attack, damage}。
      const hqb = m.vsHq;
      if(hqb&&typeof hqb==='object'&&hqb.double)doubleDamage=true;
      atk += (typeof hqb === 'object')
        ? ((hqb.attack || 0) + (hqb.damage || 0))
        : hqb;
    }
    atk += (m.extraDamageDealt || 0);
    if (m.vsArmor && target && target !== 'hq' && KG.armorOf(target) > 0) atk += m.vsArmor;
    if(target&&target!=='hq'&&m.doubleDamageFrontline&&KG.zoneOf(state,target.uid)?.zone==='frontline')doubleDamage=true;
    if(doubleDamage)atk*=2;
    return Math.max(0, atk);
  }
  KG.attackPowerAgainst = attackPowerAgainst;

  // 固守/守护：只保护"同一阵线"的目标
  //  - 攻击单位目标：只有与目标同处一条阵线的固守才会强制你改打它
  //  - 攻击总部：总部算"支援阵线"的目标，所以只有敌方【支援线】的固守才保护它（前线固守不管）
  function guardsBlocking(state, attacker, targetRef, foeIdx) {
    const guards = guardUnitsOf(state, foeIdx);
    if (!RULES.guardSameLineOnly) return guards;
    const wantZone = targetRef.kind === 'hq' ? 'support' : (targetRef.unit ? targetRef.unit.zone : null);
    let list = guards.filter(function (g) { return g.zone === wantZone; });
    // 「固守只守护左右两边的目标」：只有紧挨着的邻居被保护。
    //   ⚠ 总部虽然不在 support 数组里，但视觉上占支援线第 `hqSlot` 格（初始居中且会随插卡移动），
    //     所以"与总部相邻"必须按**视觉位置**换算（唯一真源见 ui.js）：
    //       单位数组下标 i → 视觉 = i + (hqSlot <= i ? 1 : 0)
    //     与总部（视觉 hqSlot）相邻 → 数组下标 hqSlot-1（左邻）或 hqSlot（右邻）。
    //     旧实现硬编码 `slotOf(g) === 0`，而总部并不总在最左端 → 固守单位明明站在总部旁边
    //     却护不住它（"守护守护不了总部"）。
    if (RULES.guardAdjacentOnly) {
      if (targetRef.kind === 'hq') {
        const foeP = state.players[foeIdx];
        const hqSlot = foeP.hqSlot != null ? foeP.hqSlot : 0;
        list = list.filter(function (g) {
          const s = slotOf(state, g);
          return s === hqSlot - 1 || s === hqSlot;
        });
      } else if (targetRef.unit) {
        const t = targetRef.unit;
        // ★ 规则 12.2：固守守护"相邻的非固守单位"；固守自身不获得「被守护」，
        //   也不守护另一个固守（否则 [守A][守B][守C] 会互相锁死）。
        //   所以当目标自己就是固守单位时，返回空列表 —— 没有任何固守"挡"在它前面。
        if (hasKw(t, 'guard')) return [];
        const visualSlot = function (u) {
          const s = slotOf(state, u), h = state.players[foeIdx].hqSlot || 0;
          return u.zone === 'support' ? s + (s >= h ? 1 : 0) : s;
        };
        const ts = visualSlot(t);
        list = list.filter(function (g) { return Math.abs(visualSlot(g) - ts) === 1; });
      }
    }
    return list;
  }
  KG.guardsBlocking = guardsBlocking;

  /* 目标优先级（制作者 2026-09-28 口径）：
   *  · 轰炸机攻击某目标前，必须先攻击**与目标同一阵线**的战斗机；
   *  · 巡地舰必须先攻击巡航舰（场上还有巡航舰时，只能以巡航舰为目标）。
   * 目标本身就是优先兵种 → 放行。烟幕中的单位打不了，不算可用的优先目标。 */
  function preferredBlocker(state, attacker, targetZone) {
    const foe = 1 - attacker.owner;
    const alive = allUnitsOf(state, foe).filter(function (u) { return !u.dead && !inSmoke(u); });
    if (isType(attacker, 'bomber')) {
      const f = alive.filter(function (u) { return isType(u, 'fighter') && u.zone === targetZone; });
      if (f.length) return { type: '与目标同阵线的战斗机', list: f };
    }
    if (isType(attacker, 'landcruiser')) {
      const c = alive.filter(function (u) { return isType(u, 'cruiser'); });
      if (c.length) return { type: '巡航舰（巡地舰必须先攻击巡航舰）', list: c };
    }
    return null;
  }

  KG.canAttack = function (state, pi, attackerUid, targetRef) {
    if (state.over || state.phase !== 'play') return { ok: false, why: '对局未开始或已结束' };
    const u = KG.unitByUid(state, attackerUid);
    if (!u || u.owner !== pi) return { ok: false, why: '不是你的单位' };
    if (state.active !== pi) return { ok: false, why: '不是你的回合' };
    if (!u.canAct || u.actionsLeft <= 0) return { ok: false, why: '本单位本回合已行动' };
    // ★ 0 攻也可以攻击（2026-09-26 Alan）：打 0 点伤害但合法 —— 可以踩烟幕外的 blocker、
    //   触发「攻击时」类效果、消耗行动次数；反击照常结算（0 攻撞高攻等于送，自己权衡）。
    //   旧实现一刀切 `u.attack <= 0` 拒绝，AI 侧本来就有 attack > 0 过滤，不受影响。
    const p = state.players[pi];
    const opCost = effOpCost(state, pi, u);
    if (opCost > p.kredits) return { ok: false, why: '行动花费不足（需要 ' + opCost + '）' };

    // 狂风：空中单位无法攻击（对单位/总部一视同仁 —— 原来这条误放在"攻击总部"分支里，
    //   空中单位照样能打单位；天气是战场级状态，这里统一拦截）
    if (weatherOf(state) === 'gale' && isType(u, 'air')) return { ok: false, why: '狂风天气，空中单位无法攻击' };
    if (targetRef.kind === 'hq') {
      const ti = targetRef.player;
      if (ti === pi) return { ok: false, why: '不能攻击自己的总部' };
      if (u.mods.cannotAttack) return { ok: false, why: '本单位无法攻击' };
      if (u.mods.noAttackHQ) return { ok: false, why: '本单位无法攻击总部' };
      if (u.zone !== 'frontline' && !RULES.hqRaidTypes.some(function (tp) { return isType(u, tp); })) return { ok: false, why: '只有在前线的单位才能攻击敌方总部（火炮/轰炸/太空/舰船可从支援阵线直攻）' };
      const ignoresGuardHQ = RULES.guardIgnoreTypes.some(function (tp) { return isType(u, tp); });
      const guardsHQ = guardsBlocking(state, u, { kind: 'hq' }, ti);
      // 巡地舰：非巡航舰的固守可以无视，巡航舰的固守必须先打
      const lcHQ = isType(u, 'landcruiser');
      const guardsHQEff = lcHQ ? guardsHQ.filter(function (g) { return isType(g, 'cruiser'); }) : guardsHQ;
      if (lcHQ ? guardsHQEff.length > 0 : (!ignoresGuardHQ && guardsHQ.length > 0)) {
        return { ok: false, why: lcHQ ? '巡地舰必须先攻击巡航舰的固守（非巡航舰固守可以无视）' : '对方支援线有固守单位，必须先攻击它' };
      }
      const prefHQ = preferredBlocker(state, u, 'support');
      if (prefHQ) return { ok: false, why: '必须先攻击' + prefHQ.type };
      const immSrc = hqImmuneSource(state, ti);
      if (immSrc) return { ok: false, why: '敌方总部具有免疫（' + immSrc + '）' };
      return { ok: true, opCost: opCost };
    }
    const t = KG.unitByUid(state, targetRef.uid);
    if (!t) return { ok: false, why: '目标不存在' };
    if (t.owner === pi) return { ok: false, why: '不能攻击自己的单位' };
    // ⚠ 走 inSmoke 而不是 hasKw：前线单位 / 带守护的单位**不可能**有烟幕，
    //   身上就算残留着 kws.smokescreen 也不能当成"在烟幕里"（禁则见 smokeBanned 注释）。
    if (inSmoke(t)) return { ok: false, why: '目标处于烟幕中，无法被攻击' };
    if (u.mods.noAttackAir && (t.unitType === 'fighter' || t.unitType === 'bomber')) return { ok: false, why: '本单位无法攻击空军' };
    // ★ 牌Q 卡包：「敌方空军无法攻击」→ 给这些单位挂 mods.cannotAttack（原来只判打总部那条分支）
    if (u.mods.cannotAttack) return { ok: false, why: '本单位无法攻击' };
    // 战线规则：支援阵线里的近战单位够不到敌方支援阵线
    if (RULES.lineRules && u.zone === 'support' && isMelee(u) && t.zone !== 'frontline') {
      return { ok: false, why: '支援阵线的近战单位只能攻击敌方前线单位（先把单位移上前线）' };
    }
    const tDef = t.def;
    if (tDef.effects && tDef.effects.some(function (e) { return e.noAttackIfNameOnBoard; })) {
      const name = e.noAttackIfNameOnBoard;
      const present = allUnitsOf(state, t.owner).some(function (x) { return x.uid !== t.uid && String(x.name).indexOf(name) >= 0; });
      if (present) return { ok: false, why: '场上存在' + name + '时无法被攻击' };
    }
    // ★ 目标优先级（制作者 2026-09-28）：轰炸机→与目标同阵线的战斗机；巡地舰→巡航舰
    const pref = preferredBlocker(state, u, t.zone);
    if (pref && !pref.list.some(function (x) { return x.uid === t.uid; })) {
      return { ok: false, why: '必须先攻击' + pref.type };
    }
    const ignoresGuard = RULES.guardIgnoreTypes.some(function (tp) { return isType(u, tp); });
    // ★ 规则 12.2：固守只守护"相邻的非固守单位 + 相邻总部"；固守自身**不获得「被守护」**，
    //   因此攻击一个固守单位本身永远合法（固守的意义是"逼你先打我"，而不是"我不能被打"）。
    //   所以当目标 t 自己就是固守单位时，直接放行，不再被它左右的固守拦截。
    //   （否则三固守连排 [A][B][C] 时，打 A 被 B 拦、打 B 被 A/C 拦、打 C 被 B 拦，
    //    互相锁死，一个都打不了 —— 正是"守护单位无法获得被守护"引发的 bug。）
    if (hasKw(t, 'guard')) return { ok: true, opCost: opCost };
    const guards = guardsBlocking(state, u, { kind: 'unit', unit: t }, t.owner);
    // 巡地舰：无视【非巡航舰】的固守，巡航舰的固守仍然拦路
    const lcOnly = isType(u, 'landcruiser');
    const guardsEff = lcOnly ? guards.filter(function (g) { return isType(g, 'cruiser'); }) : guards;
    if (!ignoresGuard && guards.length > 0) {
      return { ok: false, why: '同一阵线上有固守单位，必须先攻击它（火炮/轰炸机/巡地舰可以无视）' };
    }
    if (lcOnly && guardsEff.length > 0) {
      return { ok: false, why: '巡地舰必须先攻击巡航舰的固守（非巡航舰固守可以无视）' };
    }
    return { ok: true, opCost: opCost };
  };

  // 近战单位：步兵 / 坦克 / 工事（够不到远处战线）
  function isMelee(u) {
    if (!u) return true;
    return !['artillery', 'fighter', 'spacefighter', 'bomber', 'landcruiser', 'cruiser'].some(function (t) { return isType(u, t); });
  }
  KG.isMelee = isMelee;

  /* 「移动后是否保留行动」——即能不能**一回合内移动并攻击**（制作者 2026-09-19 口径）。
   *   默认：**只有坦克**（兵种全局规则）。
   *   另外两条是**卡面例外**（单卡声明，不是兵种默认能力）：
   *     · 游击词条：移动后仍可行动
   *     · 卡面原语 canMoveAndAttack：「本单位可以在同一回合内移动并攻击」
   *   抽成公开函数是为了让 UI/AI 预判和测试都走**同一份判定**，不各抄一遍。 */
  KG.keepsActionAfterMove = function (u) {
    if (!u) return false;
    return hasKw(u, 'guerrilla')
      || !!(u.mods && u.mods.canMoveAndAttack)
      || (RULES.tankMoveAndAttack && isType(u, 'tank') && u.attackedThisTurn === 0);
  };

  function guardUnitsOf(state, idx) {
    return allUnitsOf(state, idx).filter(function (u) { return hasKw(u, 'guard'); });
  }
  KG.guardUnitsOf = guardUnitsOf;

  // 「对战X时不会受到反击伤害」这类规则属于**攻击方自己**：
  // 它主动攻击 X 类型目标时不吃反击（不能写成"被攻击时免反击"）
  function attackerNoRetalVsType(a, t) {
    const v = a.mods && a.mods.noRetalVsType;
    const list = v ? (Array.isArray(v) ? v : [v]) : [];
    // 太空单位（巡地舰/巡航舰/太空战机）主动打陆军不吃反击
    if (RULES.spaceNoRetalVsLand && isType(a, 'space')) {
      RULES.landTypes.forEach(function (lt) { list.push(lt); });
    }
    if (!list.length) return false;
    return list.some(function (tp) { return isType(t, tp); });
  }
  KG.attackerNoRetalVsType = attackerNoRetalVsType;

  KG.attack = async function (state, pi, attackerUid, targetRef, chooser) {
    const chk = KG.canAttack(state, pi, attackerUid, targetRef);
    if (!chk.ok) { log(state, '无法攻击：' + chk.why, 'error'); return false; }
    const p = state.players[pi];
    const a = KG.unitByUid(state, attackerUid);
    if (chk.opCost) { p.kredits -= chk.opCost; log(state, '支付行动花费 ' + chk.opCost, 'cost'); }
    a.actionsLeft--;
    a.attackedThisTurn++;
    if (a.actionsLeft <= 0) a.canAct = false;
    if (hasKw(a, 'smokescreen')) { a.kws.smokescreen = false; log(state, a.name + ' 解除烟幕', 'keyword'); }
    if (a.kws.shield && a.shieldReady) { a.shieldReady = false; a.kws.shield = false; log(state, a.name + ' 攻击后失去强磁护盾', 'keyword'); }
    if (a.kws.guerrilla) { /* 游击攻击后仍保留行动规则 */ }

    if (targetRef.kind === 'hq') {
      const foe = state.players[targetRef.player];
      await runTrigger(state, { trigger: 'attack', owner: pi, source: a, target: { kind: 'hq', player: targetRef.player }, chooser: chooser });
      await runTrigger(state, { trigger: 'unitAttacked', owner: pi, source: a, target: { kind: 'hq', player: targetRef.player }, chooser: chooser });
      revealCovert(state, a);
      if (a.dead || state.over) return true;
      const dmg = attackPowerAgainst(state, a, 'hq');
      log(state, a.name + ' 攻击 ' + foe.name + '的总部（' + dmg + ' 点）', 'attack');
      await damageHQ(state, foe, dmg, a.name, a, { combat: true });
      // ★ 牌Q 卡包：「攻击敌方总部时/后，…」（"等量伤害"也读这里记下的数值）
      state.lastCombatDamage = dmg;
      runTrigger(state, { trigger: 'afterAttackHQ', owner: a.owner, source: a, unit: a, amount: dmg });
      afterAttack(state, a);
      checkWin(state);
      return true;
    }

    const t = KG.unitByUid(state, targetRef.uid);
    await runTrigger(state, { trigger: 'attack', owner: pi, source: a, target: { kind: 'unit', uid: t.uid }, defender: t, chooser: chooser });
    await runTrigger(state, { trigger: 'unitAttacked', owner: pi, source: a, target: { kind: 'unit', uid: t.uid }, defender: t, chooser: chooser });
    revealCovert(state, a); revealCovert(state, t);
    if (a.dead || t.dead || state.over) return true;
    await runTrigger(state, { trigger: 'attacked', owner: t.owner, source: t, defender: t, victim: t, attacker: a, chooser: chooser });
    await runTrigger(state, { trigger: 'friendlyAttacked', owner: t.owner, source: t, victim: t, attacker: a, chooser: chooser });
    if (a.dead || t.dead || state.over) return true;
    const dmg = attackPowerAgainst(state, a, t);
    const back = attackPowerAgainst(state, t, a);
    log(state, a.name + ' 攻击 ' + t.name + '，造成 ' + dmg + ' 点', 'attack');

    const defensive = collectDefensiveEffects(state, a, t);
    // 冲击：首次攻击不受到反击伤害（攻击后失去该词条）
    const impactIgnoresRetal = hasKw(a, 'impact') && a.impactUsed !== true;
    if (impactIgnoresRetal) log(state, a.name + ' 的冲击：本次攻击不会受到反击伤害', 'keyword');
    // 攻方不吃反击：炮兵；轰炸机打非战斗机
    const attackerIsBomber = isType(a, 'bomber');
    const targetIsFighter = isType(t, 'fighter');
    const bomberSafe = RULES.bomberNoRetalVsNonFighter && attackerIsBomber && !targetIsFighter;
    if (bomberSafe) log(state, a.name + ' 是轰炸机，攻击非战斗机目标不受反击', 'keyword');
    const attackerNoRetal = RULES.noRetaliationTypes.indexOf(a.unitType) >= 0
      || (a.mods && a.mods.noRetal)
      || attackerNoRetalVsType(a, t)
      || impactIgnoresRetal
      || bomberSafe
      || defensive.some(function (x) { return x.noRetalFrom && isType(a, x.noRetalFrom); });
    // 守方不反击：炮兵；轰炸机面对非战斗机攻击者；伏击已先手结算过（这条在伏击结算后再判）
    let ambushStruck = false;
    const defenderIsBomber = isType(t, 'bomber');
    const attackerIsFighter = isType(a, 'fighter');
    const defenderNoRetalStatic = (RULES.artilleryDefenderNoRetal && isType(t, 'artillery'))
      || (RULES.bomberDefenderNoRetal && defenderIsBomber && !attackerIsFighter);
    if (defenderNoRetalStatic) log(state, t.name + ' 不会反击（' + (isType(t, 'artillery') ? '炮兵' : '轰炸机对非战斗机') + '）', 'keyword');

    // 硬铝弹 / 无视对战词条：攻击时无视目标的防御词条
    // ★ 隐蔽（制作者词条）：被攻击即打破 —— 无视 tsekep 等词条忽略效果（"被攻击"是事实，不是词条判定）。
    //   打破后对手可见卡面；「无视指令 / 卡背朝上」都随之失效。
    if (t.kws && t.kws.conceal) {
      delete t.kws.conceal;
      log(state, t.name + ' 被攻击，隐蔽失效（卡面翻开）', 'keyword');
    }
    const ignoreDefKws = hasKw(a, 'tsekep') || (a.mods && a.mods.ignoreCombatKw) || defensive.some(function (x) { return x.ignoreAll; });
    const defKws = ignoreDefKws ? {} : Object.assign({}, t.kws);
    // 「对战空军具有伏击」：只有目标是对应兵种时才吃伏击
    if (defKws.ambush && t.mods && t.mods.ambushOnlyVs) {
      const av = t.mods.ambushOnlyVs;
      const hit = (Array.isArray(av) ? av : [av]).some(function (tp) { return isType(a, tp); });
      if (!hit) defKws.ambush = false;
    }

    // ★ 伏击的触发条件之一是「对战单位**能受到反击伤害**」（制作者口径）：
    //   攻方本次不吃反击时（炮兵族 / 轰炸机打非战斗机 / 冲击首次攻击 / noRetalVsType …）
    //   伏击**不触发** —— 否则会出现"轰炸机攻击伏击单位，反被先手打掉攻击"的错位结算。
    //   attackerNoRetal 已在上面算好（含 bomberSafe / impactIgnoresRetal 等全部分支），直接复用。
    if (defKws.ambush && t.ambushReady && !ignoreDefKws && !attackerNoRetal && !defenderNoRetalStatic) {
      t.ambushReady = false;
      ambushStruck = true;                       // 伏击已经先手打过 → 本次不再重复反击
      log(state, t.name + ' 触发伏击，先造成 ' + t.attack + ' 点伤害', 'keyword');
      await damageUnit(state, a, back, t, { attacker: t, combat: true });
      if (a.dead) { afterAttack(state, a, t); checkWin(state); return true; }
    }
    if (defKws.magnetic && t.magneticCharges > 0 && !ignoreDefKws) {
      t.magneticCharges--;
      log(state, t.name + ' 的磁反应装甲抵挡了这次攻击（剩余 ' + t.magneticCharges + ' 层）', 'keyword');
      if (t.magneticCharges <= 0) delete t.kws.magnetic;
      afterAttack(state, a, t);
      checkWin(state);
      return true;
    }
    state.__combatDamageBatch = [];
    await damageUnit(state, t, dmg, a, { attacker: a, pierce: !!(a.mods && a.mods.pierce), ignoreCombatKw: ignoreDefKws, combat: true });
    // 互伤是【同时结算】：守方即使会被这一击打死，也照样把它的攻击力打回来
    // （例外：伏击先手把攻方打死 → 攻方不造成伤害，已在上面 return；炮兵/轰炸机等规则见 defenderNoRetal）
    const defenderNoRetal = ambushStruck || defenderNoRetalStatic;
    if (ambushStruck) log(state, t.name + ' 已由伏击先手结算，本次不再反击', 'keyword');
    if (!attackerNoRetal && !defenderNoRetal && back > 0) {
      await damageUnit(state, a, back, t, { attacker: t, combat: true });
    } else if (attackerNoRetal) {
      log(state, a.name + ' 不受反击伤害', 'keyword');
    } else if (defenderNoRetal) {
      log(state, t.name + ' 不造成反击伤害', 'keyword');
    } else if (back <= 0) {
      log(state, t.name + ' 攻击力为 0，没有反击伤害', 'keyword');
    }
    await flushCombatDamage(state);
    emitUnitActed(state, a);
    if (!a.dead) await runTrigger(state, { trigger: 'friendlySurvived', owner: pi, source: a, survivor: a, chooser: chooser });
    if (!t.dead) await runTrigger(state, { trigger: 'friendlySurvived', owner: t.owner, source: t, survivor: t, chooser: chooser });
    afterAttack(state, a, t);
    checkWin(state);
    return true;
  };

  // 防御方相关的静态效果（例如“不会受到来自陆军的反击伤害”）
  function collectDefensiveEffects(state, a, t) {
    const out = [];
    (t.def.effects || []).forEach(function (e) {
      if (e.defensive) out.push(e.defensive);
      if (e.defensiveRules) out.push(e.defensiveRules);
    });
    (t.grantedEffects || []).forEach(function (e) { if (e.defensive) out.push(e.defensive); });
    return out;
  }
  KG.collectDefensiveEffects = collectDefensiveEffects;

  function emitUnitActed(state, u) {
    if (!u) return;
    runTrigger(state, { trigger: 'unitActed', owner: u.owner, source: u, actor: u });
  }
  KG.emitUnitActed = emitUnitActed;

  /* ═════════════════════════════ 复合型充能（武装）════════════════════════════
   * 卡面：「充能完毕后，具有重甲3，本单位攻击时，具有+3攻击力」（制作者 2026-09-24 定口径）：
   *   ① 充能到点 → **立即武装**：挂 grants 词条（重甲3 → kwValues.armor += 3），
   *      并记住攻击加成（onAttack.attackBonus → u.chargeAtkBonus，attackPowerAgainst 里生效）；
   *   ② 武装期间充能**暂停**（充能倒数处跳过，见 beginTurn 的 _hasCharge 块）；
   *   ③ 本单位**攻击后**（afterAttack）：全部增益失去（按快照还原）→ 充能重新计时。
   *     —— "充能效果必须全部触发（=这次攻击消费掉整个武装）才会进入下一次充能"。
   * 编译产物：{ trigger:'chargeArm', grants:[{kw,v}], onAttack:{attackBonus} }
   *   （compiler.js parseChargeArmLine）
   * ⚠ 不走 runTrigger/effects.js 的触发分发 —— 到点武装、攻击消费都在引擎侧直接完成。
   * ⚠ chargeReady 保持 true：武装即"本轮充能已就绪并被占用"，chargeNow/chargeGate 的
   *   检查兼容不动；攻击消费后统一由 dischargeChargeArm 关掉并重置计时。 */
  const CHARGE_NUMERIC_KW = { armor: 1, lightArmor: 1, magnetic: 1, shield: 1, sponge: 1 };
  function chargeArmIfAny(state, u) {
    const fx = (u.silenced ? [] : ((u.def && u.def.effects) || [])).concat(u.extraEffects || []).filter(function (e) { return e && e.trigger === 'chargeArm'; });
    if (!fx.length) return;
    const undo = [];
    const cnNames = [];
    fx.forEach(function (e) {
      (e.grants || []).forEach(function (g) {
        if (!g || !g.kw) return;
        if (CHARGE_NUMERIC_KW[g.kw]) {
          // 数值型词条（重甲/轻甲/磁反应装甲/强磁护盾/海绵装甲）→ kwValues 累加
          if (!u.kwValues) u.kwValues = {};
          u.kwValues[g.kw] = (u.kwValues[g.kw] || 0) + (g.v || 1);
          undo.push({ type: 'kwv', key: g.kw, v: g.v || 1 });
        } else {
          // 布尔型词条（闪击/游击…）→ 挂 kws 标记
          if (!u.kws) u.kws = {};
          if (!u.kws[g.kw]) { u.kws[g.kw] = true; undo.push({ type: 'kws', key: g.kw }); }
        }
        cnNames.push(((KEYWORDS[g.kw] && KEYWORDS[g.kw].cn) || g.kw) + (g.v || 1));
      });
      if (e.onAttack && e.onAttack.attackBonus) {
        u.chargeAtkBonus = (u.chargeAtkBonus || 0) + e.onAttack.attackBonus;
        undo.push({ type: 'atk', v: e.onAttack.attackBonus });
        cnNames.push('攻击+' + e.onAttack.attackBonus);
      }
    });
    if (!undo.length) return;
    u.chargeArmedUndo = undo;            // 还原快照（攻击后按它逆序摘除）
    u.chargeArmed = true;                // 武装态：充能倒数暂停
    log(state, u.name + ' 充能武装：' + cnNames.join('，') + '（攻击后失去并重新充能）', 'keyword');
  }

  // 攻击消费武装：全部增益按快照逆序还原 → 重新计时（进入下一次充能）
  function dischargeChargeArm(state, a) {
    const undo = a.chargeArmedUndo || [];
    for (let i = undo.length - 1; i >= 0; i--) {
      const g = undo[i];
      if (g.type === 'kwv') {
        if (a.kwValues) {
          a.kwValues[g.key] = (a.kwValues[g.key] || 0) - g.v;
          if (a.kwValues[g.key] <= 0) delete a.kwValues[g.key];
        }
      } else if (g.type === 'kws') {
        if (a.kws) delete a.kws[g.key];
      } else if (g.type === 'atk') {
        a.chargeAtkBonus = (a.chargeAtkBonus || 0) - g.v;
        if (a.chargeAtkBonus <= 0) delete a.chargeAtkBonus;
      }
    }
    a.chargeArmedUndo = null;
    a.chargeArmed = false;
    a.chargeReady = false;
    a.chargeConsumed = [];
    const cv = (a.kwValues && parseInt(a.kwValues.charge, 10)) || 0;
    if (cv > 0) a.chargeIn = cv;         // 重新计时（"全部触发完毕 → 下一次充能"）
    log(state, a.name + ' 攻击后失去充能武装' + (cv > 0 ? '，重新充能 ' + cv + ' 回合' : ''), 'keyword');
  }
  KG.chargeArmIfAny = chargeArmIfAny;
  KG.dischargeChargeArm = dischargeChargeArm;

  function afterAttack(state, a, defender) {
    if (!a || a.dead) return;
    // ★ 复合充能武装的"攻击后失去"：重甲/攻击加成随本次攻击全部消费，充能重新计时
    //   （放在最前面：攻击本身就是"全部触发"的标志，后面任何攻击后效果都拦不住它）
    if (a.chargeArmed) dischargeChargeArm(state, a);
    // ★ 「狂怒 / 奋战」不含任何"攻击后 +1 攻击力"的效果 —— 它们与 `grantMod` / `buff` 无关。
    //   这两个词条**只**改变每回合可行动次数（见 beginTurn: actionsLeft = 2）。
    //   曾经这里有一行 `if (hasKw(a,'fury')) a.attack += 1;`，是凭空多出来的行为，已删除。
    //   若某张卡卡面确实写「攻击后获得+1攻击力」，应由 primitives 解析成
    //   `{ trigger:'afterAttack' }` + `buff`/`grantMod` 效果，而不是靠词条兜底。
    // 冲击用掉即失去
    if (a.kws && a.kws.shield) { a.shieldReady = false; delete a.kws.shield; log(state, a.name + ' 攻击后失去强磁护盾', 'keyword'); }
    if (hasKw(a, 'impact') && a.impactUsed !== true && !(a.mods && a.mods.keepImpact)) {
      a.impactUsed = true;
      delete a.kws.impact;
      // ★ 牌Q 卡包：「本单位冲击后，…」——冲击（首次攻击免反击）被消耗时广播
      runTrigger(state, { trigger: 'impactUsed', owner: a.owner, source: a, unit: a });
      log(state, a.name + ' 的冲击已用掉（之后攻击会正常受到反击）', 'keyword');
    }
    const fx = (a.silenced ? [] : (a.def.effects || [])).concat(a.extraEffects || []).filter(function (e) { return e.trigger === 'afterAttack'; });
    if (fx.length) {
      // ★ 必须把**被攻击者**带进 ctx.event：
      //   「攻击X单位时将其Y」（如蜂鸟战斗机「攻击太空单位时将其压制」）要靠
      //   ctx.event.defender 做两件事 —— 条件 defenderIsType 判定、以及
      //   {sel:'ref',ref:'defender'} 取到被动目标。原实现不传 event，
      //   这两件事全部落空（条件恒假 / ref 取到 null），效果静默失效。
      const ev = defender ? { trigger: 'afterAttack', source: a, defender: defender, attacker: a } : null;
      execEffects(state, { owner: a.owner, source: a, unit: a, card: a.def, event: ev }, fx, null, 'afterAttack');
    }
  }
  KG.afterAttack = afterAttack;

  /* ------------------------------------------------------------------ 移动 */
  KG.canMove = function (state, pi, uid, toZone, opts) {
    opts = opts || {};
    if (state.over || state.phase !== 'play') return { ok: false, why: '对局未开始或已结束' };
    if (toZone !== 'frontline' && toZone !== 'support') return { ok: false, why: '无效阵线' };
    const u = KG.unitByUid(state, uid);
    if (!u || u.owner !== pi) return { ok: false, why: '不是你的单位' };
    if (state.active !== pi) return { ok: false, why: '不是你的回合' };
    if (!u.canAct || u.actionsLeft <= 0) return { ok: false, why: '本单位本回合已行动' };
    /* ★★ 已经攻击过的单位**不能再移动**（制作者 2026-09-21 口径）。
     *   奋战/狂怒（fury/valor）给的是**打两下**，不是"打完还能走"：
     *     · 奋战步兵  → 要么移动、要么打两下；**攻击后不能再移动**
     *     · 奋战坦克  → 可以在同一回合内移动并攻击（兵种全局规则）
     *     · 有 canMoveAndAttack 原语的奋战单位 → 可以移动 + 打两下（卡面明确声明）
     *   ⚠ 以前没这条：奋战步兵攻击一次后 actionsLeft 还剩 1 → 移动判定直接通过，
     *     等于**白嫖一次移动**（用户报的 bug）。
     *   坦克走 keepsActionAfterMove 里的 tank 分支（那里要求 attackedThisTurn === 0），
     *   所以坦克"移动→攻击"可以，"攻击→移动"不行 —— 符合 KARDS 的语义。 */
    if (u.attackedThisTurn > 0 && !KG.keepsActionAfterMove(u)) {
      return { ok: false, why: '本单位本回合已攻击，不能再移动' };
    }
    const p = state.players[pi];
    const opCost = effOpCost(state, pi, u);
    if (RULES.opCostOnMove && opCost > p.kredits) return { ok: false, why: '行动花费不足（需要 ' + opCost + '）' };
    if (toZone === 'frontline') {
      if (u.zone === 'frontline') return { ok: false, why: '已在前线' };
      // 前线同一时间只能由一方掌控：对手还占着就得先把他打掉
      if (frontlineOf(state, 1 - pi).length > 0) {
        return { ok: false, why: '对手还占着前线，先消灭他的前线单位才能推进' };
      }
      if (frontlineOf(state, pi).length >= KG.effectiveFrontlineMax(state, pi)) {
        return { ok: false, why: '你的前线单位已达上限（' + KG.effectiveFrontlineMax(state, pi) + ' 个）' };
      }
    } else {
      if (u.zone === 'support') return { ok: false, why: '已在支援阵线' };
      if (p.support.length >= RULES.supportMax) return { ok: false, why: '支援阵线已满（6 个单位）' };
      // 制作者规则：**非游击单位不能自己退回支援阵线**；只有「撤退」类效果能把它送回去
      // （撤退走 OPS.returnToHand，不经过这里；效果用 opts.byEffect 也可以强制移动）
      if (!opts.byEffect && !hasKw(u, 'guerrilla')) {
        return { ok: false, why: '只有「游击」单位能自己退回支援阵线（其它单位只能被撤退）' };
      }
    }
    return { ok: true, opCost: RULES.opCostOnMove ? opCost : 0 };
  };

  // 同一条阵线内换位置：不花行动花费、不消耗行动（纯摆放）
  KG.reposition = function (state, pi, uid, slotIndex, opts) {
    if (state.over || state.phase !== 'play' || !opts || !opts.byEffect) return false;
    const u = KG.unitByUid(state, uid);
    if (!u || u.owner !== pi || state.active !== pi) return false;
    const zone = u.zone;
    const line = KG.lineOf(state, pi, zone);
    const from = slotOf(state, u);
    let to = slotIndex == null ? line.length - 1 : Math.max(0, Math.min(slotIndex, line.length - 1));
    if (from === to || from < 0) return false;
    // keepHq：换位是「搬走马上插回来」，总部不因中间态滑动；insert 侧按插队语义单边维护
    removeUnitFromBoard(state, u, { keepHq: true });
    insertUnitAt(state, pi, u, zone, to);
    log(state, u.name + ' 换到' + (zone === 'frontline' ? '前线' : '支援阵线') + '第 ' + (to + 1) + ' 格', 'move');
    recomputeAuras(state);
    return true;
  };

  KG.move = async function (state, pi, uid, toZone, chooser, slotIndex, opts) {
    const chk = KG.canMove(state, pi, uid, toZone, opts);
    if (!chk.ok) { log(state, '无法移动：' + chk.why, 'error'); return false; }
    const p = state.players[pi];
    const u = KG.unitByUid(state, uid);
    if (chk.opCost) { p.kredits -= chk.opCost; log(state, '支付行动花费 ' + chk.opCost, 'cost'); }
    const from = u.zone;
    removeUnitFromBoard(state, u);
    u.zone = toZone;
    insertUnitAt(state, pi, u, toZone, slotIndex);
    log(state, u.name + ' 移动到' + (toZone === 'frontline' ? '前线' : '支援阵线'), 'move');
    if (toZone === 'frontline') {
      // 旧卡数据用 mobilize 表示移至前线；动员词条自身的成长在回合开始结算。
      const fx = (u.silenced ? [] : (u.def.effects || [])).concat(u.extraEffects || []).filter(function (e) { return e.trigger === 'mobilize' || e.trigger === 'movedToFrontline'; });
      if (fx.length) await execEffects(state, { owner: pi, source: u, unit: u, card: u.def, _preFiltered: true }, fx, chooser, 'movedToFrontline');
      // 「友方单位移至前线时」这类监听用 unitMobilized
      await runTrigger(state, { trigger: 'unitMobilized', owner: pi, source: u, chooser: chooser, unitType: u.unitType, name: u.name });
    }
    emitUnitActed(state, u);
    // 烟幕：移动也会解除（和攻击一样）
    if (hasKw(u, 'smokescreen')) {
      u.kws.smokescreen = false;
      log(state, u.name + ' 移动后解除烟幕', 'keyword');
    }
    // 移动消耗行动。保留的三条例外（原语优先）：
    //   ① 游击（词条）
    //   ② 卡牌自身的 canMoveAndAttack 原语 —— 卡面写「本单位可以在同一回合内移动并攻击」时，
    //      由该卡自己的效果声明，**不再靠 extraTypes:['tank'] 假冒坦克** 来骗过下面这条全局规则。
    //      注意：不再要求 attackedThisTurn === 0 —— 那条限制属于"坦克"这个全局规则的语义，
    //      卡面只说了"可以移动并攻击"，没有说"仅限本回合还没攻击过"。
    //   ③ 坦克全局规则（RULES.tankMoveAndAttack）：所有坦克都能移动后继续攻击
    //   ④ 奋战/狂怒（制作者 2026-09-19 口径修正）：**只有坦克 / 游击**的奋战单位能「移上前线后仍打两下」；
    //      其它奋战单位是「要么移动、要么打两下」（移动照常吃掉一次行动）。
    //      坦克走下面 ③、游击走 ①，所以这里**不能**再列 fury —— 列了等于让步兵奋战卡白嫖一次移动。
    const keepAction = KG.keepsActionAfterMove(u);
    if (!keepAction) { u.actionsLeft = 0; u.canAct = false; }
    recomputeAuras(state);
    checkWin(state);
    return true;
  };

  /* -------------------------------------------------------------- 触发与效果 */
  /* -------------------------------------------------------------- 反制卡结算 */

  /* 挂起模型辅助 —— 反制卡"留在手上"的实现细节
   *   挂起时：inst.suspended = true，手牌不删，p.counters 里放一个引用条目
   *   触发时：才把手牌里的那张真正删掉（removeSuspendedFromHand）
   *   取消时：清掉 suspended 标记 + counters 条目，卡回到普通手牌状态
   */
  function findHandIndexByInst(p, inst) {
    if (!p || !p.hand) return -1;
    for (let i = 0; i < p.hand.length; i++) if (p.hand[i] === inst) return i;
    return -1;
  }
  KG.findHandIndexByInst = findHandIndexByInst;

  // 维护 counters 条目里的 handIndexAtSet（手牌增删后仅作展示参考，容错）
  function syncSuspendedIndexes(p) {
    if (!p || !p.counters) return;
    p.counters.forEach(function (c) {
      if (!c.inst) return;
      const at = findHandIndexByInst(p, c.inst);
      c.handIndexAtSet = at;              // -1 表示已不在手牌（异常状态，UI 需容错）
    });
  }
  KG.syncSuspendedIndexes = syncSuspendedIndexes;

  // 反制触发时：把手牌里的那张真正移除（挂起态结束）
  function removeSuspendedFromHand(state, owner, cardEntry) {
    const p = state.players[owner];
    if (!p || !cardEntry) return;
    const inst = cardEntry.inst;
    if (inst) {
      const at = findHandIndexByInst(p, inst);
      if (at >= 0) {
        p.hand.splice(at, 1);
        log(state, p.name + ' 的挂起反制离手：' + cardDef(state, cardEntry.cardId).name, 'keyword');
      }
      inst.suspended = false;
    }
    syncSuspendedIndexes(p);
  }
  KG.removeSuspendedFromHand = removeSuspendedFromHand;

  // 取消挂起（玩家主动）：把这张牌退回普通手牌状态，**返还埋设时支付的指挥点**（制作者规则）
  KG.unsuspendCounter = function (state, pi, cardIdOrInst) {
    const p = state.players[pi];
    if (!p) return false;
    const list = p.counters || [];
    const i = list.findIndex(function (c) {
      return c.inst === cardIdOrInst || c.cardId === cardIdOrInst;
    });
    if (i < 0) return false;
    const c = list[i];
    list.splice(i, 1);
    if (c.inst) c.inst.suspended = false;
    // ★ 返还埋设时支付的花费（记录在 entry.paidCost；老数据没有就按当前手牌实例重算）
    let refund = c.paidCost;
    if (refund == null) {
      try { refund = c.inst ? KG.instCost(state, pi, c.inst, cardDef(state, c.cardId)) : 0; }
      catch (e) { refund = 0; }
    }
    if (refund > 0) {
      p.kredits += refund;
      log(state, p.name + ' 取消了挂起的反制：' + cardDef(state, c.cardId).name + '（返还 ' + refund + ' 指挥点）', 'cost');
    } else {
      log(state, p.name + ' 取消了挂起的反制：' + cardDef(state, c.cardId).name, 'keyword');
    }
    syncSuspendedIndexes(p);
    return true;
  };

  // 某张手牌是否正处于"挂起反制"状态
  KG.isSuspendedCard = function (state, pi, handIdx) {
    const p = state.players[pi];
    if (!p) return false;
    const inst = p.hand && p.hand[handIdx];
    if (!inst) return false;
    return !!inst.suspended;
  };

  // 本回合是否还能操作挂起的反制（原版：只有**自己回合**可以取消挂起）
  KG.canUnsuspendCounter = function (state, pi) {
    return !!state && !state.over && state.active === pi;
  };

  const COUNTER_ON_ALIAS = { deploy: 'unitDeployed', death: 'friendlyDeath' };
  function normCounterOn(x) { return COUNTER_ON_ALIAS[x] || x; }

  // 某条反制效果是否响应当前事件：
  //   on    事件名（字符串或数组）；不写 = 默认"对手的任何行动"
  //   owner 'foe'（默认，对手的事件）| 'self'（自己的事件）| 'any'（任何一方）
  function counterOnMatch(e, ev, oi) {
    if (e.interrupt) return false;                     // interrupt 类走同步打断，不在这里派发
    const name = normCounterOn(ev.trigger);
    if (!e.on) return !isBehaviorTrigger(ev.trigger) && ev.owner === 1 - oi;
    const list = (Array.isArray(e.on) ? e.on : [e.on]).map(normCounterOn);
    if (list.indexOf(name) < 0) return false;
    const who = e.owner || 'foe';
    if (who === 'any') return true;
    if (who === 'self') return ev.owner === oi;
    return ev.owner === 1 - oi;
  }

  // 「打断型」反制：必须在事件真正发生**之前**生效（亡计触发前抑制、致命伤害前偏转），
  // 所以用同步函数直接查表消费，而不是丢进异步的 FX.exec。
  function consumeInterrupt(state, owner, kind, info) {
    const p = state.players[owner];
    if (!p || !p.counters || !p.counters.length) return null;
    const i = p.counters.findIndex(function (c) {
      return (c.effects || []).some(function (e) { return e.trigger === 'counter' && e.interrupt === kind; });
    });
    if (i < 0) return null;
    const c = p.counters.splice(i, 1)[0];
    // 挂起模型：触发时才把卡从手牌真正移出，然后进弃牌堆
    removeSuspendedFromHand(state, owner, c);
    p.discard.push(c.cardId);
    const def = cardDef(state, c.cardId);
    log(state, p.name + ' 的反制卡触发：' + def.name +
      (info && info.victim ? '（目标：' + info.victim.name + '）' : ''), 'keyword');
    return c;
  }
  KG.consumeInterrupt = consumeInterrupt;

  // A self cost modification belongs to the hand instance. Other abilities
  // retain their field scope unless the author explicitly selects a zone.
  function effectListenZone(effect) {
    if (effect.listenZone && effect.listenZone !== 'auto') return effect.listenZone;
    const handCost = x => x && typeof x === 'object' &&
      (x.op === 'changeAttribute' && x.property === 'cost' && x.target?.sel === 'self' ||
       Object.values(x).some(v => Array.isArray(v) ? v.some(handCost) : v && typeof v === 'object' && handCost(v)));
    return handCost(effect.actions) ? 'hand' : 'field';
  }
  KG.effectListenZone = effectListenZone;
  function handEffects(state, inst) {
    return (cardDef(state, inst.id)?.effects || []).concat(inst.mods?.effects || [], inst.extraEffects || []);
  }
  const activeBehaviorEffects = new WeakMap();
  const isBehaviorTrigger = trigger => typeof trigger === 'string' && trigger.startsWith('behavior:');
  async function execEventEffects(state, ctx, fx, chooser, trigger) {
    if (!isBehaviorTrigger(trigger)) return execEffects(state, ctx, fx, chooser, trigger);
    let listeners = activeBehaviorEffects.get(state);
    if (!listeners) activeBehaviorEffects.set(state, listeners = new Map());
    const listener = ctx.handInst || ctx.unit || ctx.enchant || ctx.counter;
    let active = listeners.get(listener);
    if (!active) listeners.set(listener, active = new Set());
    const catalog = ctx.handInst ? handEffects(state, ctx.handInst) : ctx.unit ? (ctx.unit.def.effects || []).concat(ctx.unit.extraEffects || []) :
      (ctx.enchant || ctx.counter)?.effects || fx;
    for (const effect of fx) {
      if (ctx.handInst && !state.players[ctx.owner].hand.includes(ctx.handInst)) break;
      if (active.has(effect)) continue;
      active.add(effect);
      const alias = ctx.event?.aliasEvents?.[effect.trigger];
      try { await execEffects(state, { ...ctx, ...(alias ? { event: alias } : {}), _effectIndex: catalog.indexOf(effect) }, [effect], chooser, trigger); }
      finally { active.delete(effect); }
      if (state.over) return;
    }
    if (!active.size) listeners.delete(listener);
  }
  async function runTrigger(state, ev) {
    state.eventCounts = state.eventCounts || {};
    if (state.eventCountTurn !== state.turn) { state.eventCountTurn = state.turn; state.turnEventCounts = {}; }
    state.turnEventCounts = state.turnEventCounts || {};
    const selfEvent = ['attack', 'attacked', 'damaged', 'afterAttack', 'death', 'revealed'].indexOf(ev.trigger) >= 0;
    const eventKey = (ev.owner == null ? 'any' : ev.owner) + '|' + ev.trigger + (selfEvent && ev.source ? '|' + ev.source.uid : '');
    ev.ordinal = state.eventCounts[eventKey] = (state.eventCounts[eventKey] || 0) + 1;
    ev.turnOrdinal = state.turnEventCounts[eventKey] = (state.turnEventCounts[eventKey] || 0) + 1;
    const idxs = ev.owner != null ? [ev.owner] : [0, 1];
    const also = ev.matchAlso || [];
    if (also.length) {
      ev.aliasEvents = {};
      for (const trigger of also) {
        const key = (ev.owner == null ? 'any' : ev.owner) + '|' + trigger;
        const ordinal = state.eventCounts[key] = (state.eventCounts[key] || 0) + 1;
        const turnOrdinal = state.turnEventCounts[key] = (state.turnEventCounts[key] || 0) + 1;
        const timing = KG.parseBehaviorTrigger(trigger);
        ev.aliasEvents[trigger] = { ...ev, aliasEvents: undefined, trigger, ordinal, turnOrdinal,
          ...(timing ? { behavior: timing.action, behaviorKind: timing.kind } : {}) };
      }
    }
    // ★ 牌Q（2026-09-25）：「部署、移动、攻击时」这类**一个效果块挂多个事件** → e.triggers 数组
    const match = function (e, listener) {
      if (isBehaviorTrigger(ev.trigger) && activeBehaviorEffects.get(state)?.get(listener)?.has(e)) return false;
      if (e.trigger === 'attacked' && e.watchOpponent) return ev.trigger === 'unitAttacked';
      return e.trigger === ev.trigger || also.indexOf(e.trigger) >= 0 || (e.triggers && e.triggers.indexOf(ev.trigger) >= 0);
    };
    const selfOnly = function (u, e) {
      if (e.selfOnly) return true;
      if (['damaged', 'attacked', 'chargeNow', 'afterAttackHQ', 'impactUsed', 'revealed'].indexOf(e.trigger) >= 0 && !e.watchOpponent) return true;
      // 兼容旧效果文件：友方攻击监听曾与本单位攻击共用 attack。
      if (e.trigger === 'attack' && !e.watchOpponent) return !/友方(?:一个|任意|某个|每个)?单位攻击时/.test((u.def || {}).text || '');
      return false;
    };
    const fitsEvent = function (e, oi) {
      if (e.eventSide === 'friendly' && ev.owner !== oi) return false;
      if (e.eventSide === 'enemy' && ev.owner !== 1 - oi) return false;
      const actor = ev.victim || ev.deadUnit || ev.unit || ev.source;
      const actorType = (actor && actor.unitType) || ev.unitType;
      if (e.unitType && !typeMatch(e.unitType, actorType)) return false;
      if (e.filter && (!actor || !matchFilter(state, actor, e.filter, oi))) return false;
      const event = ev.aliasEvents?.[e.trigger] || ev;
      if (e.firstEvent && (e.firstEvent === 'turn' ? event.turnOrdinal : event.ordinal) !== 1) return false;
      return true;
    };
    // 只有"事件主角自己的效果已在别处执行过"的事件才跳过它：
    //  - deploy/unitDeployed：playCard 里已直接执行过它的部署效果
    //  - mobilize：move() 里已直接执行过它的动员效果（否则会触发两次，例如 UNTED-236 每次上前线 +2 攻击）
    const skipSource = ev.trigger === 'deploy' || ev.trigger === 'unitDeployed' || ev.trigger === 'mobilize';
    for (const oi of idxs) {
      // 场上单位
      const units = allUnitsOf(state, oi).slice();
      for (const u of units) {
        if (u.dead) continue;
        if (skipSource && ev.source && u.uid === ev.source.uid) continue;
        const fx = (u.silenced ? [] : (u.def.effects || [])).concat(u.extraEffects || []).filter(function (e) {
          if (isBehaviorTrigger(ev.trigger) && effectListenZone(e) === 'hand') return false;
          if (!match(e, u)) return false;
          if (!fitsEvent(e, oi)) return false;
          if (e.watchOpponent && ev.owner != null && ev.owner === oi) return false;
          if (selfOnly(u, e) && (!ev.source || u.uid !== ev.source.uid)) return false;
          return true;
        });
        if (fx.length) await execEventEffects(state, { owner: oi, source: u, unit: u, card: u.def, event: ev, _preFiltered: true }, fx, ev.chooser, ev.trigger);
        if (state.over) return;
      }
      if (state.over) return;
    }

    /* ★ 2026-09-26（制作者要求）：「额外抽牌」「额外获得指挥点」这类**玩家级事件**
     *   要能被**对手**监听。事件点派发时 ev.owner 是事件方（抽牌方 / 得点方），
     *   而上面的主循环只走 ev.owner 名下的单位 —— 我方单位上写的「敌方额外抽牌时」
     *   永远收不到。这里补一遍**对手侧**，只认显式 watchOpponent === true 的效果
     *   （编译器遇到「敌方…时」前缀会自动置位，见 compiler.js 的 /^敌方/ 判定），
     *   判据与下面「总部附魔」段保持一致。 */
    if (ev.owner != null) {
      const foe = ev.owner === 0 ? 1 : 0;
      const foeUnits = allUnitsOf(state, foe).slice();
      for (const fu of foeUnits) {
        if (fu.dead) continue;
        const ffx = (fu.silenced ? [] : (fu.def.effects || [])).concat(fu.extraEffects || []).filter(function (e) {
          return (e.watchOpponent === true || e.anyOwner || e.eventSide === 'enemy' || e.eventSide === 'any') &&
            (!isBehaviorTrigger(ev.trigger) || effectListenZone(e) !== 'hand') &&
            match(e, fu) && fitsEvent(e, foe) && (!selfOnly(fu, e) || ev.source?.uid === fu.uid);
        });
        if (ffx.length) await execEventEffects(state, { owner: foe, source: fu, unit: fu, card: fu.def, event: ev, _preFiltered: true }, ffx, ev.chooser, ev.trigger);
        if (state.over) return;
      }
    }
    // Behavior abilities can belong to cards still in hand. Use the actual
    // instance as listener identity so copies have separate gates and costs.
    if (isBehaviorTrigger(ev.trigger) && state.phase === 'play') {
      for (const oi of [0, 1]) {
        const p = state.players[oi];
        for (const inst of p.hand.slice()) {
          if (inst.suspended || !p.hand.includes(inst)) continue;
          const def = cardDef(state, inst.id);
          const fx = handEffects(state, inst).filter(e => {
            if (effectListenZone(e) === 'field' || !match(e, inst) || !fitsEvent(e, oi)) return false;
            if (ev.owner !== oi && !(e.watchOpponent || e.anyOwner || e.eventSide === 'enemy' || e.eventSide === 'any')) return false;
            if (e.watchOpponent && ev.owner === oi) return false;
            return !e.selfOnly || ev.inst === inst || ev.source?.instance === inst || ev.source?.uid === inst.uid;
          });
          if (!fx.length) continue;
          const source = { ...def, ...inst, kind: 'card', owner: oi, zone: 'hand', index: p.hand.indexOf(inst), def, instance: inst };
          await execEventEffects(state, { owner: oi, source, handInst: inst, card: def, event: ev, _preFiltered: true }, fx, ev.chooser, ev.trigger);
          if (state.over) return;
        }
      }
    }
    // ★ 事件驱动的老兵升级放在**单位效果结算之后**：
    //   「友方单位部署时，将其返回手牌。三次过后，升为老兵。」的第 3 次部署，
    //   应当是"先弹回这次的部署、再换老兵形态"（升级前，场上的还是旧形态，它的效果要照常结算）。
    //   放在循环之前的话，第 3 次会因为"旧形态已被换掉"而整条效果都不触发。
    //   （USG/units/_9 的卡面注释也写明"同一次部署同时算「打出1点」与「升级进度+1」"。）
    if (!isBehaviorTrigger(ev.trigger)) upgradeCheck(state, ev);
    if (state.over) return;
    // 总部附魔：可以监听自己或对手的事件
    for (const oi of [0, 1]) {
      const p = state.players[oi];
      for (const ench of (p.hqEnchants || []).slice()) {
        const fx = (ench.effects || []).filter(function (e) {
          if (!match(e, ench)) return false;
          if (e.listenZone === 'hand') return false;
          if (e.selfOnly && !(ev.hqOwner === oi || ev.trigger === 'hqDamaged' && ev.owner === oi)) return false;
          // ★「下一个加入战场的…」类 once 附魔：**打出者自己的部署不算"下一个"** ——
          //   否则刚打出的这张卡（单位卡带此效果）在部署瞬间自己吃掉守护，"下一个"永远轮不到别人
          //   （322工程步兵团实测：打出后自己有守护、附魔数归 0，2026-09-24 制作者报"效果不触发"）。
          if (ench.source && ev.source && ench.source.uid === ev.source.uid) return false;
          const isOwnEvent = ev.owner === oi;
          const isFoeEvent = ev.owner === 1 - oi;
          if (e.forOpponent && !isFoeEvent) return false;
          if (!e.forOpponent && e.watchOpponent && !isFoeEvent) return false;
          if (!e.forOpponent && !e.anyOwner && !e.eventSide && e.watchOpponent !== true && !isOwnEvent) return false;
          if (!fitsEvent(e, oi)) return false;
          return true;
        });
        if (fx.length) await execEventEffects(state, { owner: oi, source: null, hqOwner: oi, enchant: ench, card: ench.card, event: ev, _preFiltered: true }, fx, ev.chooser, ev.trigger);
        // ★ once（一次性附魔，「下一个加入战场的友方单位获得X」）：触发一次后移除
        if (fx.length && ench.once && !state.over) {
          const ei = (p.hqEnchants || []).indexOf(ench);
          if (ei >= 0) {
            p.hqEnchants.splice(ei, 1);
            log(state, p.name + ' 的总部效果「' + ench.name + '」已触发并消失', 'keyword');
          }
        }
        if (state.over) return;
      }
      // 反制卡（on 决定响应哪些事件、owner 决定是谁的事件）
      // counterSet 是"埋设事件"本身，不参与触发，否则"没写 on"的反制会立刻自己炸掉
      for (const c of (ev.trigger === 'counterSet' ? [] : (p.counters || []).slice())) {
        const fx = (c.effects || []).filter(function (e) {
          return e.trigger === 'counter' && counterOnMatch(e, ev, oi) && fitsEvent(e, oi);
        });
        if (fx.length) {
          // ★ 2026-09-22：**条件不满足 → 本次不触发，挂起必须原样保留**。
          //   以前这里只看 `on`/`owner` 匹配就把挂起消耗掉（移出挂起 + 进弃牌堆 + 打"反制卡触发"日志），
          //   而 execEffects 内部的 condition 判断只让**动作**空转 —— 结果是：
          //   带条件的反制（如「总部受到**来自陆军的**伤害时…」）被空军打一次就**白没了**，
          //   之后再被陆军打也不会触发（卡已经进了弃牌堆）。
          //   正确语义：条件不满足 = 这次事件"不算数"，反制继续挂着等下一次。
          const condCtx = { owner: oi, source: null, counter: c, card: cardDef(state, c.cardId), event: ev, chooser: ev.chooser };
          const condOk = fx.some(function (e) {
            return !e.condition || !KG.effects || !KG.effects.evalCond
              || KG.effects.evalCond(state, condCtx, e.condition);
          });
          if (!condOk) continue;
          p.counters = p.counters.filter(function (x) { return x !== c; });
          // 挂起模型：被敌方回合的事件触发 → 此刻才离开手牌，然后进弃牌堆
          removeSuspendedFromHand(state, oi, c);
          p.discard.push(c.cardId);
          log(state, p.name + ' 的反制卡触发：' + cardDef(state, c.cardId).name, 'keyword');
          await execEventEffects(state, { owner: oi, source: null, counter: c, card: cardDef(state, c.cardId), event: ev, _preFiltered: true, fromOrder: true }, fx, ev.chooser, isBehaviorTrigger(ev.trigger) ? ev.trigger : 'counter');
        }
      }
    }
  }
  KG.runTrigger = runTrigger;
  KG.BEHAVIOR_PHASES = { before: '发生前', during: '发生时', after: '发生后' };
  KG.BEHAVIOR_ACTIONS = { playCard: '打出卡牌', attack: '单位攻击', move: '单位移动', beginTurn: '开始回合', endTurn: '结束回合', drawCard: '抽一张牌',
    damageUnit: '对单位造成伤害', healUnit: '单位恢复生命', killUnit: '消灭单位', damageHQ: '对总部造成伤害', healHQ: '总部恢复生命' };
  KG.parseBehaviorTrigger = function (trigger) {
    const m = /^behavior:(op|game)\/([^:]+):(before|during|after)$/.exec(trigger || '');
    return m ? { kind: m[1], action: m[2], phase: m[3] } : null;
  };
  // A single action has three separately awaited observation points. Effects
  // raised by a phase finish before the action advances to the next phase.
  KG.performBehavior = async function (state, kind, action, info, perform, valid) {
    const event = phase => runTrigger(state, { ...info, trigger: 'behavior:' + kind + '/' + action + ':' + phase,
      matchAlso: (info.behaviorAliases || []).map(alias => 'behavior:' + alias + ':' + phase), behavior: action, behaviorKind: kind, phase });
    await event('before');
    if (state.over || valid && !valid()) return false;
    await event('during');
    if (state.over || valid && !valid()) return false;
    const result = await perform();
    if (result !== false) { info = { ...info, result }; await event('after'); }
    return result;
  };
  KG.performImmediateBehavior = function (state, kind, action, info, perform) {
    const prefix = 'behavior:' + kind + '/' + action + ':';
    const prefixes = [prefix, ...(info.behaviorAliases || []).map(alias => 'behavior:' + alias + ':')];
    const matches = x => typeof x === 'string' && prefixes.some(p => x.startsWith(p));
    const listens = effect => matches(effect?.trigger) ||
      (Array.isArray(effect?.triggers) && effect.triggers.some(matches)) ||
      (effect?.trigger === 'counter' && (Array.isArray(effect.on) ? effect.on : [effect.on]).some(matches));
    const listeners = state.players.some((p,owner) => allUnitsOf(state, owner).some(u =>
      !u.dead && ((u.silenced ? [] : u.def.effects || []).concat(u.extraEffects || []).some(e => effectListenZone(e) !== 'hand' && listens(e)))) ||
      (state.phase === 'play' && p.hand.some(inst => !inst.suspended && handEffects(state, inst).some(e => effectListenZone(e) !== 'field' && listens(e)))) ||
      (p.hqEnchants || []).some(e => (e.effects || []).some(listens)) || (p.counters || []).some(c => (c.effects || []).some(listens)));
    if (listeners) return KG.performBehavior(state, kind, action, info, perform);
    // Keep startup drawing synchronous when there is nobody to react. Record
    // the phases so first-event limits still include actions before arrival.
    const event = (phase, extra = {}) => runTrigger(state, { ...info, ...extra, trigger: prefix + phase,
      matchAlso: (info.behaviorAliases || []).map(alias => 'behavior:' + alias + ':' + phase), behavior: action, behaviorKind: kind, phase });
    for (const phase of ['before', 'during']) event(phase);
    const result = perform();
    const after = value => { if (value !== false) event('after', { result: value }); return value; };
    return result && typeof result.then === 'function' ? result.then(after) : after(result);
  };
  for (const action of ['playCard', 'attack', 'move', 'endTurn']) {
    const original = KG[action];
    KG[action] = async function (state, ...args) {
      const owner = action === 'endTurn' ? state.active : args[0];
      const source = action === 'attack' || action === 'move' ? KG.unitByUid(state, args[1]) : null;
      const inst = action === 'playCard' ? state.players[owner]?.hand[args[1]] : null;
      const chooser = action === 'endTurn' ? args[0] : args[3 - (action === 'playCard' ? 1 : 0)];
      const valid = () => {
        if (action === 'endTurn') return !state.over && state.phase === 'play' && state.active === owner;
        if (action === 'playCard') {
          args[1] = state.players[owner].hand.indexOf(inst);
          return args[1] >= 0 && KG.canPlayCard(state, owner, args[1]).ok;
        }
        if (action === 'attack') return KG.canAttack(state, owner, args[1], args[2]).ok;
        return KG.canMove(state, owner, args[1], args[2], args[5]).ok;
      };
      // Invalid input is still handled by the original diagnostic path, but
      // does not produce a successful behavior or consume its phase listeners.
      if (!valid()) return original(state, ...args);
      const info = { owner, source, unit: source, chooser,
        ...(inst ? { played: cardDef(state, inst.id), card: cardDef(state, inst.id), inst } : {}),
        ...(action === 'attack' ? { target: args[2], defender: args[2]?.kind === 'unit' ? KG.unitByUid(state, args[2].uid) : null } : {}),
        ...(action === 'move' ? { from: source.zone, to: args[2] } : {}) };
      return KG.performBehavior(state, 'game', action, info, () => original(state, ...args), valid);
    };
  }
  KG.applyDeployTraits = applyDeployTraits;   // ★ 牌Q 卡包「触发山地效果」用

  async function execEffects(state, ctx, effects, chooser, trigger) {
    if (!KG.effects || !KG.effects.exec) return;
    await KG.effects.exec(state, ctx, effects, chooser, trigger);
  }
  KG.execEffects = execEffects;

  function recomputeAuras(state) {
    if (KG.effects && KG.effects.recomputeAuras) KG.effects.recomputeAuras(state);
    // ★ 烟幕禁则放在**光环之后**判：守护可能是这一轮刚由光环/效果授予的，
    //   先算光环再清扫，才能抓住"先有烟幕、后拿到守护"的顺序。
    enforceSmokeBans(state);
  }
  KG.recomputeAuras = recomputeAuras;

  /* ------------------------------------------- 战时修正：无视单位效果等 */
  // 当攻击者具有“无视单位效果”时，忽略目标的 mods
  KG.effectiveMods = function (a, t) {
    if (a && a.mods && a.mods.ignoreEnemyEffects) return newMods();
    return (t && t.mods) || newMods();
  };

  /* ------------------------------------------------------------------ 视图 */
  KG.view = function (state, pi) {
    const me = state.players[pi], foe = state.players[1 - pi];
    return {
      turn: state.turn, active: state.active, winner: state.winner, over: state.over,
      me: {
        name: me.name, hq: me.hq, hqMax: me.hqMax, kredits: me.kredits, maxKredits: me.maxKredits,
        deck: me.deck.length, hand: me.hand.map(function (h) { return h.id; }),
        handInsts: me.hand, discard: me.discard.length, fatigue: me.fatigue,
        support: me.support, frontline: frontlineOf(state, pi), counters: me.counters.length,
      },
      frontline: state.frontline,
      foe: {
        name: foe.name, hq: foe.hq, hqMax: foe.hqMax, kredits: foe.kredits, maxKredits: foe.maxKredits,
        deck: foe.deck.length, discard: foe.discard.length, fatigue: foe.fatigue,
        support: foe.support, frontline: frontlineOf(state, 1 - pi), counters: foe.counters.length,
        handCount: foe.hand.length,
        hand: foe.hand.map(function (h) { return h.revealed ? h.id : null; }),
        intel: (state.players[pi].intelSeen || []),
      },
    };
  };

  KG.serialize = function (state) {
    const s = Object.assign({}, state);
    delete s.rng; delete s.pool;
    return s;
  };

  if (typeof module !== 'undefined' && module.exports) module.exports = KG;
})(typeof window !== 'undefined' ? window : globalThis);
