/* Effect Inspector: lossless editing of the engine DSL.
 * Capabilities come from the running engine. Card samples provide labels and
 * optional references, never legality or automatic confirmation.
 */
(function (global) {
  'use strict';
  const KG = global.KG = global.KG || {};

  /* ============================================ 核心：无损通用模型
   * spec §2.4「永不静默丢数据」：模型把**每个字段**都存成 key+node，
   * 未知字段照样保留 → fromModel(toModel(x)) 必须与 x 完全相等（见 inspector-selftest.js）。
   */
  function toModel(v) {
    if (Array.isArray(v)) return { t: 'a', items: v.map(toModel) };
    if (v && typeof v === 'object') {
      return { t: 'o', fields: Object.keys(v).map(k => ({ key: k, node: toModel(v[k]) })) };
    }
    return { t: 'v', v: v };
  }
  function fromModel(n) {
    if (!n) return undefined;
    if (n.t === 'a') return n.items.map(fromModel);
    if (n.t === 'o') { const o = {}; n.fields.forEach(f => { o[f.key] = fromModel(f.node); }); return o; }
    return n.v;
  }
  const clone = v => JSON.parse(JSON.stringify(v === undefined ? null : v));

  /* ============================================ 友好名称（种子词典）
   * effects.js 的 OPS 只是实现函数，没有参数元数据，label 无法自动推断。
   * 想改名称：改这里，或直接写进 game/data/effect-schema.json 的 label（重新生成会保留）。
   */
  const SEED_OP = {
    addCardToHand: '加入手牌', buff: '属性增减', damage: '造成伤害', draw: '抽牌', destroy: '消灭',
    grantMod: '获得常驻修正', grant: '获得词条', gainKredits: '获得指挥点', setWeather: '设置天气',
    damageHQ: '对总部造成伤害', summon: '召唤', conditional: '如果…则', develop: '开发', pin: '压制',
    chooseOne: '抉择', gainKreditSlot: '获得指挥点槽', opCostMod: '行动花费修正', shuffleIn: '洗入卡组',
    hqMaxUp: '总部上限提升', upgradeSelf: '升为老兵', hqEnchant: '总部附魔', repeat: '重复',
    forEach: '逐个执行', setStats: '设置身材', loseDefense: '失去防御力', heal: '治疗', retreat: '撤退',
    discard: '弃牌', silence: '沉默', unpin: '解除压制', move: '移动', costReduce: '费用降低',
    log: '日志（占位）', script: '内联脚本（危险）', auraBuff: '光环增益', removeKeyword: '失去词条',
    takeControl: '夺取控制', deckToHand: '卡组→手牌', discover: '发现', mill: '磨牌', reveal: '明牌',
    setVar: '设置变量', endTurnNow: '立即结束回合', fight: '互相伤害', transformUnit: '变形',
    /* ★ 补名（Alan 09-27「手搭模式倒是也加上啊」）：grantEffect / asVar 以前没中文名，
     *   专家模式的 op 下拉（opOptions 文案链）里露英文「grantEffect ×5」「asVar ·库存未用」，
     *   中文搜「授予」「变量」根本搜不到。 */
    grantEffect: '获得效果（挂在单位身上）', asVar: '作为变量',
    /* ★ nthTime（Alan 09-28「三次过后，升为老兵」）：卡面写「N次过后，X」/「每N次，X」时
     *   编译器自动产出；手搭模式里也能直接加这个动作（then 里填"第 N 次到了要做的事"）。 */
    nthTime: '第 N 次才做（计数门控）',
  };
  const SEED_COND = {
    and: '且', or: '或', not: '非', compare: '数值比较', unitCount: '单位数量',
    controlsType: '控制某兵种', hasKeyword: '拥有词条', targetPinned: '目标被压制',
    controlFrontline: '控制前线', frontlineEmpty: '前线为空', kreditsAtLeast: '指挥点不少于',
    kreditsAtMost: '指挥点不多于', handSize: '手牌数', deckSize: '卡组数', turnAtLeast: '回合数不少于',
    isUnitType: '是某兵种', targetIsUnit: '目标是单位', targetAlive: '目标存活', targetDead: '目标已死',
    damaged: '已受伤', undamaged: '未受伤', var: '变量比较', unitInZone: '单位在某区', playerIs: '阵营判定',
    /* ★ Alan 09-28「变量那张卡的词条也要能引用」 */
    cardVarHasKeyword: '变量卡有某词条',
  };
  const SEED_FIELD = {
    trigger: '触发', actions: '动作', targets: '目标声明', condition: '条件', target: '目标',
    amount: '数值', count: '数量', times: '次数', attack: '攻击力', defense: '防御力',
    side: '阵营', zone: '区域', keyword: '词条', mod: '修正', duration: '持续时间',
    prompt: '提示语', label: '选项名', name: '卡名', filter: '筛选', value: '值',
    cmp: '比较', sel: '选择方式', ref: '引用', options: '选项', id: '目标代号',
    cardType: '卡牌类型', kind: '目标种类', spec: '单位筛选', opCost: '行动花费',
    aura: '光环', cardFields: '卡面级字段', vars: '变量', oncePerTurn: '每回合一次',
    else: '否则', then: '则', items: '子条件', item: '子条件', text: '文本', excludeSelf: '排除自己',
    /* ★ nthTime 的参数（Alan 09-28）：手搭模式里「第 N 次才做」这一行会用到 */
    key: '计数名（同名的算同一串）', every: '每 N 次就再来一轮（达成后清零）',
    reset: '达成后清零', on: '计数记在哪（global 本局 / unit 这个单位身上）',
  };
  const SEED_VALUE = {
    self: '自己', enemy: '敌方', friendly: '友方', any: '任意', both: '双方',
    all: '所有', random: '随机', ref: '引用', eventUnit: '事件单位', defender: '被攻击者',
    summoned: '被召唤者', turn: '本回合', hq: '总部', unit: '单位', support: '支援阵线',
    frontline: '前线', blitz: '闪击', valor: '奋战', fury: '狂怒', impact: '冲击',
    hqAdjacent: '总部相邻处', sameZone: '与来源同阵线',
    ambush: '伏击', guerrilla: '游击', smokescreen: '烟幕',
    /* ★ 对战词条全集（2026-09-26「词条不全，补齐」）：与 engine.js KEYWORDS 30 个对齐，
     *   下拉另走 enumOptionsFor('keyword') 从 KG.KEYWORDS 动态取，这里是人话兜底。
     *   ⚠ safekeeping（守护）是幽灵词条：引擎 KEYWORDS 没有、卡池 0 使用 → 删除，固守 = guard。 */
    guard: '固守', sponge: '海绵装甲', magnetic: '磁反应装甲', intel: '情报',
    mobilize: '动员', deployment: '部署', deathrattle: '亡计', repair: '维修',
    salvage: '打捞', pin: '压制', veteran: '老兵', charge: '充能',
    armor: '重甲', lightArmor: '轻甲', exile: '流亡', shield: '强磁护盾',
    confiscate: '收缴', revealed: '明牌', airdrop: '空投', tsekep: '硬铝弹',
    alpine: '山地', synergy: '协力', conceal: '隐蔽',
    immune: '免疫', ignoreOrders: '无视指令', canMoveAndAttack: '可移动并攻击',
    vsType: '对战某兵种修正', vsHq: '对战总部修正', takeDoubleFrom: '受某兵种双倍伤害', extraTypes: '额外视为某兵种',
  };
  const TRIGGERS = {
    deploy: '部署时', death: '亡计（被消灭时）', drawn: '抽到时', mobilize: '动员（移前线）',
    choose: '抉择', afterAttack: '本单位攻击后', attack: '友方单位攻击时', attacked: '敌方单位攻击时',
    afterKill: '消灭 N 个后', damaged: '本单位受伤后', turnStart: '友方回合开始时（每个回合）',
    turnEnd: '友方回合结束时（每个回合）', orderPlayed: '使用指令后', unitDeployed: '单位部署时',
    unitMobilized: '单位移至前线时', friendlyDeath: '友方单位被消灭时', order: '打出指令（默认）',
    passive: '常驻 / 光环', counter: '反制', chargeNow: '充能完毕时', hqDamaged: '总部受伤时',
    enemyKilled: '消灭敌方单位时', impactUsed: '本单位冲击后', afterAttackHQ: '攻击总部后',
    unpinned: '压制结束时', kreditsGained: '获得指挥点时', targetedByOrder: '被指令指定时',
    extraDraw: '友方额外抽牌时（非回合开始，或回合开始第 2 张起）',
    friendlySuppressed: '友方单位被抑制时', shuffle: '洗切卡组时（洗入/洗切动作后派发）', unitLeft: '单位不因消灭离开战场时（移除/撤退）',
    activated: '激活（旧数据兼容）', developed: '友方开发时',
  };

  /* ============================================ 校验器（spec §2.5）
   * 返回 {errors, warnings, infos}，每条带 path（如 '0 > actions > 2'）
   */
  const Contract = global.KG_EFFECT_CONTRACT || (typeof require === 'function' ? require('./effect-contract.js') : null);
  function validate(dsl, schema, opts) { return Contract.validate(dsl, schema, opts); }

  /* 「常驻/光环里的动作」哪些能**每轮重算持续执行** —— 名单唯一真源是引擎 effects.js
   * （KG.effects.PASSIVE_CONT_OPS）；引擎没加载时退回同一份字面量，校验器不能因为拿不到引擎就静默放过。 */
  function PASSIVE_CONT_OPS() {
    const e = (global.KG && global.KG.effects && global.KG.effects.PASSIVE_CONT_OPS) || null;
    return e || { buff: 1, buffAll: 1, grantMod: 1, grant: 1, grantAll: 1, removeKeyword: 1,
      opCostMod: 1, opCostModAll: 1, canMoveAndAttack: 1 };
  }

  /* 全量中文对照（外置 game/data/effect-labels.json，页面 fetch 后挂在 KG_EFFECT_LABELS） */
  function applyLabels(L) {
    L = L || global.KG_EFFECT_LABELS || {};
    Object.keys(L.op || {}).forEach(k => { SEED_OP[k] = L.op[k]; });
    Object.keys(L.cond || {}).forEach(k => { SEED_COND[k] = L.cond[k]; });
    Object.keys(L.field || {}).forEach(k => { SEED_FIELD[k] = L.field[k]; });
    Object.keys(L.value || {}).forEach(k => { SEED_VALUE[k] = L.value[k]; });
  }
  applyLabels();   // 页面若在加载脚本前已备好表就立即生效

  /* ============================================ 卡面句子 → effects 合并（零 DOM）
   * 制卡台 ui.js 的 compileEditor 只做「整段替换」；检查器需要「追加」，
   * 而追加最容易踩的坑是 **targets 的 tN 与已有目标撞车**（第二个 t1 覆盖第一个）。
   * 所以合并前统一 renumberTargets：新块里的 id 若与已用集合冲突 → 换新号，
   * 同时把该块内部对它的引用一起改掉。
   */
  function remapRefs(v, map) {
    if (v == null) return;
    if (typeof v === 'string') return;
    if (Array.isArray(v)) { v.forEach(x => remapRefs(x, map)); return; }
    if (typeof v !== 'object') return;
    if (typeof v.op === 'string' && v.target !== undefined) { }   // 动作/条件对象照样按字段遍历
    Object.keys(v).forEach(k => {
      const x = v[k];
      if (typeof x === 'string') { if (Object.prototype.hasOwnProperty.call(map, x)) v[k] = map[x]; }
      else remapRefs(x, map);
    });
  }
  /* effects（DSL 数组）→ 用已用 id 集合重编号，返回 {effects, map}（不改原数组） */
  function renumberTargets(effects, usedIds) {
    const out = clone(effects || []);
    const used = {};
    (usedIds || []).forEach(id => { used[id] = 1; });
    const map = {};
    (out || []).forEach(e => {
      if (!e || typeof e !== 'object' || !Array.isArray(e.targets)) return;
      const local = {};
      let changed = false;
      e.targets.forEach(t => {
        if (!t || t.id == null) return;
        const old = String(t.id);
        if (local[old] != null) { if (local[old] !== old) { t.id = local[old]; changed = true; } return; }
        let id = old;
        while (used[id] || Object.prototype.hasOwnProperty.call(map, id)) {
          const m = id.match(/^(.*?)(\d+)$/);
          id = m ? m[1] + (Number(m[2]) + 1) : id + '2';
        }
        used[id] = 1; local[old] = id;
        if (id !== old) { map[old] = id; t.id = id; changed = true; }
      });
      if (changed) remapRefs(e, local);
    });
    return { effects: out, map };
  }
  function usedTargetIds(effects) {
    const out = [];
    (effects || []).forEach(e => {
      if (!e || typeof e !== 'object') return;
      (e.targets || []).forEach(t => { if (t && t.id != null && out.indexOf(t.id) < 0) out.push(t.id); });
    });
    return out;
  }
  /* mode: 'replace' 整体替换 | 'append' 追加到末尾（追加时 tN 强制避撞） */
  function mergeCompiled(base, incoming, mode) {
    const b = Array.isArray(base) ? clone(base) : [];
    const inc = Array.isArray(incoming) ? clone(incoming) : [];
    if (mode === 'replace') return inc;
    const used = usedTargetIds(b);
    const mid = [];
    inc.forEach(e => {
      const r = renumberTargets([e], used);
      r.effects.forEach(x => {
        mid.push(x);
        usedTargetIds([x]).forEach(id => { if (used.indexOf(id) < 0) used.push(id); });
      });
    });
    return b.concat(mid);
  }
  /* 配方库过滤（零 DOM）：q 匹配 name / text / notes / effects；triggers / ops 为精确筛选 */
  function recipeFilter(index, q, trigger, op) {
    const items = (index && index.items) || [];
    const s = String(q || '').trim().toLowerCase();
    return items.filter(it => {
      if (trigger && (it.triggers || []).indexOf(trigger) < 0) return false;
      if (op && (it.ops || []).indexOf(op) < 0) return false;
      if (!s) return true;
      const hay = [it.name, it.text, it.notes, it.effectsText, it.id].map(x => String(x == null ? '' : x)).join('\n').toLowerCase();
      return hay.indexOf(s) >= 0;
    });
  }

  /* ============================================ undo / redo（快照差分）
   * mutation 散落在十几处，逐处埋点必漏 → 在 rebuild() 入口对 JSON 做差分：
   * rebuild 触发的变更 = 一步；touch() 连续打字 600ms 防抖合并；换卡/保存清栈。
   */
  function History(limit) {
    this.limit = limit || 50;
    this.undoStack = []; this.redoStack = [];
    this.last = null; this.timer = null;
  }
  History.prototype.snap = function (json) { return typeof json === 'string' ? json : JSON.stringify(json); };
  History.prototype.reset = function (json) {
    clearTimeout(this.timer); this.timer = null;
    this.undoStack = []; this.redoStack = []; this.last = this.snap(json);
  };
  History.prototype.check = function (json) { return this.snap(json) !== this.last; };
  History.prototype.push = function (json) {
    const s = this.snap(json);
    if (s === this.last) return false;
    if (this.last != null) { this.undoStack.push(this.last); if (this.undoStack.length > this.limit) this.undoStack.shift(); }
    this.last = s; this.redoStack = [];
    return true;
  };
  History.prototype.canUndo = function () { return this.undoStack.length > 0; };
  History.prototype.canRedo = function () { return this.redoStack.length > 0; };
  History.prototype.undo = function () {
    if (!this.undoStack.length) return null;
    this.redoStack.push(this.last);
    this.last = this.undoStack.pop();
    return this.last;
  };
  History.prototype.redo = function () {
    if (!this.redoStack.length) return null;
    this.undoStack.push(this.last);
    this.last = this.redoStack.pop();
    return this.last;
  };

  const Core = { toModel, fromModel, clone, validate, SEED_OP, SEED_COND, SEED_FIELD, SEED_VALUE, TRIGGERS,
    mergeCompiled, renumberTargets, usedTargetIds, recipeFilter, History };

  /* ============================================ UI（只在浏览器装配） */
  function label(kind, id, key) {
    if (kind === 'op') return SEED_OP[id] || String(id);
    if (kind === 'cond') return SEED_COND[id] || String(id);
    if (kind === 'trigger') return TRIGGERS[id] || String(id);
    if (kind === 'value') return SEED_VALUE[String(id)] != null ? SEED_VALUE[String(id)] : String(id);
    return SEED_FIELD[key] || String(key);
  }

  function mount(host, api) {
    applyLabels();
    return global.KG_EFFECT_EDITOR.mount(host, api || defaultApi(), Core);
  }

  function defaultApi() {
    const read = (key, fallback) => { try { return JSON.parse(localStorage.getItem('kg.' + key)) || fallback; } catch (e) { return fallback; } };
    const write = (key, value) => localStorage.setItem('kg.' + key, JSON.stringify(value));
    const base = id => (global.KG_CARDS || []).find(c => c.id === id);
    const isCustom = id => !base(id) && read('custom', []).some(c => c && c.id === id);
    const custom = id => read('custom', []).find(c => c && c.id === id);
    function record(id) {
      const source = base(id) || custom(id) || {};
      return Object.assign({}, source, (global.KG_EFFECT_OVERLAY || {})[id] || {}, read('edits', {})[id] || {});
    }
    function saveCard(id, patch) {
      if (isCustom(id)) {
        const list = read('custom', []), i = list.findIndex(c => c && c.id === id);
        list[i] = Object.assign({}, list[i], read('edits', {})[id] || {}, patch);
        const previous = localStorage.getItem('kg.custom');
        if (previous && previous !== '[]') localStorage.setItem('kg.custom.bak', previous);
        write('custom', list);
        // Preserve a pre-existing edit layer; never erase unrelated keywords or fields.
        const edits = read('edits', {});
        if (edits[id]) { edits[id] = Object.assign({}, edits[id], patch); write('edits', edits); }
      } else {
        const edits = read('edits', {}); edits[id] = Object.assign({}, edits[id] || {}, patch); write('edits', edits);
      }
    }
    let images = {};
    const api = {
      isCustom, saveCard,
      list: () => {
        const removed = read('removed', {}), ids = new Set();
        (global.KG_CARDS || []).forEach(c => c && ids.add(c.id));
        read('custom', []).forEach(c => c && ids.add(c.id));
        return [...ids].filter(id => !removed[id] && !record(id).removed).map(id => Object.assign(record(id), { custom: isCustom(id) }));
      },
      getFx: id => clone(record(id).effects || []),
      setFx: (id, effects, effectStatus, warnings) => saveCard(id, { effects, effectStatus, compileWarnings: warnings || [] }),
      setCardFields: saveCard,
      getWarnings: id => record(id).compileWarnings || [],
      schema: () => global.KG_EFFECT_SCHEMA || {},
      cardArt: c => images[c.id] || c.art || (c.src ? '../' + c.src : ''),
      altArt: c => c.src ? '../' + String(c.src).replace(/\.png$/i, 'y.png') : '',
      newCard: partial => {
        const id = 'custom/new-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8);
        const card = Object.assign({ id, name: '新卡', set: '自定义', cardType: 'unit', unitType: 'infantry',
          cost: 1, attack: 1, defense: 1, opCost: 1, rarity: 'iron', text: '', effects: [], token: false }, partial, { id });
        const list = read('custom', []); list.push(card); write('custom', list); return card;
      },
      customCount: () => read('custom', []).length,
      deleteCard: id => { if (!isCustom(id)) return; write('custom', read('custom', []).filter(c => c.id !== id)); },
      loadImages: () => new Promise(resolve => {
        const r = indexedDB.open('kg-cards', 1);
        r.onupgradeneeded = () => r.result.createObjectStore('images');
        r.onerror = () => resolve({});
        r.onsuccess = () => {
          const db = r.result, tx = db.transaction('images', 'readonly'), cursor = tx.objectStore('images').openCursor();
          cursor.onsuccess = () => { const c = cursor.result; if (c) { images[c.key] = URL.createObjectURL(c.value); c.continue(); } };
          tx.oncomplete = () => { db.close(); resolve(images); }; tx.onerror = () => { db.close(); resolve({}); };
        };
      }),
    };
    return api;
  }

  const BUILD = '2026-10-03 · 原语组合';
  const api = { Core, mount, validate, toModel, fromModel, clone, label, BUILD };
  KG.inspector = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
