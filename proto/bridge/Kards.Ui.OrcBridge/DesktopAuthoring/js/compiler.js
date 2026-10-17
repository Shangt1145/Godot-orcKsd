/* ==========================================================================
 * KG Card Text Compiler —— 中文卡面文本 → 效果 DSL
 * 用于"导入我的 DIY 卡牌"：新卡的文本若符合常见句式，可自动生成可执行效果
 * 覆盖不到的部分会在编辑器里标出，用户可手改 JSON
 * ========================================================================== */
(function (global) {
  'use strict';
  const KG = global.KG = global.KG || {};
  const C = KG.compiler = KG.compiler || {};

  const CN_NUM = { 一: 1, 两: 2, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10, 零: 0 };
  function num(s) {
    if (s == null) return null;
    if (global.KG_PRIMITIVES && global.KG_PRIMITIVES.num) return global.KG_PRIMITIVES.num(s);
    s = String(s).trim();
    if (/^-?\d+$/.test(s)) return parseInt(s, 10);
    if (CN_NUM[s] != null) return CN_NUM[s];
    if (/^十[一二三四五六七八九]$/.test(s)) return 10 + CN_NUM[s[1]];
    if (/^[一二三四五六七八九]十$/.test(s)) return CN_NUM[s[0]] * 10;
    return null;
  }

  // 兵种词表：从 primitives.js 的 UNIT_TYPES（全项目唯一权威源）取，不再另抄一份。
  //   只额外补一个 '单位' → null（表示"任意单位，不限兵种"），这是编译器特有的默认值。
  function buildTypes() {
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    const base = (P && P.UNIT_TYPES) ? P.UNIT_TYPES : {
      '步兵': 'infantry', '坦克': 'tank', '火炮': 'artillery', '炮兵': 'artillery',
      '战斗机': 'fighter', '轰炸机': 'bomber', '空军': ['fighter', 'spacefighter', 'bomber'],
      '太空战机': 'spacefighter', '巡地舰': 'landcruiser', '巡航舰': 'cruiser',
      '太空': 'space', '太空单位': 'space', '陆军': ['infantry', 'tank', 'artillery'],
      '工事': 'structure', '建筑': 'structure',
    };
    return Object.assign({}, base, { '单位': null });
  }
  const TYPES = buildTypes();

  /* 词条词表（用于识别"纯词条行"）。
   * 卡面里单独成行的「闪击」「游击」「重甲1 守护」这类**只有一个/几个词条名**的行，
   * 表达的是卡牌的关键词属性，不是效果文本 —— 它们已由 cards.json 的
   * keywords / kwMap / kwValues 承载，引擎按词条结算。
   * 编译器必须跳过它们，否则会产出「未实现：闪击 游击」这类假告警。 */
  const KW_WORDS = ['闪击', '固守', '守护', '烟幕', '隐蔽', '伏击', '动员', '狂怒', '奋战', '亡计', '遗言',
    '老兵', '流亡', '流放', '冲击', '游击', '重甲', '轻甲', '硬铝弹', '强磁护盾', '空投', '海绵装甲',
    '情报', '压制', '抑制', '收缴', '明牌', '抉择', '磁反应装甲', '反制', '充能'];
  function isKeywordOnlyLine(line) {
    const s = String(line || '').trim();
    if (!s || s.length > 24) return false;                 // 太长的不可能是纯词条
    const parts = s.split(/[\s　]+/).filter(Boolean);
    if (!parts.length) return false;
    return parts.every(p => KW_WORDS.indexOf(p) >= 0 || KW_WORDS.indexOf(p.replace(/\d+$/, '')) >= 0);
  }
  C.isKeywordOnlyLine = isKeywordOnlyLine;

  /* ★★ 复合型充能（武装）句式解析（制作者 2026-09-24 定口径）：
   *   「具有重甲3，本单位攻击时，具有+3攻击力」
   *     → { trigger:'chargeArm', grants:[{kw:'armor',v:3}], onAttack:{attackBonus:3} }
   *   语义：充能完毕后**立即武装**（挂词条 + 记住攻击加成）；**本单位攻击后全部失去**，
   *         然后才重新计时（武装期间充能暂停）。"充能效果必须全部触发（=攻击消费掉
   *         整个武装）才会进入下一次充能"。
   *   两个段都可缺（只写词条、或只写攻击加成）。整句都不匹配返回 null → 照常走通用解析
   *   （即普通的 chargeGate 门控路径），不影响既有卡面。
   *   引擎侧实现：engine.js 的 chargeArmIfAny / dischargeChargeArm。 */
  function parseChargeArmLine(text) {
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    const KW = (P && P.KW_CN) || {};                       // 中文词条 → 引擎 id（重甲→armor…）
    const CN = { '一':1,'二':2,'两':2,'三':3,'四':4,'五':5,'六':6,'七':7,'八':8,'九':9,'十':10 };
    const num = (s) => /^\d+$/.test(s) ? parseInt(s, 10) : (CN[s] || 1);
    let rest = String(text || '').replace(/[。.]\s*$/, '').trim();
    if (!rest) return null;
    // 段1：「具有重甲3」→ grants（词条名后可带中文/阿拉伯数字；布尔词条缺省 v=1）
    const grants = [];
    const gm = rest.match(/^具有\s*([\u4e00-\u9fa5]+?)([一二两三四五六七八九十\d]*)\s*(?=[，,]|$)/);
    if (gm && KW[gm[1]]) {
      grants.push({ kw: KW[gm[1]], v: gm[2] ? num(gm[2]) : 1 });
      rest = rest.slice(gm[0].length).replace(/^[，,]\s*/, '');
    }
    // 段2：「本单位攻击时，具有+3攻击力」→ onAttack
    let onAttack = null;
    const am = rest.match(/^本单位?攻击时\s*[，,]\s*具有\s*\+?([一二两三四五六七八九十\d]+)\s*攻击力$/);
    if (am) {
      onAttack = { attackBonus: num(am[1]) };
      rest = '';
    }
    if (!grants.length && !onAttack) return null;          // 一段都没认出来 → 不是武装句
    const fx = { trigger: 'chargeArm' };
    if (grants.length) fx.grants = grants;
    if (onAttack) fx.onAttack = onAttack;
    return { fx: fx, rest: rest.trim() };                  // rest 非空 = 武装句后面还有别的正文
  }
  C.parseChargeArmLine = parseChargeArmLine;

  /* ★★ 「充能完毕后」复合句拆分（制作者 2026-09-24 定口径，不过拟合单一句式）：
   *   一句卡面里可能**混着两类效果**：
   *     「充能完毕后，所有友方单位具有+4攻击力，友方使用指令时，对敌方总部造成5点伤害」
   *      └─ 立即动作（充能完毕就执行）─┘└─ 监听触发（充能完毕后**等事件**）──────┘
   *   判据（通用）：剥掉前缀后在**逗号段边界**上找 TRIGGERS 命中点 ——
   *     · 命中段起的后续 = 监听触发（"…时"子句）→ 交回主循环走 chargeGate；
   *     · 命中点之前的段 = 立即动作（无"时"）→ 编成 chargeNow（充能完毕立即执行）。
   *   ⚠ 只在段边界找、绝不从句中抠 —— 动作句里"具有冲击时"这类合法原语句不会被误拆。
   *   ⚠ 消费端：监听段重新以「充能完毕后，」前缀入队（复用现有剥前缀 → chargeGate 通路）。 */
  function splitChargeNowAndListen(text) {
    const segs = String(text || '').split(/[，,]\s*/);
    if (segs.length < 2) return null;
    for (let k = 1; k < segs.length; k++) {
      const tail = segs.slice(k).join('，');
      for (const entry of TRIGGERS) {
        const re = Array.isArray(entry) ? entry[0] : entry.re;
        if (re.test(tail)) return { head: segs.slice(0, k).join('，'), tail: tail };
      }
    }
    return null;
  }
  C.splitChargeNowAndListen = splitChargeNowAndListen;

  // 触发前缀：可带兵种限定（"友方太空单位部署时"）；"敌方…部署时"表示监听对手
  const TRIGGERS = [
    [/^部署[：:]\s*/, 'deploy'],
    //   牌Q：「部署。」（触发词后直接句号）也要认
    [/^部署\s*[。.]\s*/, 'deploy'],
    [/^亡计[：:]\s*/, 'death'],
    [/^动员[：:]\s*/, 'mobilize'],
    [/^抉择[：:]\s*/, 'choose'],
    [/^本单位?攻击后[，,]?\s*/, 'afterAttack'],
    [/^对战后[，,]?\s*/, 'afterAttack'],
    // 「攻击X时将其Y」→ afterAttack（攻击特定类型时的监听）
    //   ★ 必须用**零宽前瞻**，不能吃掉正文：正文里带着"攻击哪个兵种"这个关键限定，
    //     原实现 `^攻击[^，,。]{1,10}时\s*` 会把"攻击太空单位时"整段吃掉，
    //     只剩"将其压制"交给动作原语 → 动作原语不认识"其"这个代词，
    //     退化成通用选靶（side:'any'）：弹窗让玩家选人，还能选中**自己的**单位
    //     （用户报的「蜂鸟战斗机压制自己的太空单位」）。改为前瞻后，正文完整保留，
    //     由原语表里的「攻击X时将其Y」规则统一产出
    //     conditional(defenderIsType X) + op(target:{sel:'ref',ref:'defender'})。
    [/^(?=攻击[^，,。]{1,10}时[，,]?\s*(?:将|把|使)?\s*(?:其|它))/, 'afterAttack'],
    // 兜底：纯粹的「攻击时…」（没有"将其/它"代词）仍按老行为吃掉前缀
    [/^攻击[^，,。]{1,10}时[，,]?\s*/, 'afterAttack'],
    // 「消灭受到本单位对战伤害的X」→ 隐含 afterAttack（要"受过本单位伤害"意味着打过之后）。
    //   用**零宽前瞻**只标记触发、不吃掉正文（body 仍是完整一句，交给动作规则解析）。
    // ★★「消灭受到本单位（对战）伤害的单位（后）」= **攻击必杀**（2026-09-24 制作者口径）：
    //   是**效果**不是监听 —— 你攻击的那个单位无视血量直接消灭（99 血也死）。
    //   产出 afterAttack + 注入 destroy(defender)（三元组 extraActions，见主循环）。
    //   ⚠ 必须整段剥掉前缀：以前是零宽前瞻只挂触发名，正文「消灭…」被当成**主动消灭**
    //     解析成"选择一个单位并消灭"（语义完全反了，制作者爆骂的那条）。
    //   目标已被互伤打死 → destroy 空操作无害；打总部时 defender 不存在 → 同样空转。
    [/^每?消灭(?:并)?受到本单位(?:对战)?伤害的单位(?:后|时)?[，,]?\s*/,
      'afterAttack', { extraActions: [{ op: 'destroy', target: { sel: 'ref', ref: 'defender' } }] }],
    [/^本单位?攻击时[，,]?\s*/, 'attack'],
    // 「友方单位攻击时，抽一张牌」= 监听"我方任意单位攻击"（引擎 attack 事件在 owner 内广播）
    //   ★ 量词可有可无：卡面也写「友方**一个**单位攻击时」（充能那个例子的后半句就是这种）。
    [/^友方(?:一个|任意|某个|每个)?单位攻击时[，,]?\s*/, 'unitAttacked'],
    [/^敌方单位攻击时[，,]?\s*/, 'unitAttacked'],
    // ★ 「抽取：抽一张空军」= **抽到这张牌时**触发它自己的效果（自身语义，和亡计同一档）
    [/^抽取[：:]\s*/, 'drawn'],
    [/^抽到此?牌[：:]\s*/, 'drawn'],
    // ★ 充能完毕（无条件）：「充能完毕时，额外获得一个指挥点槽」→ 到点**立即**执行一次。
    //   有条件的「充能完毕后，<事件>，…」不在这里（它由行处理剥前缀 + chargeGate 门控）。
    [/^充能完毕时\s*[，,]?\s*/, 'chargeNow'],
    [/^本单位?受到伤害(?:后|时)[，,]?\s*/, 'damaged'],
    // ★ 「被攻击时…」（水箱坦克「被攻击时先获得重甲1，直至本回合结束」）→ attacked（自身被攻击触发）
    [/^被攻击时[，,]?\s*/, 'attacked'],
    // 「每受到一次攻击…」（USG104团；卡面异写「此前有方案为每受到一次攻击」一并吃掉）
    [/^(?:此前有方案为)?每受到(?:一|1)次攻击[，,]?\s*/, 'attacked'],
    [/^友方回合开始时[，,]?\s*/, 'turnStart'],
    [/^友方回合结束时[，,]?\s*/, 'turnEnd'],
    // 牌Q 卡包（2026-09-25）：同义写法补齐。
    //   「友方回合结束后，完全修复所有友方单位」（山地第17医疗团）
    //   「（每）回合结束时，…」（TF465编队 / 放良先锋458组 / G-559）
    [/^友方回合结束后[，,]?\s*/, 'turnEnd'],
    [/^(?:每)?回合结束时[，,]?\s*/, 'turnEnd'],
    // 「一回合一次，友方使用指令后，…」→ 监听已方打出指令（oncePerTurn 由效果定义承担）
    [/^(?:一回合一次[，,]?\s*)?友方使用指令(?:后|时)[，,]?\s*/, 'orderPlayed'],
    // ★ 「友方使用<卡名>后/时…」→ 同一个 orderPlayed 事件 + 条件"打出的就是这张牌"
    //   （第466步兵团：「友方使用补给后，获得+1攻击力」）
    [/^(?:一回合一次[，,]?\s*)?友方使用\s*([^，,。]{1,10}?)\s*(?:后|时)[，,]?\s*/, 'orderPlayed'],
    [/^友方(?:([\u4e00-\u9fa5]{1,4})?单位)?部署时[，,]?\s*/, 'unitDeployed'],
    // 牌Q 卡包（2026-09-25）：「其他友方单位部署时」/「友方空降兵部署时」（兵种词后**不带"单位"**）
    [/^(?:其他|其它)?友方([\u4e00-\u9fa5]{1,4})?部署时[，,]?\s*/, 'unitDeployed'],
    //   受伤害（friendlyDamaged 引擎已有事件，之前没接触发前缀）
    [/^(?:其他|其它)?友方(?:前线)?([\u4e00-\u9fa5]{0,4}?)?单位受到伤害时[，,]?\s*/, 'friendlyDamaged'],
    //   抽牌时（drawn 事件引擎已有）
    [/^友方抽牌时[，,]?\s*/, 'drawn'],
    //   本单位冲击后（impact 消耗时广播 impactUsed）
    [/^本单位冲击后[，,]?\s*/, 'impactUsed'],
    //   攻击敌方总部后 / 时（afterAttackHQ）
    [/^本单位攻击敌方总部(?:后|时)[，,]?\s*/, 'afterAttackHQ'],
    //   压制结束（解除压制时广播 unpinned）
    [/^压制结束时[，,]?\s*/, 'unpinned'],
    [/^敌方([\u4e00-\u9fa5]{1,4})?单位部署时[，,]?\s*/, 'unitDeployed'],
    [/^敌方部署([\u4e00-\u9fa5]{0,4})?时[，,]?\s*/, 'unitDeployed'],
    // 「(本单位)消灭N个敌方单位后」→ afterKill（击杀监听）
    //   ★ 两处都放宽过：
    //     ① 数量词：原来只认「一个」，卡面写「消灭**两个**敌方单位后升为老兵」就漏了
    //        → 现在认 一/两/三/N 个（名/辆/架/艘/张）。
    //     ② 主语可省：卡面第二行常直接写「消灭两个敌方单位后…」（承接上一行的"该单位"），
    //        原来强制 `^本单位?消灭` → 无主语的行匹配不上 → 那半句掉进**动作**解析，
    //        变成"无条件消灭两个敌方单位"（空投师的 bug：该是条件，却被直接执行）。
    [/^(?:本单位?|该单位|它|这个单位)?消灭(?:[一二两三四五六七八九十\d]+\s*[个名辆架艘张])?[^，,。]{0,12}后[，,]?\s*/, 'afterKill'],
    // 「移上前线后」→ mobilize（动员）。⚠ 排除「移上前线时，前线至多有N个单位」
    //   —— 那是**常驻容量规则**（frontlineMaxOverride），不是"移上前线"触发。
    [/^(?:本单位?)?移(?:上|到|至)前线(?:后|时)(?!\s*[，,]?\s*前线至多有)[，,]?\s*/, 'movedToFrontline'],
    // 「（一回合一次，）友方单位移至前线时」→ unitMobilized（监听友方单位动员）
    [/^(?:一回合一次[，,]?\s*)?友方单位移(?:至|到|上)前线时[，,]?\s*/, 'unitMobilized'],
    // 牌Q 卡包（2026-09-25）：「友方单位移动时」（不限前线）——引擎只有动员/移动广播，按移动处理
    [/^(?:一回合一次[，,]?\s*)?友方单位移动时[，,]?\s*/, 'unitMobilized'],
    // 牌Q（2026-09-25）：「友方空降兵部署、移动、攻击时」= 三个事件共用一个监听（triggers[]）
    [/^友方([\u4e00-\u9fa5]{1,4}?)部署[、，,]\s*移动[、，,]\s*攻击时[，,]?\s*/, 'unitDeployed|mobilize|attack'],
    // 「友方总部受到伤害时」→ hqDamaged
    //   ★ 2026-09-22（制作者报）：「总部受到**来自<兵种>的**伤害时」也必须认。
    //     捕获"来自X的"里的兵种词 → 做成效果级条件 `damageFromType`（见下方 trigCondition 分支）。
    //     旧正则要求"总部受到伤害时"**连续**，被"来自陆军的"挡住 → 匹配失败 →
    //     前缀留在正文里 → 动作规则从**中间**匹配走后半句 → 触发+来源限定静默丢失。
    //   牌Q 卡包（2026-09-25）：「友方总部受到**陆军**伤害时」（兵种词直接跟在"受到"后、不写"来自…的"）
    [/^(?:友方|我方)总部受到(?:来自\s*([\u4e00-\u9fa5]{1,6}?)\s*的\s*|([\u4e00-\u9fa5]{1,4}?)\s*)?伤害时[，,]?\s*/, 'hqDamaged'],
    // 「（一回合一次，）友方消灭敌方单位时」→ enemyKilled（**监听**：己方任意单位消灭了敌方单位）
    //   ⚠ 必须排在「单位被消灭时」**之前**，而且必须认得出来：不认的话这段前缀会被当成**动作**——
    //     "友方"被当成目标阵营、"消灭敌方单位"被当成 destroy →
    //     产出「部署：消灭一个**友方**单位」，**效果完全反了**（制作者 2026-09-22 报）。
    //   ⚠ 这类"前缀被当动作"比"前缀被跳过"更隐蔽：每一段都被消费掉了，
    //     "有东西被跳过"的闸门抓不到，只能靠把句式**补齐**。
    [/^(?:一回合一次[，,]?\s*)?友方(?:单位)?消灭(?:一个|1\s*个)?\s*敌方单位时[，,]?\s*/, 'enemyKilled'],
    // 「单位被消灭时」→ friendlyDeath
    //   ⚠ 卡面常加**冠词**「一个/一名/一只」（如「解体」："一个友方单位被消灭时，对敌方总部造成8点伤害"）。
    //     冠词不是数量词 —— 不吞掉的话，"一个友方单位被消灭时"会退化成"消灭一个友方单位"的即时指令，
    //     效果完全变味（实测：打出后要求选一个友方单位来消灭，而不是等友方单位死了再触发）。
    [/^(?:一个|一名|一只|某个)?\s*(?:友方)?单位(?:们)?被消灭时[，,]?\s*/, 'friendlyDeath'],
    // 「友方单位对战并存活后」→ friendlySurvived
    [/^友方单位对战(?:并|且)?存活(?:后|时)[，,]?\s*/, 'friendlySurvived'],
    // 「友方获得额外指挥点时」→ kreditsGained
    // 牌Q 卡包（2026-09-25）：补「友方**单位**额外获得指挥点时」「友方**每**额外获得指挥点时」
    [/^(?:一回合一次[，,]?\s*)?友方(?:单位)?(?:每)?(?:额外)?(?:获得|得到)?(?:一|1)?\s*(?:个|点)?\s*(?:额外)?指挥点时[，,]?\s*/, 'kreditsGained'],
    // ★ 2026-09-26：「友方额外抽一张牌时，…」（后勤营 av76/units/33）→ extraDraw。
    //   引擎**早就会派发**这个事件（engine.js 第 609 行：额外抽牌时 runTrigger({trigger:'extraDraw'})），
    //   但编译器一直没有对应卡面规则 → 整句退化成「未实现」。这是纯粹的规则缺失，不是引擎缺口。
    [/^(?:一回合一次[，,]?\s*)?友方(?:单位)?(?:额外|又|再)抽(?:一|1)?\s*张?牌(?:时|后)[，,]?\s*/, 'extraDraw'],
    [/^(?:一回合一次[，,]?\s*)?友方(?:单位)?额外抽牌(?:时|后)[，,]?\s*/, 'extraDraw'],
    // ★ 敌方侧同理：前缀以「敌方」开头 → compiler 的 /^敌方/ 判定会自动打上 watchOpponent，
    //   引擎侧的对手监听在 runTrigger 的对手循环里兑现。
    [/^(?:一回合一次[，,]?\s*)?敌方(?:单位)?(?:额外|又|再)抽(?:一|1)?\s*张?牌(?:时|后)[，,]?\s*/, 'extraDraw'],
    [/^(?:一回合一次[，,]?\s*)?敌方(?:单位)?(?:每)?(?:额外)?(?:获得|得到)?(?:一|1)?\s*(?:个|点)?\s*(?:额外)?指挥点时[，,]?\s*/, 'kreditsGained'],
    [/^(?:一个|一名|一只|某个)?\s*友方([^，,。]{0,12}?)单位被消灭时[，,]?\s*/, 'friendlyDeath'],
    // 「友方单位被X时」系列监听（同样允许冠词前缀）
    [/^(?:一个|一名|一只|某个)?\s*友方单位被压制时[，,]?\s*/, 'friendlyPinned'],
    // ★ 2026-10-02：抑制有自己的事件（friendlySuppressed），不再占位到 friendlyPinned
    [/^(?:一个|一名|一只|某个)?\s*友方单位被抑制时[，,]?\s*/, 'friendlySuppressed'],
    [/^(?:一个|一名|一只|某个)?\s*友方单位被攻击时[，,]?\s*/, 'friendlyAttacked'],
    [/^(?:一个|一名|一只|某个)?\s*友方单位被撤退时[，,]?\s*/, 'friendlyRetreated'],
    [/^友方单位被沉默时[，,]?\s*/, 'friendlySilenced'],
    [/^友方单位受到伤害(?:时|后)[，,]?\s*/, 'friendlyDamaged'],
    [/^友方单位被指定(?:为目标)?时[，,]?\s*/, 'targetedByOrder'],
    // 「（当）本单位/这张卡 成为指令的目标时」→ 自身语义的 targetedByOrder
    //   （区别于上一条：上一条监听"友方单位"，本条是**这张卡自己**被指向）
    [/^(?:当)?(?:本单位?|这张卡|该单位)?(?:成为|被指定为)指令(?:的)?目标(?:时|后)[，,]?\s*/, 'targetedByOrder'],
    // 「友方单位数量不小于N时」→ 单位部署监听 + 数量条件（友方单位数量只在部署时增加，
    //   故挂 unitDeployed，每次部署后检查 unitCount >= N —— 见下方 trigCondition 处理）
    [/^友方单位数量(?:不小于|不少于|大于等于|达到|达)\s*([一二两三四五六七八九十\d]+)\s*(?:个|名|辆|架|艘)?\s*(?:单位)?时[，,]?\s*/, 'unitDeployed'],
    /* ★ 洗切（2026-10-02 Alan 定义）：触发词，本身没有效果 —— 引擎在「洗入卡组 /
     *   洗切卡组」类动作（shuffleIn / shuffleInUntil / shuffleRandomSet / shuffleHandIntoDeck）
     *   执行成功后派发 shuffle 事件。「敌方洗切卡组时」由 /^敌方/ 判定自动打 watchOpponent。 */
    [/^(?:友方|敌方|对方|对手)?洗切(?:卡组|牌库|牌组)时[，,]?\s*/, 'shuffle'],
    [/^(?:友方|敌方|对方|对手)?(?:卡组|牌库|牌组)被洗切时[，,]?\s*/, 'shuffle'],
    // ★ 单位不因消灭离开战场（2026-10-02）：移除 / 撤退回手牌 / 撤退遇手牌满转移除 都派发 unitLeft
    [/^(?:友方|敌方)?单位不因消灭离开战场(?:时|后)[，,]?\s*/, 'unitLeft'],
    [/^(?:友方|敌方)?单位被移除时[，,]?\s*/, 'unitLeft'],
  ];

  // "其"指谁：监听类触发器里 = 事件主角；「受到伤害时」的事件主角是 victim 而不是 source
  const EVENT_REF = {
    unitDeployed: 'eventUnit', unitMobilized: 'eventUnit', friendlyDeath: 'eventUnit',
    orderPlayed: 'self',
    friendlyPinned: 'eventUnit', friendlyAttacked: 'eventUnit', friendlyRetreated: 'eventUnit',
    friendlySilenced: 'eventUnit', targetedByOrder: 'eventUnit', friendlyDamaged: 'eventVictim',
    friendlySuppressed: 'eventUnit', unitLeft: 'eventUnit',   // ★ 2026-10-02：抑制 / 离场（事件主角 = 离开的那个单位）
    orderPlayed: 'eventUnit',
    // 「本单位移至前线时 / 受到伤害后」→ 事件主角就是这张卡自己
    mobilize: 'eventUnit', damaged: 'eventUnit', attack: 'eventUnit', afterAttack: 'eventUnit',
  };

  /* 单句 → action[]；返回 {actions, consumed} */
  function parseSentence(s, ctx) {
    s = s.trim().replace(/。$/, '');
    if (!s) return null;
    const out = [];
    let m;

    // ── 手牌花费相关（"使一张手牌获得-1花费" 这类）──
    // ★ 限定词必须读出来（制作者 2026-09-22 报：「使友方**所有**手牌获得-1花费」
    //   被编成"选择一张手牌减费" —— "所有"整段丢了）。
    //   · 写了 所有 / 全部 / 每个 / 全体 → 该方**全部**手牌都改，不让玩家选；
    //   · 只写「一张」或没写数量词 → 玩家自己选一张（保持原有交互）。
    //   ⚠ 「任意」不算"所有"（它是"随便挑一张"= 玩家选）。
    //   ⚠ 阵营也要读：以前写死 side:'self'，「使敌方所有手牌获得-1花费」会错编成自己的手牌。
    const handAll = /(?:所有|全部|每个|全体)/.test(s);
    const handSide = /(?:敌方|对方|对手)/.test(s) ? 'enemy'
      : /(?:双方|两方)/.test(s) ? 'any' : 'self';
    const handFilter = () => {
      if (/指令/.test(s)) return { cardType: 'order' };
      if (/单位/.test(s)) return { cardType: 'unit' };
      return undefined;
    };
    // count 给足 = 引擎里"改到没有可改的为止"（setHandCost 是 `for(…){ if(done>=n) break }`）
    const handCost = (mode, value) => ([{
      op: 'setHandCost', mode: mode, side: handSide,
      count: handAll ? 99 : 1,
      choose: !handAll,
      ...(mode === 'set' ? { value: value } : { amount: value }),
      ...(handFilter() ? { filter: handFilter() } : {}),
      prompt: mode === 'set' ? '选择一张手牌改变花费' : '选择一张手牌减费',
    }]);
    // (a) 获得 -N 花费 / 费用
    if ((m = s.match(/使[^，,。]*?(?:手牌|指令|单位|牌)[^，,。]*?获得\s*(-?\d+)\s*(?:点)?(?:花费|费用)/))) {
      const n = num(m[1]);
      return { actions: handCost('reduce', n > 0 ? -n : n) };
    }
    // (b) 花费 / 费用 减为 / 变为 / 设为 N
    if ((m = s.match(/使[^，,。]*?(?:手牌|指令|单位|牌)[^，,。]*?(?:花费|费用)\s*(?:减为|变为|设为|改为)\s*(\d+)/))) {
      return { actions: handCost('set', num(m[1])) };
    }
    // (c) 花费 / 费用 减 N
    if ((m = s.match(/使[^，,。]*?(?:手牌|指令|单位|牌)[^，,。]*?(?:花费|费用)\s*(?:减|降低|减少)\s*(-?\d+)/))) {
      const n = num(m[1]);
      return { actions: handCost('reduce', n > 0 ? -n : n) };
    }
    // 抽N张牌
    if ((m = s.match(/^抽\s*([一二两三四五六七八九十\d]+)\s*张(牌)?\s*$/))) {
      out.push({ op: 'draw', count: num(m[1]), side: /敌方|对手/.test(s) ? 'enemy' : 'self' });
      return { actions: out };
    }
    // 对...造成N点伤害
    if ((m = s.match(/^对(一个|一名|随机)?\s*(敌方|友方)?\s*(单位|总部|所有单位|所有敌方单位)?\s*造成\s*([一二两三四五六七八九十\d]+)\s*点伤害/))) {
      const target = buildTarget(s, ctx);
      out.push({ op: target.kind === 'hq' ? 'damageHQ' : 'damage', target: target.sel, side: target.side, amount: num(m[4]) });
      return { actions: out, targets: target.declare };
    }
    // 造成N点伤害
    if ((m = s.match(/^造成\s*([一二两三四五六七八九十\d]+)\s*点伤害/))) {
      const target = buildTarget(s, ctx);
      out.push({ op: 'damage', target: target.sel, amount: num(m[1]) });
      return { actions: out, targets: target.declare };
    }
    // 消灭
    if ((m = s.match(/^消灭\s*(一个|一名|随机|所有)?\s*(敌方|友方)?\s*(单位|所有单位|受伤单位|陆军|空军|坦克|步兵|太空单位)?/))) {
      const target = buildTarget(s, ctx);
      // 非指向性的消灭没写阵营时默认敌方（指向性的仍由玩家任选）
      const side = (/所有|全部/.test(s) && target.side === 'any' && !/友方|我方|双方/.test(s)) ? 'enemy' : target.side;
      if (target.sel && typeof target.sel === 'object') target.sel.side = side;   // 选择器里的阵营也要同步
      out.push({ op: /所有/.test(s) ? 'destroyAll' : 'destroy', target: target.sel, side: side });
      return { actions: out, targets: target.declare };
    }
    // 获得 +N+N / 获得+2+2
    if ((m = s.match(/获得\s*\+?(-?\d+)\s*\+\s*(-?\d+)/))) {
      const target = buildTarget(s, ctx);
      out.push({ op: 'buff', target: target.sel, attack: parseInt(m[1], 10), defense: parseInt(m[2], 10) });
      return { actions: out, targets: target.declare };
    }
    // 使所有X获得...
    if ((m = s.match(/^使\s*(所有|场上所有|全部)?\s*(友方|敌方)?\s*([^获]+?)\s*获得\s*(.+)$/))) {
      const filter = typeFilter(m[3]);
      const rest = m[4];
      const acts = [];
      const bm = rest.match(/\+?(-?\d+)\s*\+\s*(-?\d+)/);
      if (bm) acts.push({ op: 'buffAll', target: { sel: 'all', side: m[2] === '敌方' ? 'enemy' : 'friendly', filter }, attack: parseInt(bm[1], 10), defense: parseInt(bm[2], 10) });
      const kw = keywordIn(rest);
      if (kw) acts.push({ op: 'grantAll', target: { sel: 'all', side: m[2] === '敌方' ? 'enemy' : 'friendly', filter }, keyword: kw });
      if (acts.length) return { actions: acts };
    }
    // 压制
    if ((m = s.match(/^压制\s*(所有|一个|一名|随机)?\s*(敌方|友方)?\s*(单位|空军|坦克|步兵)?/))) {
      const target = buildTarget(s, ctx);
      out.push({ op: 'pin', target: target.sel, side: target.side });
      return { actions: out, targets: target.declare };
    }
    // 治疗 / 恢复
    if ((m = s.match(/(恢复|治疗|修复)\s*([一二两三四五六七八九十\d]+)\s*点/))) {
      out.push({ op: 'heal', target: buildTarget(s, ctx).sel, amount: num(m[2]) });
      return { actions: out };
    }
    // 获得N个指挥点 / 指挥点槽
    if ((m = s.match(/获得\s*([一二两三四五六七八九十\d]+)\s*个指挥点槽/))) {
      out.push({ op: 'gainKreditSlot', amount: num(m[1]), side: /敌方/.test(s) ? 'enemy' : 'self' });
      return { actions: out };
    }
    if ((m = s.match(/获得\s*([一二两三四五六七八九十\d]+)\s*个指挥点/))) {
      out.push({ op: 'gainKredits', amount: num(m[1]), side: /敌方/.test(s) ? 'enemy' : 'self' });
      return { actions: out };
    }
    // 加入手牌
    if ((m = s.match(/将\s*(?:一|1)?\s*张?[“"「]?([^”"」]+?)[”"」]?\s*加入手牌/))) {
      out.push({ op: 'addCardToHand', name: m[1].trim(), count: 1 });
      return { actions: out };
    }
    // 洗入卡组
    if ((m = s.match(/将\s*(?:([一二两三四五六七八九十\d]+)\s*张)?[“"「]?([^”"」]+?)[”"」]?\s*洗入卡组/))) {
      out.push({ op: 'shuffleIn', name: m[2].trim(), count: num(m[1]) || 1 });
      return { actions: out };
    }
    // 开发
    if ((m = s.match(/^开发\s*(?:一|1)?\s*张?(.+)$/))) {
      out.push({ op: 'discover', filter: {}, prompt: '开发：' + m[1] });
      return { actions: out, uncertain: true };
    }
    return null;
  }

  function typeFilter(text) {
    const f = {};
    Object.keys(TYPES).forEach(k => {
      if (text.indexOf(k) >= 0 && TYPES[k]) {
        if (typeof TYPES[k] === 'string') f.unitType = f.unitType || TYPES[k];
        else f.unitType = TYPES[k];
      }
    });
    // ★ 「游击单位 / 老兵单位 / 星盟单位」这类**词条 + 单位**：走同一份词表（P.traitFilterOf）。
    //   ⚠ 必须要求后跟「单位/的/牌」，否则「使所有太空单位获得游击和+2+2」里的裸「游击」
    //     会被当成目标限定词（目标集合变空）。
    //   历史 bug：只查 UNIT_TYPES，其它词条一律当**卡名** —— `友方游击单位具有+1攻击力`
    //   被编成 filter:{name:'游击单位'}，永远匹配 0 个单位。
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    if (P && P.TRAIT_WORDS) {
      const words = Object.keys(P.TRAIT_WORDS).sort((a, b) => b.length - a.length);
      const t = String(text || '');
      for (const w of words) {
        const kv = P.traitFilterOf(w);
        if (!kv || kv.unitType) continue;                       // 兵种词已由上面的 TYPES 处理
        if (kv.keyword && new RegExp(w + '(?:单位|的)').test(t) && f.keyword == null) f.keyword = kv.keyword;
        else if (kv.setIn && new RegExp(w + '(?:单位|牌)').test(t) && f.setIn == null) f.setIn = kv.setIn;
      }
    }
    return f;
  }

  // 「友方<这段描述>单位被消灭时」里的描述 → 监听过滤条件（花费不小于N / 兵种 / 已受伤…）
  function descFilter(desc) {
    const f = {};
    const d = String(desc || '');
    if (!d) return f;
    const mx = d.match(/花费不大于\s*([一二两三四五六七八九十\d]+)/);
    const mn = d.match(/花费不小于\s*([一二两三四五六七八九十\d]+)/);
    if (mx) f.maxCost = num(mx[1]);
    if (mn) f.minCost = num(mn[1]);
    const tf = typeFilter(d);
    if (tf.unitType) f.unitType = tf.unitType;
    if (tf.keyword) f.keyword = tf.keyword;
    if (tf.setIn) f.setIn = tf.setIn;
    // 「友方<词条>单位被消灭时」捕获的是**词条本身**（"单位"已被吃掉）→ 直查词表
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    const kv = (P && !tf.unitType && !tf.keyword && !tf.setIn) ? P.traitFilterOf(String(d).replace(/的$/, '')) : null;
    if (kv) Object.assign(f, kv);
    if (/受伤|受到伤害/.test(d)) f.damaged = true;
    return f;
  }

  function keywordIn(text) {
    const map = { '闪击': 'blitz', '固守': 'guard', '守护': 'guard', '烟幕': 'smokescreen', '伏击': 'ambush', '动员': 'mobilize', '狂怒': 'fury', '奋战': 'valor', '冲击': 'impact', '游击': 'guerrilla', '流亡': 'exile', '亡计': 'deathrattle', '老兵': 'veteran', '强磁护盾': 'shield', '硬铝弹': 'tsekep', '空投': 'airdrop' };
    for (const k in map) if (text.indexOf(k) >= 0) return map[k];
    return null;
  }

  function buildTarget(sentence, ctx) {
    const declare = [];
    const enemy = /敌方|对方|对手/.test(sentence);
    const friendly = /友方|我方/.test(sentence);
    const side = enemy ? 'enemy' : friendly ? 'friendly' : 'any';
    const all = /所有|全部|场上所有/.test(sentence);
    const random = /随机/.test(sentence);
    const isHQ = /总部/.test(sentence);
    const filter = typeFilter(sentence);
    // 阵线限定：前线 / 支援阵线（"消灭前线所有单位" 必须带上这个条件，否则会变成"消灭所有单位"）
    const zone = /前线/.test(sentence) ? 'frontline' : (/支援(阵线|线)/.test(sentence) ? 'support' : null);
    let sel;
    if (isHQ) {
      return { kind: 'hq', sel: { sel: 'all', side: side === 'any' ? 'enemy' : side }, side: side === 'any' ? 'enemy' : side, declare: [] };
    }
    if (all) sel = Object.assign({ sel: 'all', side, filter }, zone ? { zone } : {});
    else if (random) sel = Object.assign({ sel: 'random', side, filter, count: 1 }, zone ? { zone } : {});
    else if (side === 'any') {
      const id = 't' + (declare.length + 1);
      declare.push(Object.assign({ id, side: 'any', kind: 'unit', prompt: '选择一个单位' }, zone ? { zone } : {}));
      sel = id;
    } else {
      const id = 't' + (declare.length + 1);
      declare.push(Object.assign({ id, side, kind: 'unit', prompt: '选择一个' + (side === 'enemy' ? '敌方' : '友方') + '单位' }, zone ? { zone } : {}));
      sel = id;
    }
    return { sel, side, filter, declare, kind: 'unit' };
  }

  /* --------------------------------------------------------------- 主入口 */
  // 先用专用正则（更精确），不行再交给"原语组合解析器"（覆盖长尾写法）
  function parsePrimitives(sentence, opts) {
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    if (!P) return null;
    /* 「等同于v1的费用」这类**变量引用**：P.parse 里先把那段换成哨兵数字，
     *   这里在返回前把哨兵回填成真正的 {cardVar, stat}（Alan 09-28 变量系统）。 */
    try { return P.takeVarRefs(P.parse(sentence, Object.assign({ cardName: CUR_CARD_NAME }, opts || {}))); }
    catch (e) {
      const reason = '原语解析器异常，已阻止旧规则猜测：' + (e && e.message ? e.message : String(e));
      return P.unparsed ? P.unparsed(sentence, reason) : { actions: [], uncertain: true, unparsed: { text: String(sentence || ''), reason: reason } };
    }
  }
  // 旧专用正则里有不少前缀匹配。只让它兜底没有复合/条件边界的短句；
  // 含多个分句或条件的正文若主解析器没接住，必须明确失败，不能碰巧生成半截效果。
  function hasCompoundSyntax(text) {
    const s = String(text || '').replace(/[。！？.]+$/, '').trim();
    return /[，,；;]|(?:并且|而且|然后|同时|以及|否则|抉择|或者|如果|假如|只要|若|并|且|和|或)|(?:^|[，,])当/.test(s);
  }
  // 动作规则返回的 actText 是实际匹配片段。若其后仍有连接词和另一个动作，
  // 说明解析器只吃了前半句；不要求每个词都由同一条动作正则消耗，但不允许遗留动作。
  function hasUnconsumedActionTail(text, actText) {
    if (!text || !actText) return false;
    const source = String(text).replace(/[“「"][\s\S]*?[”」"]/g, '“”');
    const at = source.indexOf(actText);
    if (at < 0) return false;
    const tail = source.slice(at + actText.length).trim();
    if (/^(?:如果|若|假如|只要|否则|当)/.test(tail)) return true;
    return /(?:并且|而且|同时|以及|然后|并|且|或者|或|[，,；;])\s*(?:额外|再次|抽|获得|得到|消灭|摧毁|造成|施加|压制|抑制|移动|移至|移到|召唤|开发|弃|洗入|恢复|治疗|复制|失去|选择|加入|使用|攻击|结束|翻开|揭示|使|令|对)/.test(tail);
  }
  // 反制卡的触发条件：写在卡面开头的「当一个…时」「一个单位的…触发前」
  //   on 事件名 / owner 谁的 event / interrupt 打断型（引擎在事件发生前同步消费）
  const COUNTER_CLAUSES = [
    { re: /^当一个?友方(?:目标|单位)受到伤害大于[^，,。]*?时[，,]?\s*(?:将其随机转移[^，,。]*)?[，,]?\s*/, on: 'friendlyDamaged', owner: 'self', interrupt: 'fatalDamage' },
    { re: /^当一个?友方(?:目标|单位)受到(?:伤害|攻击)时[，,]?\s*/, on: 'friendlyDamaged', owner: 'self' },
    { re: /^一个?单位的?亡计触发前[，,]?\s*(?:将其抑制|抑制它)?[，,]?\s*/, on: 'friendlyDeath', owner: 'any', interrupt: 'deathrattle' },
    // 卡面「一个友方单位被消灭时…」= 监听自己这边单位阵亡（解体就是这张卡）。
    //   ⚠ 少了这条，前缀会被当成**目标**解析（编译出"选一个友方单位"+destroy，语义完全不同）。
    { re: /^一个?友方(?:目标|单位)(?:被)?(?:消灭|阵亡|死亡)时[，,]?\s*/, on: 'friendlyDeath', owner: 'self' },
    // 「一个单位的部署效果触发时」= **打断型**：单位一进场就在其部署效果结算**之前**拦截
    //   （"将其反制" = counter 掉那个部署效果），然后消灭它。若映射成 on:'unitDeployed'
    //   会在部署效果（抽牌/加buff）结算完**之后**才触发，时机反了。
    { re: /^一个?单位的?部署(?:效果)?触发时[，,]?\s*(?:将其反制|反制它|将其抑制)?[，,]?\s*/, on: 'unitDeployed', owner: 'foe', interrupt: 'unitDeployed' },
    { re: /^敌方(?:单位)?部署(?:效果)?(?:触发)?时[，,]?\s*/, on: 'unitDeployed', owner: 'foe', interrupt: 'unitDeployed' },
    { re: /^敌方部署\s*[\u4e00-\u9fa5]{0,4}?时[，,]?\s*/, on: 'unitDeployed', owner: 'foe' },
    { re: /^敌方(?:单位)?被消灭时[，,]?\s*/, on: 'friendlyDeath', owner: 'foe' },
    { re: /^敌方使用指令(?:后|时)[，,]?\s*/, on: 'orderPlayed', owner: 'foe' },
    { re: /^敌方(?:单位)?(?:攻击|行动)(?:后|时)[，,]?\s*/, on: 'unitActed', owner: 'foe' },
    // ★ 2026-09-22：「（一回合一次，）友方消灭敌方单位时」→ 监听**己方的击杀**。
    //   反制卡走 COUNTER_CLAUSES（不走 TRIGGERS），所以两条路径都要有这条，
    //   否则反制卡写这个前缀会认不出触发条件（order/单位卡则由 TRIGGERS 认）。
    { re: /^(?:一回合一次[，,]?\s*)?友方(?:单位)?消灭(?:一个|1\s*个)?\s*敌方单位时[，,]?\s*/, on: 'enemyKilled', owner: 'self' },
    // ★ 牌Q（制作者口径：坐标是反制卡）：「敌方指令指向友方单位时，将其反制」→ 指令型打断
    { re: /^敌方指令指向友方单位时[，,]?\s*/, on: 'orderPlayed', owner: 'foe', interrupt: 'order' },
  ];

  // ★ 监听型触发（适合"打出后挂起等待事件"的指令陷阱）：
  //   order 卡卡面写「<监听句>，<效果>」时 → 编译成 counter 埋伏（复用引擎的指令式反制机制）。
  //   不含 deploy/mobilize/damaged/death（这些是"自己身上"的事件，语义不同）。
  const LISTEN_TRIGGERS = ['friendlyDeath', 'friendlyAttacked', 'friendlyPinned', 'friendlyRetreated',
    'friendlySilenced', 'friendlySurvived', 'afterKill', 'afterAttack', 'unitActed', 'orderPlayed',
    'kreditsGained', 'unitDeployed', 'unitAttacked', 'hqDamaged', 'enemyKilled',
    // 牌Q 卡包（2026-09-25）新增的监听：受伤害 / 冲击消耗 / 攻击敌方总部 / 压制解除 / 抽牌
    'friendlyDamaged', 'impactUsed', 'afterAttackHQ', 'unpinned', 'drawn',
    // 2026-10-02：抑制 / 洗切 / 不因消灭离场（指令卡写监听句 → 编译成指令式反制）
    'friendlySuppressed', 'shuffle', 'unitLeft'];

  let CUR_CARD_NAME = null;      // ★ 牌Q：给原语层传"当前卡名"（招募令要读自身的打出次数）
  C.compile = function (text, card) {
    CUR_CARD_NAME = (card && (card.name || card.id)) || null;
    let raw = String(text || '').trim();
    if (!raw) return { effects: [], warnings: [] };
    const isCounter = !!(card && card.cardType === 'counter');
    const warnings = [];
    const effects = [];
    const defTrigger = isCounter ? 'counter' : (card && card.cardType === 'order') ? 'order' : 'deploy';
    // ★ 起始牌机制：「在第一回合抽取，抽取：使用」→ 卡面字段（开局自动抽到 + 自动使用）。
    //   这是**卡牌 meta 机制**，不是对战效果原语 —— 从文本里抠掉，避免落成"未实现" log。
    /* ★ 牌Q 卡包（2026-09-25）：「一回合一次，」是**效果级**限定（引擎 effects.js 读 oncePerTurn），
     *   以前只有少数触发词条自带这个可选项 → 其它写法整句落到"触发前缀未能识别"。
     *   这里在最前统一剥掉，并把标出来的效果打上 oncePerTurn。 */
    let oncePerTurnAll = false;
    if (/^一回合一次\s*[，,]?\s*/.test(raw)) {
      raw = raw.replace(/^一回合一次\s*[，,]?\s*/, '');
      oncePerTurnAll = true;
    }
    const startFields = {};
    {
      const sm = raw.match(/在第一回合抽取[，,]?\s*抽取[：:]\s*使用[。]?/);
      if (sm) {
        startFields.startInHand = true;
        startFields.autoUse = true;
        raw = raw.replace(sm[0], '').trim();
      }
      /* ★★ 「在第N回合抽取」= **卡牌 meta 机制**（不是对战效果原语）：
       *   这张牌会在**第 N 回合**被抽到手上（引擎在 beginTurn 兑现，见 engine.js）。
       *   ⚠ 必须从文本里抠掉：留着会被当成效果正文 → 产出"未实现"假告警。
       *   （原来只硬编码了"在第一回合抽取，抽取：使用"一种，这里做成**任意回合**。） */
      const dm = raw.match(/在第\s*([一二两三四五六七八九十\d]+)\s*回合(?:开始)?时?抽取/);
      if (dm) {
        const turnN = num(dm[1]) || 1;
        startFields.drawOnTurn = turnN;
        raw = raw.replace(dm[0], '').trim();
        raw = raw.replace(/^[，,。]\s*/, '').trim();
      }
    }
    // ★ 词条定义/说明卡（cost=null 的参照卡）：卡面是"某个对战词条的释义"或占位符模板，
    //   不是正常对局卡（cru/mra 是 x/y 占位参照，gue/lcru/sc/sms/tsekep 是词条释义）。
    //   它们无需产出效果 —— 否则会把"单位可以回到上一阵线、返回手牌"这类释义当成效果正文，
    //   产出"未实现"假告警，污染覆盖率。
    //   ⚠ 「进攻」(av76/command/-17) 也是 cost=null，但它是用户要求保留手写覆盖的规则速记卡，
    //      不在跳过名单里（它的 text 是"2费打5|…"规则表，正常解析本就不会报未实现）。
    const REFERENCE_CARDS = ['cru', 'mra', 'gue', 'lcru', 'sc', 'sms', 'tsekep'];
    if (card && REFERENCE_CARDS.indexOf(card.id) >= 0) return { effects: [], warnings: [] };
    // 反制卡：触发条件可能跨多行，先拼成一行抠掉它，剩下的才是效果正文
    const counterMeta = {};
    let src = raw;
    if (isCounter) {
      const joined = raw.replace(/\r/g, '').split('\n').map(function (x) { return x.trim(); }).filter(Boolean).join('');
      for (const cl of COUNTER_CLAUSES) {
        const cm = joined.match(cl.re);
        if (!cm) continue;
        counterMeta.on = cl.on;
        counterMeta.owner = cl.owner;
        if (cl.interrupt) counterMeta.interrupt = cl.interrupt;
        src = joined.slice(cm[0].length);
        break;
      }
      if (!counterMeta.on) src = joined;      // 没写到触发条件 → 默认「对手的任何行动」
    }
    // 按行拆句
    // ★★ 中文分号 `；` 与句号一样**也要拆成独立行**（文档 §2：分号后仍受前面控制，
    //    但**必须被处理到**）。不拆的话，分号后的半句进了原语层会被"匹配到第一个动作就返回"
    //    的逻辑整段吞掉 —— 这就是"分号后面的句子全被忽略"。
    //    ⚠ 拆在**这里**（而不是原语层入口）是因为：compiler 对每行做完整的
    //      触发识别 + 条件剥离 + 元数据推断，拆开的段各自走完整流程，条件包装才不会丢。
    //      实测在原语层入口拆会把第一段的条件包装（conditional）整个搞丢。
    //   ★ ASCII 句号也当句子分隔（卡面 OCR 常把「。」写成「.」：「…+2+2.随机对一个单位造成2点伤害」），
    //     只在**后随汉字**时切 —— 否则会把「A.A.A.A.」这类拉丁缩写/卡名切碎。
    let lines = src.split(/\n|(?<=。)|(?<=；)|(?<=;)|(?<=[\u4e00-\u9fa5\d])\.(?=[\u4e00-\u9fa5])/).map(s => s.trim()).filter(Boolean);
    // ★ 纯数值/费用行（「12K」「12K9行动花费」「9（6） 5」）在这里就滤掉：
    //   放到后面的循环里就晚了 —— 合并行的步骤会把它们并进上一行（卡B 曾因此整张卡变"未实现"）。
    lines = lines.filter(function (l) {
      return !/^\d+\s*K?(?:\s*\d+\s*行动花费)?$/.test(l) && !/^\d+\s*[（(]\s*\d+\s*[）)]\s*\d+$/.test(l);
    });

    /* ---------- 跨行归并（原语组合器的"分句"前置步骤） ----------
     * 卡面换行经常把一个完整语义切成两行，单看任一行都不是完整句子：
     *   ① 条件句： 「如果剩余指挥点不小于4，」 / 「随机消灭一个敌方单位」
     *              —— 条件限定的是**下一行**的动作，必须拼起来才是 conditional。
     *   ② 触发头： 「部署：」 / 「对一个敌方单位造成2点伤害」
     *              —— "部署："单独成行，后面才是正文（TRIGGERS 里那些"部署："带正文的写法）。
     *   ③ 续接：   「抽一张牌。若敌方手中具有明牌，」 / 「弃掉一张牌」
     *   ④ 引号承载：「使友方总部获得：」 / 「“友方单位部署时，…”」 —— 由 hqCarrier 处理，这里不动。
     *
     * 归并规则（保守，只拼"明显没说完"的行）：
     *   - 行尾是「，」「：」「、」→ 与下一行拼接
     *   - 行是纯条件半句（如果/若…且以逗号结尾）→ 与下一行拼接
     *   - 行只以「部署」「亡计」等触发词 + 冒号结尾 → 与下一行拼接
     */
    {
      // 触发词（后面可能跟「：」也可能跟「，」，例如「部署，弃掉所有手牌…」）
      const TRIG_WORD = '(?:部署|亡计|遗言|打捞|维修|老兵|流放|流亡|情报|抉择|压制|抑制|收缴|明牌)';
      const TRIG_HEADS = new RegExp('^' + TRIG_WORD + '\\s*[：:]\\s*$');
      const TRIG_HEAD_COMMA = new RegExp('^' + TRIG_WORD + '\\s*[，,]\\s*$');
      const TRIG_HEAD_INLINE = new RegExp('^' + TRIG_WORD + '\\s*[，,]');
      const merged = [];
      for (let i = 0; i < lines.length; i++) {
        let cur = lines[i];
        // 纯词条行不参与归并（「重甲1」+「对战步兵伤害翻倍」是两条独立语义，
        //   拼起来会得到「重甲1，对战步兵伤害翻倍」这种既不是词条也不是效果的怪东西）
        if (isKeywordOnlyLine(cur)) { merged.push(cur); continue; }
        let guard = 0;
        // 反复向后吞并，直到这一行"看起来说完了"（防 2 行以上的长链）
        while (i + 1 < lines.length && guard++ < 4) {
          const nxt = lines[i + 1];
          // 以句号/叹号/问号收尾 = 完整句子，绝不与下行合并
          //   ★ 例外：下一行是"时长尾巴"（直至…回合结束）时，句号是误标，
          //     「和+1攻击力。直至本回合结束」实为一句，仍要并。
          const nxtDur = /^直至|^直到/.test(nxt) && /回合结束|回合开始/.test(nxt);
          //   ★ 牌Q 卡包（2026-09-25）：**引号没闭合**时，句号也不该断开 ——
          //     卡面把句号写在引文里（「…获得“友方回合开始时，具有 -1 行动花费。”」），
          //     按句号拆会把收尾的「”」拆成独立行（那一行永远解析不了）。
          const _q = (t, a) => t.split(a).length - 1;
          const quoteOpenNow = (_q(cur, '“') + _q(cur, '「') + _q(cur, '"')) > (_q(cur, '”') + _q(cur, '」'));
          if (/[。！？]\s*$/.test(cur) && !nxtDur && !quoteOpenNow) break;
          // 下一行是纯词条行 → 不吞（避免把词条并进效果正文）
          if (isKeywordOnlyLine(nxt)) break;
          // ★ 下一行开启**新的触发段**（「亡计：…」「部署：…」「动员：…」）→ 绝不吞。
          //   否则「部署：…压制所有单位」+「亡计：…」会被 isHalfPhrase（行尾"单位"）
          //   粘成一行且**中间不补分隔符** → 两个触发头同时失效、正文错位（S-1 的现象）。
          if (new RegExp('^' + TRIG_WORD + '\\s*[：:]').test(nxt)) break;
          // ★ 关键：**「能独立解析的动作…，」+「如果…」不合并**。
          //   卡面把「动作，」写在上一行、条件限定写在下一行时（如
          //   「开发一张协约国空军，/ 如果剩余指挥点不小于4，/ 随机消灭一个敌方单位」），
          //   那个行尾逗号只是**排版换行**，不是"话没说完"。
          //   若在这里把它吞并，三个语义会被粘成一句长句，原语规则全部匹配不上 →
          //   整体退化成 `filter:{}` 的兜底 discover（"无法自动解析"的直接原因）。
          //
          //   ⚠ 但要和「侦察」那种情况区分开：`抽一张牌。若敌方手中具有明牌，/ 具有减2花费`
          //     —— 那一行的「若…」是**附着在上一句之后的从句**，它的动作在下一行，必须合并。
          //   判据：**本行（去掉尾部逗号）能不能被原语解析器独立解析成一个完整动作**。
          //     能 → 尾逗号只是换行，断开（战术空袭）；
          //     不能 → 本行没说完，继续吞并（侦察）。
          if (/[，,]\s*$/.test(cur) && /^(?:如果|若|假如|当)/.test(nxt)) {
            const P1 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
            const probe = P1 && P1.parse ? P1.parse(cur.replace(/[，,]\s*$/, ''), {}) : null;
            if (probe && probe.actions && probe.actions.length) break;
          }
          const endsOpen = /[，,、：:]\s*$/.test(cur);                    // 以逗号/冒号结尾
          const isTrigHead = TRIG_HEADS.test(cur);                       // 纯触发头（带冒号）
          const isTrigHeadComma = TRIG_HEAD_COMMA.test(cur)              // 触发头 + 逗号（「部署，」）
            || (TRIG_HEAD_INLINE.test(cur) && /[，,]\s*$/.test(cur));    // 「部署，xxx，」（触发词在行首且行尾逗号）
          const isCondHalf = /^(如果|若|假如)/.test(cur) && /[，,]\s*$/.test(cur);  // 纯条件半句
          // 条件半句的"无逗号变体"：「如果…帝安单位\n，获得…」（逗号顶格在下一行）
          //   或「如果场上存在其它X\n则具有守护」（"则"换行）。这类"如果/若"开头、句末无句号的
          //   行，下一行若以「，」「则」开头，也当条件半句吞。
          //   也覆盖「友方回合开始时，如果…单位\n，获得…」这种"触发前缀 + 条件半句"的复合。
          const isCondHalfBare = /(?:如果|若|假如)/.test(cur) && !/[。！？]\s*$/.test(cur);
          // ★ 行尾挂着**无正文的条件子句**：「压制一个敌方单位，如果其已被压制」+「随机消灭…」
          //   （抗敌）。这一行不以逗号结尾，所以上面 endsOpen/isHalfPhrase 都不认 →
          //   旧逻辑会把条件子句和下一行的 then 正文拆成两行，条件被孤零零丢掉。
          //   判据：本行含「，如果/，若…」，且**条件子句自己解析不出动作**（说明它在等下一行给正文）。
          const condTailM = cur.match(/[，,](?:如果|若|假如|只要|当)([^，,。]*)$/);
          const isCondTailOpen = (() => {
            if (!condTailM) return false;
            // ⚠ 判据必须是"这段能不能被认成**条件**"（buildCondition），
            //   不能是"能不能解析出动作" —— 「其已被压制」会被动词规则 /压制/ 命成
            //   pin 动作，于是误判成"条件子句自成一句"而不吞下一行。
            const P0 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
            if (!P0 || !P0.buildCondition) return false;
            return !!P0.buildCondition(condTailM[1]);
          })();
          // 下一行以**真续接词**开头 → 本行没说完（「…减1行动花费」/「和+1攻击力」）
          //   注意：只认虚词连词与代词承载（和/并/且/然后/再/同时/直至/直到/否则），
          //   不认「对/将/给/受到」这类实词 —— 它们是独立句子的正常开头，误并会毁掉语义。
          const nxtContinues = /^(和|并|且|然后|再|同时|直至|直到|否则|或|使其|将其|其)/.test(nxt);
          // 下一行以「，」或「则」开头 → 本行是没说完的条件/半句（「…帝安单位\n，获得…」「…巡地舰\n则具有守护」）
          const nxtStartsOpen = /^[，,]/.test(nxt) || /^则/.test(nxt);
          // 本行结尾无标点、但明显是"半句"（以这些动词/限定收尾）
          const isHalfPhrase = /(为本|为其|为|此前|此后|获得|具有|使|将|对|失去|移除|抽|弃|召唤|加入|造成|受到|随机|所有|全部|等同于|至多|至少|花费|单位|否则|和|与|及|、|获|得|并|是|以|即|消灭|摧毁|攻击|压制|抑制)$/.test(cur);
          // ★ 行尾停在**数量词**上（「亡计：随机消灭一个」+「攻击力不大于3的敌方单位」）：
          //   目标还没写完，必须并下一行。卡面换行常常正好断在数量词后面
          //   （第446侦察团就是这样断的 —— 不并的话后半句会被判成"未能自动解析"，
          //     前半句退化成"随机消灭一个敌方单位"：**筛选条件静默丢失**）。
          //   ⚠ 只在**下一行是名词性继续**时才并：下一行以动作词开头说明那是"另起一个动作"，
          //     交给下面的 isHalfPhrase 分支处理（它会补逗号），否则两个动作会被粘成一句。
          // ★ 行尾停在**比较词**上（卡面换行断在「如果剩余指挥点不」+「小于3，…」）→ 必须并下一行，
          //   否则这个半句条件既认不出来、又会因为"未知条件→显式失败"而报未解析。
          const isCmpTail = /(?:不|大于|小于|等于|不少于|不大于|不超过|超过|多于|高于|低于|不足|至少|至多|达到|达)$/.test(cur);
          const isQuantTail = (/(?:[一二两三四五六七八九十\d]+)?\s*(?:个|张|名|辆|架|艘|支|枚|位|种)$/.test(cur)
            || /(?:所有|全部|全体|任意数量|若干|每个)(?:敌方|友方|我方|对方)?$/.test(cur))
            && !/^(?:对|使|令|将|把|消灭|摧毁|抽|弃|随机|造成|压制|抑制|召唤|洗入|加入|复制|开发|恢复|回复|移动|撤退|移除|结束|额外|获得|得到|失去|选择|弃掉)/.test(nxt);
          // ★ 本行是「每有…」**数量前缀**（整行只有数量描述、没有动作主体）：
          //   「每有一张"星盟通用大驱"」+「对战太空单位时具有+1攻击力」
          //   必须拼成一句，否则前缀被孤立丢掉、主体按固定值解析
          //   → 「每有」被**静默忽略**（卫星联盟通用驱逐舰）。
          const isPerPrefix = /^(?:场上)?每有[^，,。；:]{1,16}$/.test(cur);
          //   ★ 牌Q：「…具有+1」+「花费」/「将其+1」+「行动花费」这类**单位词换行**必须并回去
          const nxtIsUnitWord = /^(?:行动花费|花费|攻击力|防御力|伤害|点数|护甲)(?:[，,]|$)/.test(nxt.trim());
          //   ★ 「…为本回合」+「第一个部署的单位，则…」→ 下一行以"第N"开头也算没说完
          const nxtIsOrdinal = /^第[一二两三四五六七八九十\d]/.test(nxt.trim()) && /(?:回合|为|是|算)$/.test(cur.trim());
          /* ★★ 牌Q 卡包（2026-09-25）：三类"明显没说完"的行尾，之前没认 → 短语被拆成两半、
           *   两半都解析不了（"未能自动解析"的成片主因）。 */
          //   ① 引号没闭合（「使一个友方单位获得“友方回合结束时」+「将其返回手牌…”）：
          //      开引号数 > 闭引号数 = 引文还没完，必须并下一行。
          const cntQ = (t, a) => t.split(a).length - 1;
          const isQuoteOpen = (cntQ(cur, '“') + cntQ(cur, '「') + cntQ(cur, '"')) >
            (cntQ(cur, '”') + cntQ(cur, '」'));
          //   ② 行尾停在**数字**上（「使敌方随机弃掉一张花费不大于 5」+「的牌」、
          //      「随机对一个友方目标造成 2 点」+「伤害」、「攻击力和为 6 的」+「BSUC战斗机」）
          //   ⚠ 必须收紧：只有"数字紧跟比较/计量词"才算被截断。像「…减1行动花费和+2+2」
          //     这种**以身材收尾的完整句**绝不能并下一行（essay-a 回归就是这么来的）。
          const isNumTail = /(?:不大于|不小于|不少于|不超过|大于|小于|等于|为|是|造成|受到|至多|至少|最多|花费|攻击力|防御力|具有|获得|设为|变成|改为)\s*[0-9０-９一二两三四五六七八九十]+\s*$/.test(cur)
            && !/[+＋]\s*[0-9]+\s*$/.test(cur);
          //   ②′ 行尾停在「N 点」（「…造成 2 点」+「伤害」）
          const isPointTail = /[0-9０-９一二两三四五六七八九十]\s*点\s*$/.test(cur) && /造成|受到|损失|伤害|打出/.test(cur);
          //   ③ 行尾停在「的」上（定语没写完：「…攻击力和为 6 的」）
          const isDeTail = /的\s*$/.test(cur);
          const isCountTail = /(?:数量|牌数|张数|个数|之和)\s*$/.test(cur);
          // ★ 下一行是「每消灭一个…」**计数补充段**（对上一行「消灭所有单位」的每单位结算）：
          //   分号拆行把它分成两行后，第二段会被通用解析吃成"再消灭一个单位 + 得槽"
          //   （凭空多消灭一个 + 弹选靶窗，制作者 2026-09-22 报「自动解析坏了」的就是它）。
          //   必须并回上一行，交给原语 0.049 的 forEach 计数规则。
          const nxtPerKill = /^每消灭(?:一个|1\s*个)/.test(nxt);
          // ★ 下一行是"时长尾巴"（直至/直到…回合结束/开始）→ 必须并进上一句，
          //   即使上一句以句号收尾（「和+1攻击力。直至本回合结束」的句号是误标，
          //   真正的语义是"和+1攻击力，直至本回合结束"，duration 由整句规则统一处理）。
          const nxtDurationTail = /^直至|^直到/.test(nxt) && /回合结束|回合开始/.test(nxt);
          // ★ 2026-09-26：下一行**以触发前缀开头**（「友方额外抽一张牌时，…」）→ 那是一条**新效果**，
          //   绝不能再并进上一行：并进去以后触发词埋在句子中间，只能当普通文字 → 整卡"未实现"
          //   （后勤营 av76/units/33 的第二个子句就是这么被吞掉的）。
          const nxtIsTrigger = (TRIGGERS || []).some(r => r[0].test(nxt.trim()));
          // ★ 下一行是**相对数值条件**（「若其不小于4，则额外获得一个指挥点槽」）→ 必须独立成句：
          //   ACTIONS 里那条 lastTurnKilledCompare 规则锚定句首「若其/若该数量/若此数」，
          //   并进上一行后「若其」不在句首 → 条件被**静默丢掉**（后勤第312团）。
          const nxtIsRelCond = /^(?:若其|若该数量|若此数|若该数值)/.test(nxt.trim());
          // ★ 「抉择：A」+「或者B」→ 必须合并，否则 choose 分支拿不到"或者"（抉择拆不开）。
          const isTrigWithBody = new RegExp('^' + TRIG_WORD + '\\s*[：:]').test(cur);
          if (!endsOpen && !isTrigHead && !isTrigHeadComma && !isCondHalf && !isHalfPhrase && !isQuantTail && !isCmpTail && !nxtDurationTail
              && !nxtIsTrigger && !nxtIsRelCond
              && !isCondTailOpen && !isPerPrefix && !nxtPerKill && !isQuoteOpen && !isNumTail && !isDeTail && !isPointTail && !nxtIsUnitWord && !isCountTail && !nxtIsOrdinal
              && !(isCondHalfBare && nxtStartsOpen) && !(isTrigWithBody && nxtContinues)) break;
          // 「使友方总部获得：」/「使友方总部获得」（冒号可省）要保留独立行给 hqCarrier 用，别吞掉
          if (/^[使是]?\s*(?:友方|我方|敌方|对方|双方)?\s*总部\s*(?:获得|具有)\s*[：:]?\s*$/.test(cur)) break;
          if (isTrigHead) {
            // 「部署：」+ 正文 → 保留冒号，拼成「部署：正文」（TRIGGERS 依赖这个冒号）
            cur = cur + nxt;
          } else if (isTrigHeadComma) {
            // 「部署，」+ 正文 → 开头逗号归一成冒号（TRIGGERS 认「部署：」），
            //   结尾逗号是**动作分隔符**，保留，拼上下一行正文。
            cur = cur.replace(/^(部署|亡计|遗言|打捞|维修|老兵|流放|流亡|情报|抉择|压制|抑制|收缴|明牌)\s*[，,]/, '$1：') + nxt;
          } else if (isPerPrefix) {
            // 「每有…」数量前缀 + 下一行主体 → 补逗号拼接，交给原语的「每有」规则
            cur = cur + '，' + nxt;
          } else if (nxtPerKill) {
            // 「消灭所有单位」+「每消灭一个，额外获得…」→ 补分号拼接，交给原语 0.049 的
            // forEach 计数规则（正则里「；」分隔可选，逗号也认）
            cur = cur + '，' + nxt;
          } else if (endsOpen) {
            cur = cur.replace(/[，,、：:]\s*$/, '') + '，' + nxt;
          } else if (nxtDurationTail) {
            // 时长尾巴：把 cur 末尾的句号换成逗号，再拼「直至…结束」
            cur = cur.replace(/[。！？]\s*$/, '，') + nxt;
          } else if (isCondHalfBare && nxtStartsOpen) {
            // 「如果…单位\n，获得…」/「如果…巡地舰\n则…」：补逗号拼接
            cur = cur.replace(/\s*$/, '，') + nxt.replace(/^[，,]\s*/, '');
          } else if (isCondTailOpen) {
            // 行尾的裸条件子句（「…，如果其已被压制」）+ 下一行正文 → 补逗号拼接，
            //   交给原语的中段条件分支编译成 conditional。
            cur = cur.replace(/\s*$/, '，') + nxt.replace(/^[，,]\s*/, '');
          } else if (nxtContinues || isHalfPhrase || isQuantTail || isCmpTail) {
            // ★ 「…<半句收尾>」+「<实词开头的新动作>」两行之间**必须补一个逗号**。
            //   直接拼接会得到「抉择：开发一张协约国单位对所有目标造成1点伤害」这种
            //   两个动作粘成一句的怪东西 —— 前一个动作被整个吃掉
            //   （奋起反抗的「开发一张协约国单位」曾因此消失，只剩后半句的伤害）。
            //   ★ 2026-09-24 补「获得|得到|失去」：卡面常在上一行写完"弃/抽/消灭X"、下一行写
            //     「获得等同于其防御力的指挥点」。缺了这几个词 → 两行无缝粘成
            //     「选择并弃一张单位获得等同于其防御力的指挥点」→ 只有后半句被规则命中，
            //     前半句的 discard **静默消失**（且 warnings 为空，看着像"解析成功"）。
            const needSep = isHalfPhrase && !nxtContinues &&
              /^(?:对|使|令|将|把|消灭|摧毁|抽|弃|随机|造成|压制|抑制|召唤|洗入|加入|复制|开发|恢复|回复|移动|撤退|移除|结束|额外|获得|得到|失去)/.test(nxt);
            cur = cur + (needSep ? '，' : '') + nxt;
          } else if (isQuoteOpen || isNumTail || isDeTail || isPointTail || nxtIsUnitWord || isCountTail || nxtIsOrdinal) {
            /* ★ 牌Q 卡包（2026-09-25）：这三类是"短语被换行截断"，直接拼接（不加分隔符）——
             *   卡面上的换行就断在词中间（「花费不大于 5」/「的牌」、「…数量的」/「防御力」、
             *   引号里的「本单位被」/「指向或攻击时…」）。 */
            cur = cur + nxt;
          } else {
            break;
          }
          i++;
        }
        merged.push(cur);
      }
      lines = merged;
    }
    let current = { trigger: defTrigger, actions: [], targets: [] };
    // 最近一次匹配到的**监听触发器**（供卡面级字段用：如「N次过后，升为老兵」要知道累计的是什么事件）
    let lastListen = null;
    let hqCarrier = false;      // 「使友方总部获得：」单独成行 → 其后的整段效果都挂到总部上
    let hqCarrierFrom = -1;     // ★ 记录"总部附魔"从 effects 的第几条开始，避免把前面的动作也吞进去

    // ⚠ 索引循环（不是 for...of）：复合充能拆分会往 lines 里**插入**监听段（当前行后面），
    //   for...of 的游标会被插入打乱（刚处理过的整行被再次读到 → 死循环）。
    for (let _li = 0; _li < lines.length; _li++) {
      const rawLine = lines[_li];
      // 整行被引号包住（跟着"总部获得："写的那种）→ 取引号里的内容
      let line = rawLine;
      const qm = line.match(/^[“"「]([\s\S]+)[”"」]$/);
      if (qm) line = qm[1].trim();
      // ★★ 充能：「充能完毕后，<某事件>，…」= **有条件**的充能效果。
      //   做法：剥掉前缀、打 chargeGate 标记，剩下的 `<某事件>`（如「友方一个单位攻击时」）
      //   照常走触发识别 —— 于是"充能门控"复用了全部现有触发，不用另造一套。
      //   （无条件的「充能完毕时，…」走 TRIGGERS 里的 chargeNow，不在这里剥。）
      let lineChargeGate = false;
      if (/^充能完毕后/.test(line)) {
        const cg = line.match(/^充能完毕后\s*[，,]?\s*(.+)$/);
        if (cg && cg[1]) { line = cg[1].trim(); lineChargeGate = true; }
      }
      // ★★ 复合型充能（武装）：「充能完毕后，具有重甲3，本单位攻击时，具有+3攻击力」
      //   = 充能完毕后立即武装、攻击后全部失去并重新计时（语义见 parseChargeArmLine）。
      //   命中就走 chargeArm 效果，不再往下当 chargeGate 解析（那条路编不出"获得词条"）；
      //   武装句后面若还跟着别的正文（rest），剩余部分照常走通用解析。
      if (lineChargeGate) {
        const arm = parseChargeArmLine(line);
        if (arm) {
          effects.push(arm.fx);
          if (arm.rest) { line = arm.rest; lineChargeGate = false; }
          else continue;
        } else if (!isCounter) {
          // ★ 通用拆分：「充能完毕后，<立即动作>，<监听触发>…」——
          //   监听段（"…时"触发开头）带上前缀插到**当前行后面**（下一轮按 chargeGate 解析；
          //   splice 插入保证不干扰索引循环游标）；剩余立即动作段本行继续 →
          //   匹配不到触发前缀 → 落到 chargeNow（见无前缀分支）。
          const sp = splitChargeNowAndListen(line);
          if (sp) {
            lines.splice(_li + 1, 0, '充能完毕后，' + sp.tail);
            line = sp.head;
          }
        }
      }
      // ★ OCR/转写错别字归一（制作者 2026-09-20 确认）：
      //   「友方单位攻击师，抽一张牌」= 攻击时；「有房位于支援阵线」= 友方位于支援阵线。
      //   放在所有规则之前，这样"卡面照抄的错字"也能解析出正确效果。
      line = line.replace(/攻击师/g, '攻击时').replace(/有房/g, '友方');
      // ★ 纯数值/费用行（「12K」「12K9行动花费」「9（6） 5」）不是效果文本：
      //   卡面数值走卡牌字段（cost/opCost/attack/defense），这里跳过，别报"未实现"污染覆盖率。
      if (/^\d+\s*K?(?:\s*\d+\s*行动花费)?$/.test(line)) continue;
      if (/^\d+\s*[（(]\s*\d+\s*[）)]\s*\d+$/.test(line)) continue;
      // 纯词条行（「闪击」「闪击 游击」「重甲3 守护」…）→ 不产生效果动作。
      //   这些词条已经在 cards.json 的 keywords / kwMap / kwValues 里表达，引擎按词条结算，
      //   编译器**不应该**再把它当效果正文去解析 —— 否则会产出「未实现：闪击 游击」的假告警，
      //   污染覆盖率统计，也让"这张卡是不是真的没实现"变得看不清。
      if (isKeywordOnlyLine(line)) continue;
      // 「每有一张"X"」这类计数条件行。
      //   ★ 2026-09-20：**已支持"按卡名计数增强"**（effects.js 的 num 支持
      //     `{count:spec, times:N}`，primitives 新增了「每有」前缀规则），
      //     所以这里**不能再无脑跳过整行** —— 那是"每有被静默忽略"的直接原因。
      //   只在前缀**单独成行、后面没有主体**时跳过（那种情况由上面的 isPerPrefix 归并拼给下一行）。
      if (/^每有一张\s*[“"「]/.test(line) && !/[，,]\s*\S/.test(line)) continue;
      // 「使友方总部获得：」自己一行 → 开启"总部承载"模式
      if (/^[使是]?\s*(?:友方|我方|敌方|对方|双方)?\s*总部\s*(?:获得|具有)\s*[：:]?\s*$/.test(line)) {
        // ★ 先把"总部获得"**之前**累积的动作落定为独立效果 ——
        //   否则它们会被后面的 hqEnchant 一起吞进去（如 Winter防空塔：
        //   「消灭场上所有空军，下一回合随机弃两张牌 / 使友方总部获得 / "敌方部署空军时将其压制"」
        //   前两句是独立动作，只有第三句该进总部附魔）。
        if (hqCarrierFrom < 0) {
          if (current.actions.length) { effects.push(finalize(current)); current = { trigger: defTrigger, actions: [], targets: [] }; }
          hqCarrierFrom = effects.length;
        }
        hqCarrier = true;
        continue;
      }
      // 触发前缀（可带兵种限定 / 敌方监听）——反制卡不走这里：它的触发条件已由 COUNTER_CLAUSES 处理
      let trig = null, body = line, trigUnitType = null, trigWatchFoe = false, trigFilter = null, trigOncePerTurn = false, trigCondition = null;
      let trigAlso = null;      // ★ 牌Q：「部署、移动、攻击时」→ 一个效果块挂多个事件
      let trigExtraActions = null;   // 三元组触发前缀自带的附加动作（如攻击必杀的 destroy defender）
      // 触发前缀里有**认不出的限定**（如「总部受到来自<未知兵种>的伤害时」）→ 显式失败，
      // 绝不悄悄把这个限定丢掉（制作者 2026-09-22 原则：「有东西被跳过则不能解析成功」）。
      let trigUnresolved = null;
      let trigHead = null;                                     // 匹配到的触发前缀原文（供埋伏反制的 owner 判定）
      // ★ 触发前缀**之前**的前置条件（「如果这是你的回合，友方单位部署时，…」）：
      //   先摘掉条件子句、再匹配触发前缀，摘下来的条件合并进 current.condition。
      //   ⚠ 以前这种"条件写在前面"的写法整行匹配不到触发前缀 → 整句落到通用解析 → "未实现"。
      //   只在**后面确实跟着触发前缀**时才摘（否则交给原语自己的条件分支处理，免得条件被丢）。
      let leadCond = null, trigLine = line;
      const firstEvent = /^(?:每|本)回合(?:中)?(?:首次|第一次)[，,]?/.test(trigLine) ? 'turn'
        : /^(?:整局|本局)?(?:首次|第一次)[，,]?/.test(trigLine) ? 'battle' : null;
      if (firstEvent) trigLine = trigLine.replace(/^(?:(?:每|本)回合(?:中)?|整局|本局)?(?:首次|第一次)[，,]?\s*/, '');
      if (!isCounter) {
        const lc = line.match(/^(?:如果|若|假如)([^，,。]{1,40})[，,]\s*([\s\S]+)$/);
        if (lc) {
          const P6 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
          const c6 = (P6 && P6.buildCondition) ? P6.buildCondition(lc[1]) : null;
          if (c6 && TRIGGERS.some(function (x) { return x[0].test(lc[2]); })) { leadCond = c6; trigLine = lc[2]; }
        }
      }
      for (const entry of (isCounter ? [] : TRIGGERS)) {
        // 三元组 [re, trigger, {extraActions}]：触发前缀自带**附加动作**（如攻击必杀的 destroy）
        const re = Array.isArray(entry) ? entry[0] : entry.re;
        const t = Array.isArray(entry) ? entry[1] : entry.t;
        const tExtra = (!Array.isArray(entry) && entry.extraActions) || (Array.isArray(entry) && entry[2] && entry[2].extraActions) || null;
        const m = trigLine.match(re);
        if (m) {
          trig = t; body = trigLine.slice(m[0].length); trigHead = m[0];
          // ★ 牌Q：「部署、移动、攻击时」这类前缀在表里写成 'unitDeployed|mobilize|attack'
          if (typeof trig === 'string' && trig.indexOf('|') >= 0) {
            const parts = trig.split('|');
            trig = parts[0]; trigAlso = parts.slice(1);
          }
          if (tExtra) trigExtraActions = tExtra;   // tExtra 本身就是动作数组（entry[2].extraActions）
          // ★ 卡面省略动词：触发前缀后直接跟「+N+N」（「每消灭…的单位，+1+1」）→ 补「获得」
          if (/^\+\s*[一二两三四五六七八九十\d]+\s*[+＋]/.test(body)) body = '获得' + body;
          // 「一回合一次，友方使用指令后，…」→ 每个友方回合只触发一次
          if (t === 'orderPlayed' && /一回合一次/.test(m[0])) trigOncePerTurn = true;
          if (t === 'unitMobilized' && /一回合一次/.test(m[0])) trigOncePerTurn = true;
          if (t === 'enemyKilled' && /一回合一次/.test(m[0])) trigOncePerTurn = true;
          if (m[1] && /来自/.test(m[0]) && /总部受到/.test(m[0])) {
            // ★★ 「友方总部受到**来自<兵种>的**伤害时」→ 触发 hqDamaged + 条件"伤害来源属于该兵种"。
            //   兵种词走**同一个词表**（primitives 的 traitFilterOf）："陆军"是**族**
            //   （infantry+tank+artillery）→ 得到数组 unitType，引擎按"任一命中"判。
            //   认不出的词**不硬猜**：记 trigUnresolved → 整句走显式失败（不许把限定悄悄丢掉）。
            const P5 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
            const _w5 = String(m[1] || m[2] || '').trim();
            const tf5 = (P5 && P5.traitFilterOf && _w5) ? P5.traitFilterOf(_w5) : null;
            const ut5 = (tf5 && tf5.unitType) || null;
            if (ut5) trigCondition = { op: 'damageFromType', unitType: ut5 };
            else trigUnresolved = '总部受到来自「' + _w5 + '」的伤害时（认不出这个兵种词）';
          } else if (m[1] && /友方单位数量/.test(m[0])) {
            // ★ 「友方单位数量不小于N时」→ 条件 unitCount >= N（触发事件已映射为 unitDeployed）
            trigCondition = { op: 'unitCount', cmp: '>=', value: num(m[1]), spec: { side: 'friendly' } };
          } else if (/^友方使用/.test(m[0]) && m[1] && !/^指令$/.test(m[1].trim())) {
            // ★ 「友方使用<卡名>后…」→ 条件"本次打出的是那张牌"。
            //   效果级 condition 在触发路径上**是生效的**（已用恒假/恒真条件对照实验验证：
            //   恒假 >=99 不执行、恒真 >=1 正常执行）。
            trigCondition = { op: 'eventCardIs', name: m[1].trim() };
          } else if (m[1]) {
            const tf = /被消灭时/.test(m[0]) ? descFilter(m[1]) : typeFilter(m[1]);
            // ★ 触发前缀里的描述**已被吃掉「单位」**（"友方游击单位被消灭时" → m[1]='游击'），
            //   typeFilter 的"词条+单位"守卫匹配不到 → 这里直接查词表兜底。
            if (!tf.unitType && !tf.keyword && !tf.setIn) {
              const P4 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
              const bare = String(m[1]).replace(/(?:单位|的)$/, '').trim();
              const kv = (P4 && bare) ? P4.traitFilterOf(bare) : null;
              if (kv) Object.assign(tf, kv);
            }
            if (tf.unitType) trigUnitType = tf.unitType;
            // 词条 / 系列限定 → 效果级 filter（引擎 runTrigger 对事件主角跑 matchFilter）
            if (tf.keyword || tf.setIn) {
              trigFilter = Object.assign({}, trigFilter || {});
              if (tf.keyword) trigFilter.keyword = tf.keyword;
              if (tf.setIn) trigFilter.setIn = tf.setIn;
            }
            if (tf.maxCost != null || tf.minCost != null || tf.damaged) {
              trigFilter = Object.assign({}, trigFilter || {}, tf);
              delete trigFilter.unitType;
            }
          }
          if (/^敌方/.test(m[0])) trigWatchFoe = true;
          break;
        }
      }
      if (trig && trig !== 'choose') lastListen = { trigger: trig, owner: trigWatchFoe ? 'foe' : 'self' };
      // ★ 「部署：抉择：A；或者B」——抉择写在触发前缀**之后**：把主体交给下面的抉择分支
      const chm0 = (!isCounter && trig !== 'choose') ? body.match(/^(?:抉择|选择)[：:]\s*([\s\S]+)$/) : null;
      if (chm0 && /或者|或/.test(chm0[1])) {
        // ⚠ 换触发器前必须给上一段收尾：否则抉择会被并进**上一段**（卡B 的「抽取：抽一张空军」段
        //   曾把后面的「部署：抉择：…」一起吞掉）。
        if (current.actions.length || current.cardFields) {
          effects.push(finalize(current));
          current = { trigger: defTrigger, actions: [], targets: [] };
        }
        trig = 'choose'; body = chm0[1];
      }
      if (trig === 'choose') {
        // 抉择：A 或者 B  → 收集为 chooseOne
        const mm = body.split(/或者|或/);
        if (mm.length >= 2) {
          const opts = [];
          let badOption = null;
          mm.forEach(part => {
            // ★ 选项优先走**原语组合器**（与主解析路径一致），它带卡名/数量/阵营的完整解析；
            //   旧的 parseSentence 只认部分专用正则，会把「将三张星盟通用大驱加入手牌」
            //   解析成 name="三张星盟通用大驱" count=1（数量词没剥掉）。
            const pp = parsePrimitives(part, {});
            const p = pp && pp.unparsed ? null
              : (pp && pp.actions && pp.actions.length ? pp
                : (!hasCompoundSyntax(part) ? parseSentence(part, {}) : null));
            if (!p || p.unparsed || p.uncertain || !(p.actions && p.actions.length)) {
              badOption = part.trim();
              return;
            }
            opts.push({ label: part.trim(), actions: p.actions, targets: p.targets });
          });
          if (badOption) {
            warnings.push('抉择未能完整解析（选项已阻止自动执行）：' + badOption + ' ← ' + body);
            current.actions.push({ op: 'log', text: '未实现：' + body });
            current.unimplemented = true;
            continue;
          }
          current.actions.push({ op: 'chooseOne', prompt: '抉择', options: opts });
          continue;
        }
      }
      if (trig && trig !== 'choose') {
        if (current.actions.length) effects.push(finalize(current));
        // ★ 指令卡 + 监听触发 → **埋伏成反制**（指令式陷阱）。
        //   order 卡打出即进弃牌堆，`friendlyDeath` 这类监听**没有载体、永远不会触发**
        //   （「解体」曾因此变成一张废牌）。正确语义（原版）：打出后挂起，
        //   事件发生时触发 —— 正好复用引擎现成的"指令式反制"机制
        //   （playCard：effects 里有 trigger==='counter' 的指令会转挂起态）。
        //   反制卡本身（isCounter）不走这里，维持 counter 语义。
        let effTrig = trig;
        let counterOn = null, counterOwner = null;
        // ⚠ 「使友方总部获得：“…”」（hqCarrier）是**总部附魔**，监听挂在总部上，
        //   不能转成 counter 埋伏 —— 只转换"裸监听"的指令陷阱。
        //   注意：单行写法「使友方总部获得：“友方太空单位部署时，…”」里，hqCarrier 的
        //   判定正则要求该行**以"总部获得"结尾**，单行带内容时不会置位 ——
        //   所以这里额外排除"本行含 总部获得/总部具有"的情况。
        const isHqEnchantLine = hqCarrier || /总部\s*(?:获得|具有)/.test(line);
        if (!isCounter && !isHqEnchantLine && (card && card.cardType === 'order') && LISTEN_TRIGGERS.indexOf(trig) >= 0) {
          effTrig = 'counter';
          counterOn = trig;
          counterOwner = /^敌方/.test(trigHead || '') ? 'foe' : 'self';
        }
        current = { trigger: effTrig, actions: [], targets: [] };
        // ★ 触发前缀自带的**附加动作**（三元组 extraActions，如攻击必杀的 destroy defender）
        if (trigExtraActions) current.actions.push(...trigExtraActions);
        // ★ 充能门控标记要在**新建 current 之后**补上 —— 剥离前缀时设的那个 current
        //   马上会被这里换掉（否则 chargeGate 丢，充能效果变成无条件一直触发）。
        if (lineChargeGate) current.chargeGate = true;
        if (counterOn) { current.on = counterOn; current.owner = counterOwner; }
        if (trigAlso) current.triggers = trigAlso;
        if (trigUnitType) current.unitType = trigUnitType;
        if (trigWatchFoe) current.watchOpponent = true;
        if (/^本单位?攻击时/.test(trigHead || '')) current.selfOnly = true;
        if (trigFilter) current.filter = trigFilter;
        if (trigCondition) current.condition = trigCondition;
        // 触发前缀之前的前置条件（先摘下来那个）：与触发器自带条件合并（and）
        if (leadCond) current.condition = current.condition ? { op: 'and', items: [leadCond, current.condition] } : leadCond;
        if (trigOncePerTurn) current.oncePerTurn = true;
        if (firstEvent) current.firstEvent = firstEvent;
        // 普通受伤能力每次触发；只有明确写首次才有次数限制。
        if (!firstEvent && trig === 'damaged' && /第一次|首次/.test(trigHead || line)) current.once = true;
      } else if (!trig) {
        // 无前缀：指令/反制默认各自的触发；单位卡若文本不像触发式，作为被动/部署处理
        // ★ 充能的"无条件"形态（制作者 2026-09-24 定口径）：「充能完毕后，<纯动作>」——
        //   动作没有"时"结尾（不是事件监听）→ 语义是**充能完毕立即执行一次** → 编成 chargeNow
        //   （引擎到点直接执行；同卡还有监听段时，监听触发后才统一重置充能
        //   = "充能效果必须全部触发才会进入下一次充能"）。有"时"的监听段
        //   （"友方使用指令时…"）上面 TRIGGERS 已认出 → 走 chargeGate，不落这里。
        if (lineChargeGate) {
          if (current.actions.length) effects.push(finalize(current));
          current = { trigger: 'chargeNow', actions: [], targets: [] };
        } else if (!current.actions.length) {
          current.trigger = defTrigger;
        }
      }
      const P0 = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
      // ⚠ 用 trigLine（摘掉前置条件后的那句）：条件交给下面的 condition 合并，别丢
      let dep = null;
      try { dep = P0 && P0.parseDeployUpgrade ? P0.parseDeployUpgrade(trigLine) : null; }
      catch (e) {
        const reason = '部署升级解析器异常，已阻止半截效果：' + (e && e.message ? e.message : String(e));
        dep = P0 && P0.unparsed ? P0.unparsed(trigLine, reason)
          : { unparsed: { text: String(trigLine || ''), reason: reason } };
      }
      if (dep && dep.unparsed) {
        warnings.push('未能完整解析（部署升级链已阻止半截效果）：' + (dep.unparsed.text || trigLine));
        current.actions.push({ op: 'log', text: '未实现：' + trigLine });
        current.unimplemented = true;
        continue;
      }
      if (dep) {
        if (current.actions.length) effects.push(finalize(current));
        // cardFields（如 upgradeOn）必须跟着走：finalize 会保留并聚合到 compile 顶层
        effects.push({ trigger: dep.trigger,
          condition: (leadCond && dep.condition) ? { op: 'and', items: [leadCond, dep.condition] } : (leadCond || dep.condition),
          actions: dep.actions, targets: dep.targets, cardFields: dep.cardFields });
        current = { trigger: defTrigger, actions: [], targets: [] };
        continue;
      }
      // 原语组合器是**主解析器**（它带完整的 sel/side/zone/filter/count 模型）；
      // 旧的专用正则只作为兜底，避免长尾句式回退成"未实现"
      const popts = {
        eventRef: EVENT_REF[trig] || (isCounter && defTrigger === 'counter' ? 'eventUnit' : null),
        // 卡面级字段（「N次过后，升为老兵」的 upgradeOn）需要知道"这句监听的是什么事件/听谁"
        listenerTrigger: trig || (lastListen && lastListen.trigger) || null,
        listenerOwner: trigWatchFoe ? 'foe' : ((lastListen && lastListen.owner) || 'self'),
        // ★ 2026-09-25 制作者铁律：「只要卡面没写『部署：』，就一定是光环效果」——
        //   原语规则用它区分"显式部署触发"（TRIGGERS 认出「部署：」前缀）与
        //   "defTrigger 兜底"（单位卡无前缀正文，被默认成 deploy）。后者对
        //   「下一个加入战场的友方单位获得X」类句式产出**常驻光环**而非一次性附魔。
        explicitTrig: !!trig,
      };
      /* 「N次过后 / 每N次」的处理**不在这里**，在 primitives.js 的 P.parse 里（Alan 09-28）——
       *   复合句会按逗号拆成子句，前缀常落在**子句中段**（「将其返回手牌，三次过后，升为老兵」的
       *   「三次过后」是第 2 个子句），只有下沉到 P.parse 才能同时覆盖整句级和子句级。
       *   ⚠ 别在这里再剥一次前缀：剥掉了 P.parse 就看不到 nth，两边都会漏。 */
      const primitiveRes = parsePrimitives(body, popts);
      const res = (primitiveRes && primitiveRes.unparsed) ? primitiveRes
        : (primitiveRes || (!hasCompoundSyntax(body) ? parseSentence(body, {}) : null));
      /* ★★ 制作者 2026-09-22 原则：「**如果有东西被跳过则不能解析成功**」。
       *
       *   旧行为：只要 `res` 有值就算成功 —— 而 `res` 完全可能是"从句子中间挑走一段"的结果：
       *   触发前缀没被 TRIGGERS 认出来 → 它留在正文里 → 动作规则**从中间匹配**走后半句 →
       *   "解析成功"，但**前缀和它携带的限定静默消失**，且不报任何 error/warning。
       *
       *   典型（制作者报）：「友方总部受到**来自陆军的**伤害时，对敌方总部造成3点伤害」
       *     → 产出 {trigger:'deploy', actions:[damageHQ 3]}：
       *       "总部受到来自陆军的伤害"这个**触发 + 来源限定全丢**，却看起来一切正常。
       *
       *   判据用**动作规则实际匹配到的片段**（`P.parse` 返回的 `actText`）来定位：
       *     · `actText` 覆盖到了句首 → 没有跳过开头，正常；
       *     · 前面还剩一段（`head`），且这段**长得像触发前缀**（含明确事件词 + 以「时，/后，」收尾）
       *       → 判定为"有内容被跳过" → **显式失败**（标 unimplemented），绝不产出半截效果。
       *   ⚠ 为什么不用"正文以…时，开头"这种宽判据：`具有冲击时，获得+N攻击力`、
       *     `在手牌中时，每使用一张情报牌…` 这类是**原语层整句处理**的合法句式，
       *     宽判据会把它们一起打成"未实现"（实测误伤 11 句）。用 actText 定位后零误伤。
       *   ⚠ 拿不到 `actText`（走 compiler 兜底 `parseSentence` 的路径）时**不做判定** —— 宁可漏抓，不可误杀。 */
      const leftoverTrigger = (function () {
        if (isCounter || trig || leadCond) return false;
        const at = (res && res.actText) || '';
        if (!at) return false;
        const atIdx = body.indexOf(at);
        if (atIdx <= 0) return false;                 // 覆盖到句首 = 没有跳过开头
        const head = body.slice(0, atIdx);
        // head = "动作规则匹配到的片段**之前**剩下的那段"。它若以「…时，」/「…后，」收尾，
        // 就**长得像个触发前缀**，而 TRIGGERS 又一条都没认（上面的 `trig` 为 null）
        // → 这段前缀就是"被跳过的内容"，判失败。
        // ⚠ **不要**在这里列举事件词（受到/消灭/摧毁/击毁/阵亡…）—— 同义词永远列不全，
        //   实测就漏掉了「友方单位被**击毁**时，…」。靠 actText 定位 + "…时，/…后，"形态已经足够精确：
        //   原语层**整句处理**的句式（如「具有冲击时，获得+N攻击力」）actText 覆盖句首、atIdx=0，
        //   根本进不了这个分支。
        return /(?:时|后)\s*[，,]\s*$/.test(head);
      })();
      const leftoverAction = !(res && res.unparsed) && hasUnconsumedActionTail(body, res && res.actText);
      if (trigUnresolved || leftoverTrigger) {
        warnings.push(trigUnresolved
          ? ('触发前缀有认不出的限定（按"不许跳过"的规则不算解析成功）：' + trigUnresolved + ' ← ' + body)
          : ('触发前缀未能识别（有内容被跳过，按"不许跳过"的规则不算解析成功）：' + body));
        current.actions.push({ op: 'log', text: '未实现：' + body });
        current.unimplemented = true;
      } else if (leftoverAction) {
        warnings.push('动作规则只匹配了句子的一部分（尾部仍有动作，已阻止半截效果）：' + body);
        current.actions.push({ op: 'log', text: '未实现：' + body });
        current.unimplemented = true;
      } else if (res && res.unparsed) {
        const detail = res.unparsed.reason ? '（' + res.unparsed.reason + '）' : '';
        warnings.push('未能完整解析' + detail + '；已阻止半截效果：' + (res.unparsed.text || body));
        current.actions.push({ op: 'log', text: '未实现：' + body });
        current.unimplemented = true;
      } else if (res) {
        res.actions.forEach(a => {
          // ★ 自带 trigger 的**完整效果块**（如 passive 光环「友方单位造成的对战伤害+1」）：
          //   它不是"当前触发段里的一个动作"，而是独立的常驻声明，必须单独成条 ——
          //   塞进 current.actions 会被外层 trigger 包住，recomputeAuras 就看不到了。
          if (a && a._standaloneEffect) {
            if (current.actions.length) { effects.push(finalize(current)); current = { trigger: defTrigger, actions: [], targets: [] }; }
            const se = a._standaloneEffect;
            // ★ 充能完毕语境下的"常驻声明"必须**降格为一次性动作**（制作者 2026-09-24 口径）：
            //   「充能完毕后，所有友方单位具有+4攻击力」= 到点给一次 buff，**不是**常驻光环
            //   （光环在 chargeNow 语义下等于每 X 回合叠一层，直接错）。
            //   只转 attack/defense 属性；出现别的字段说明是没见过的光环形态 →
            //   保留原样并提示人工确认（绝不静默丢语义）。
            if (lineChargeGate && se.trigger === 'passive' && se.aura) {
              const au = se.aura;
              const extra = Object.keys(au).filter(k => k !== 'target' && k !== 'attack' && k !== 'defense');
              if (extra.length) {
                effects.push(se);
                warnings.push('需人工确认：充能完毕后的光环声明未转为一次性动作（含未知字段 ' + extra.join('/') + '）：' + body);
              } else {
                const act = { op: 'buff', target: au.target };
                if (au.attack) act.attack = au.attack;
                if (au.defense) act.defense = au.defense;
                effects.push({ trigger: 'chargeNow', actions: [act] });
              }
            } else {
              effects.push(se);
            }
            return;
          }
          current.actions.push(a);
        });
        if (res.targets) res.targets.forEach(t => current.targets.push(t));
        if (res.cardFields) current.cardFields = Object.assign({}, current.cardFields, res.cardFields);
        if (res.uncertain) warnings.push('需人工确认：' + body);
      } else if (!res) {
        // ★ body 为空 = 触发前缀自带全部动作（如攻击必杀「消灭受到本单位对战伤害的单位」）→ 不算失败
        if (body.trim()) {
          warnings.push('未能自动解析：' + body);
          current.actions.push({ op: 'log', text: '未实现：' + body });
          current.unimplemented = true;
        }
      }
    }
    if (current.actions.length || current.cardFields) effects.push(finalize(current));
    // 打断型反制只需要"声明"（真正的结算由引擎在事件发生前做），允许没有 actions
    if (isCounter && counterMeta.interrupt && !effects.some(function (e) { return e.trigger === 'counter'; })) {
      effects.push({
        trigger: 'counter', actions: [],
        on: counterMeta.on, owner: counterMeta.owner, interrupt: counterMeta.interrupt,
      });
    }
    // 反制卡的触发条件（开头抠出来的那一段）落到第一条 counter 效果上
    if (isCounter && counterMeta.on) {
      const first = effects.find(function (e) { return e.trigger === 'counter'; });
      if (first) {
        first.on = counterMeta.on;
        if (counterMeta.owner) first.owner = counterMeta.owner;
        if (counterMeta.interrupt) first.interrupt = counterMeta.interrupt;
      }
    }
    // 「使友方总部获得：」后面的整段效果 → 合并成一条总部附魔
    //   ★ 只合并 hqCarrierFrom 之后的效果（前面累积的独立动作保持原样）
    if (hqCarrier && hqCarrierFrom >= 0 && effects.length > hqCarrierFrom) {
      const sub = effects.splice(hqCarrierFrom, effects.length - hqCarrierFrom);
      effects.push({
        trigger: 'order',
        actions: [{ op: 'hqEnchant', name: (card && card.name) || '总部效果', effects: sub }],
      });
    }
    // ★ 还原**嵌套**在 hqEnchant（总部附魔）里的 counter 转换：
    //   "使友方总部获得：“友方太空单位部署时，X”" 的监听挂在**总部**上，不是"打出后埋伏"，
    //   转 counter 会导致总部附魔失效（单行写法下 hqCarrier 未置位，转换会误伤嵌套监听）。
    (function restoreNested(effs) {
      (effs || []).forEach(function (e) {
        if (!e) return;
        (e.actions || []).forEach(function (a) {
          if (!a) return;
          if (a.op === 'hqEnchant' && Array.isArray(a.effects)) {
            a.effects.forEach(function (sub) {
              if (sub && sub.trigger === 'counter' && sub.on) {
                sub.trigger = sub.on;
                delete sub.on; delete sub.owner; delete sub.interrupt;
              }
              if (sub) restoreNested([sub]);
            });
          }
          if (Array.isArray(a.options)) a.options.forEach(function (o) { restoreNested(o.actions); });
        });
      });
    })(effects);
    const finalEffects = validateCardNames(effects);
    if (oncePerTurnAll) finalEffects.forEach(function (e) { if (e && !e.once) e.oncePerTurn = true; });
    return { effects: finalEffects, warnings, cardFields: Object.assign({}, startFields, effects.reduce((acc, e) => Object.assign(acc, e.cardFields || {}), {})) };
  };

  /* ★ 出口统一校验卡名（制作者规则：**单位名字都是有引号的**）
   *   任何路径（原语组合器 / 旧的兜底 parseSentence）产出的卡牌操作，若 `name` 既没有引号依据、
   *   又**不命中卡池**，就是无效卡名 —— 引擎只会报"卡池中找不到「蜂鸟突击舰和」"然后静默失效。
   *   这里统一收拾：能退化成"兵种/系列 filter"的就退化，否则去掉 name（让它显式报"缺卡名"，
   *   而不是拿一个不存在的名字蒙混过关）。
   */
  function validateCardNames(effects) {
    const P = (typeof window !== 'undefined' ? window : globalThis).KG_PRIMITIVES;
    if (!P || !P.resolveCardRef) return effects;
    const CARD_NAMING_OPS = ['addCardToHand', 'summon', 'shuffleIn', 'shuffleInUntil', 'deckToField'];
    const fix = function (list) {
      (list || []).forEach(function (a) {
        if (!a) return;
        if (CARD_NAMING_OPS.indexOf(a.op) >= 0 && a.name && !a.cardId && !a.self) {
          // `_quotedName` 来自原语层：卡面带引号就是作者明确指定的卡名，
          // 不得因为 OCR 空格、连字符或当前池暂未加载而退化成兵种/系列 filter。
          // 例：「BS-20A1坦克」应保持为指定卡名，即使卡池写作「BS-20A1 坦克」。
          const explicitName = !!a._quotedName;
          delete a._quotedName;
          const ref = P.resolveCardRef(String(a.name).replace(/[“”"「」]/g, ''), explicitName);
          if (ref && ref.cardId) {
            a.cardId = ref.cardId; a.name = ref.name;        // 别名命中 → 直接换 id
          } else if (ref && ref.name) {
            a.name = ref.name;                              // 命中卡池（顺带修正大小写）
          } else if (ref && ref.filter) {
            delete a.name; a.filter = ref.filter;           // 退化为"兵种/系列"卡池
          } else {
            delete a.name;                                  // 认不出 → 显式报"缺卡名"
          }
        }
        ['actions', 'then', 'else'].forEach(function (k) { fix(a[k]); });
        (a.options || []).forEach(function (o) { fix(o.actions); });
      });
    };
    effects.forEach(function (e) { fix(e.actions); });
    return effects;
  }

  function finalize(e) {
    const out = { trigger: e.trigger, actions: e.actions };
    if (e.targets && e.targets.length) out.targets = e.targets;
    if (e.triggers && e.triggers.length) out.triggers = e.triggers;   // ★ 牌Q：多事件监听
    if (e.unimplemented) out.unimplemented = true;
    if (e.cardFields) out.cardFields = e.cardFields;
    if (e.unitType) out.unitType = e.unitType;
    if (e.filter) out.filter = e.filter;
    if (e.watchOpponent) out.watchOpponent = true;
    if (e.oncePerTurn) out.oncePerTurn = true;
    if (e.firstEvent) out.firstEvent = e.firstEvent;
    if (e.selfOnly) out.selfOnly = true;
    if (e.once) out.once = true;              // 「受到伤害后」这类一次性触发（引擎读 ef.once）
    // ★ 充能门控：「充能完毕后，<事件>，…」剥掉前缀后打上这个标记。
    //   引擎只在单位"充能完毕就绪"时才执行它；执行一次即失去并重新计时（见 effects.js 的 exec）。
    if (e.chargeGate) out.chargeGate = true;
    if (e.condition) out.condition = e.condition;   // 条件触发（如「友方单位数量不小于N时」）
    // 反制卡的触发条件
    if (e.trigger === 'counter') {
      if (e.on) out.on = e.on;
      if (e.owner) out.owner = e.owner;
      if (e.interrupt) out.interrupt = e.interrupt;
    }
    return out;
  }

  if (typeof module !== 'undefined' && module.exports) module.exports = C;
})(typeof window !== 'undefined' ? window : globalThis);
