/* 效果原语：把卡面中文按"关键词"拆解，再**排列组合**成可执行效果。
 *
 * 设计（按制作者思路）：
 *   一句话 = [选靶方式] + [阵营] + [区域] + [限定条件] + [动作] + [数值]
 *   例如「随机消灭一个单位」     = 选靶:随机 + 动作:消灭 + 数量:一个
 *        「消灭前线所有单位」    = 动作:消灭 + 区域:前线 + 选靶:所有
 *        「对一个花费不大于4的敌方单位造成3点伤害」
 *                              = 数量:一个 + 条件:花费≤4 + 阵营:敌方 + 动作:造成伤害(3)
 * 这些原语可以任意组合，不再需要为每句话写一条正则。
 *
 * 约定（与引擎/制作者规则一致）：
 *   - 指向性（"一个"）：默认 side:'any' → 由玩家选目标
 *   - 非指向性（"所有"/"随机"）且没写阵营：增益类默认友方，伤害/消灭类默认敌方
 */
(function (root) {
  const P = {};
  const CN = { 一: 1, 二: 2, 两: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10, 半: 0 };
  function num(s) {
    if (s == null) return null;
    s = String(s).trim();
    if (/^-?\d+$/.test(s)) return parseInt(s, 10);
    if (CN[s] != null) return CN[s];
    if (/^十[一二三四五六七八九]$/.test(s)) return 10 + CN[s[1]];
    if (/^[一二三四五六七八九]十$/.test(s)) return CN[s[0]] * 10;
    if (/^[零〇一二两三四五六七八九十百千]+$/.test(s) && /[十百千]/.test(s)) {
      let total = 0, digit = 0;
      for (const ch of s) {
        const scale = ({ 十: 10, 百: 100, 千: 1000 })[ch];
        if (scale) { total += (digit || 1) * scale; digit = 0; }
        else digit = ch === '零' || ch === '〇' ? 0 : CN[ch];
      }
      return total + digit;
    }
    return null;
  }
  P.num = num;

  /* ============================================================ 兵种词表（唯一权威源）
   * 中文兵种/类别词 → 引擎 unitType（或多个）。
   * ⚠ 这里必须是**全项目唯一**的一份。历史上同一个映射抄了三份：
   *     primitives.js 的 TYPE_WORD_FILTER / compiler.js 的 TYPES / effects.js 的 TYPE_CN2+TYPE_CN3，
   *   三份不一致时，同一句话在"解析期"和"结算期"会得到不同的兵种 —— 这是最难查的一类 bug。
   * 现在 compiler.js / effects.js 都从这里取（P.filtersFromWord / P.KW_CN …）。
   * 值的两种形态：
   *   字符串        → 单兵种
   *   字符串数组    → 一个"族"（如 陆军 = 步兵+坦克+火炮），下游按"任一命中"处理
   */
  const UNIT_TYPES = {
    步兵: 'infantry',
    坦克: 'tank',
    火炮: 'artillery',
    炮兵: 'artillery',
    战斗机: 'fighter',
    轰炸机: 'bomber',
    太空战机: 'spacefighter',
    巡地舰: 'landcruiser',
    巡航舰: 'cruiser',
    工事: 'structure',
    建筑: 'structure',
    空军: ['fighter', 'spacefighter', 'bomber'],
    陆军: ['infantry', 'tank', 'artillery'],
    // ★ 「地面单位」= **非**太空单位（engine.js isType 对 'nonspace' 做否定判定）。
    //   曾经不在词表里 → typeFilterFromText 返回 null，下游兜底成 infantry，
    //   于是「对地面单位造成的伤害+4」只对步兵生效（UNTED-LPD-7 的 bug）。
    //   注意：**不是**陆军族（infantry/tank/artillery）——空军与工事也算地面单位。
    地面单位: 'nonspace',
    地面: 'nonspace',
    太空单位: 'space',
    太空: 'space',
  };
  P.UNIT_TYPES = UNIT_TYPES;
  // 按"词"取兵种（精确匹配）
  P.typeOfWord = function (w) { return UNIT_TYPES[String(w || '').trim()] || null; };
  // ★ 「Mk坦克」这类**拉丁词 + 兵种/词条词** → { name:'Mk', unitType:'tank' }
  //   （matchFilter 的 name 是"名字包含"）。卡面里 Mk / MR / USG 之类的系列前缀都走这条。
  function latinTraitFilter(word) {
    const w = String(word || '').trim();
    if (!w) return null;
    const m = w.match(/^([A-Za-z][A-Za-z0-9\-\.]{0,7})\s*(.*)$/);
    if (!m) return null;
    const rest = (m[2] || '').replace(/单位$/, '').trim();
    const tf = rest ? (P.traitFilterOf(rest) || P.typeFilterFromText(rest)) : null;
    const out = { name: m[1] };
    if (tf && tf.unitType) out.unitType = tf.unitType;
    else if (tf && tf.keyword) out.keyword = tf.keyword;
    return out;
  }
  P.latinTraitFilter = latinTraitFilter;
  // ★ 「USG太空战机和轰炸机」「Mk坦克」「友方雷达站和工事」这类**词串** → 一个 matchFilter 口径的 filter。
  //   系列词（SET_WORD_FILTER）→ setIn；多个兵种词 → unitType 数组；拉丁词 → name。
  function wordsFilter(word) {
    const src = String(word || '').replace(/单位$/, '').trim();
    if (!src) return null;
    const out = {};
    for (const key in SET_WORD_FILTER) {
      if (src.indexOf(key) >= 0) {
        const v = SET_WORD_FILTER[key];
        out.setIn = Array.isArray(v) ? v.slice() : [v];
        break;
      }
    }
    let rest = src.replace(/[的和与及、,，]/g, '');
    const types = [];
    Object.keys(UNIT_TYPES).sort(function (a, b) { return b.length - a.length; }).forEach(function (k) {
      if (types.length >= 4 || rest.indexOf(k) < 0) return;
      rest = rest.split(k).join('');        // ★ 吃掉已识别的长词，避免短词重复命中（「太空战机」之后不该再加「太空」族）
      const t = UNIT_TYPES[k];
      (Array.isArray(t) ? t : [t]).forEach(function (x) { if (types.indexOf(x) < 0) types.push(x); });
    });
    if (types.length) out.unitType = types.length === 1 ? types[0] : types;
    const lm = src.match(/[A-Za-z][A-Za-z0-9\-\.]{0,7}/);
    if (lm && !out.setIn) out.name = lm[0];
    return Object.keys(out).length ? out : null;
  }
  P.wordsFilter = wordsFilter;
  // 在一段文字里找兵种词 → { unitType } 或 null（长词优先，避免"太空战机"被"太空"抢走）
  const TYPE_KEYS_BY_LEN = Object.keys(UNIT_TYPES).sort(function (a, b) { return b.length - a.length; });
  P.typeFilterFromText = function (text) {
    const t = String(text || '');
    for (const k of TYPE_KEYS_BY_LEN) if (t.indexOf(k) >= 0) return { unitType: UNIT_TYPES[k] };
    return null;
  };

  /* ------------------------------------------------------------ 词表 */
  // 选靶方式
  const QUANT = [
    { re: /随机/, sel: 'random' },
    { re: /(所有|全部|全体|任意数量|每个|各个|场上所有)/, sel: 'all' },
    { re: /(多个|若干)/, sel: 'all' },
    { re: /(至多|最多)\s*([一二两三四五六\d]+)\s*(个|张|名|辆|架|艘)?/, sel: 'upto' },
    // ★ 「一个/1张」的量词**必填**（原来是可选）：否则「获得+1攻击力」里的 "1" 会被当成
    //   "选一个单位"，让本该 target:self 的效果变成选靶（UNTED-236 曾因此变成"选一个友方单位 +1攻击力"）。
    { re: /(?:一|1)\s*(个|张|名|辆|架|艘|支|枚)/, sel: 'one' },
    { re: /([一二两三四五六七八九十\d]+)\s*(个|张|名|辆|架|艘|支|枚)/, sel: 'many' },
  ];
  // 阵营
  const SIDE = [
    { re: /友方|我方|自己的?/, side: 'friendly' },
    { re: /敌方|对方|对手/, side: 'enemy' },
    { re: /任意|双方/, side: 'any' },
    // 「场上所有X」不带阵营词 → 一般不分敌我（如「消灭场上所有受伤单位」）。
    //   放在 friendly/enemy 之后：有阵营词时优先用阵营词。
    //   ⚠ weak:true = 这只是"没写阵营"的兜底。**正面动作**（群体增益）按制作者规则
    //     默认只给友方（即使写了"所有"）—— 见下面组装阶段的 POSITIVE 处理。
    { re: /场上(?:所有|全部|全体)/, side: 'any', weak: true },
  ];
  // 区域
  const ZONE = [
    { re: /前线/, zone: 'frontline' },
    { re: /支援(阵线|线)?/, zone: 'support' },
  ];
  // 限定条件 → filter
  const FILTERS = [
    { re: /陆军/, filter: { unitType: ['infantry', 'tank', 'artillery'] } },
    { re: /空军/, filter: { unitType: ['fighter', 'spacefighter', 'bomber'] } },
    { re: /太空单位|太空(?!战机)/, filter: { unitType: 'space' } },
    { re: /巡地舰/, filter: { unitType: 'landcruiser' } },
    { re: /巡航舰/, filter: { unitType: 'cruiser' } },
    { re: /太空战机/, filter: { unitType: 'spacefighter' } },
    { re: /战斗机/, filter: { unitType: 'fighter' } },
    { re: /轰炸机/, filter: { unitType: 'bomber' } },
    { re: /火炮|炮兵/, filter: { unitType: 'artillery' } },
    { re: /步兵/, filter: { unitType: 'infantry' } },
    { re: /坦克/, filter: { unitType: 'tank' } },
    { re: /工事/, filter: { unitType: 'structure' } },
    { re: /花费不大于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ maxCost: num(m[1]) }) },
    { re: /花费不小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minCost: num(m[1]) }) },
    { re: /受到?过?伤害的|已?受伤/, filter: { damaged: true } },
    { re: /未受伤的?/, filter: { undamaged: true } },
    { re: /行动花费不小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minOpCost: num(m[1]) }) },
    // 攻击力阈值：「攻击力不大于3的敌方单位」→ 通用 filter（引擎 matchFilter 读 maxAttack/minAttack）。
    //   ★ 以前这条写死在 LONGTAIL 里、把「消灭」硬编成 damage(amount:null,sel:'all')：
    //     动作被吃掉（消灭→伤害）、筛选条件也丢 —— 属于"解析成功但结果全错"的假绿。
    //     现在只产出**过滤条件**，动作/阵营/选靶全部交给组合器。
    { re: /攻击力不大于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ maxAttack: num(m[1]) }) },
    { re: /攻击力不小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minAttack: num(m[1]) }) },
    { re: /攻击力小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ maxAttack: num(m[1]) - 1 }) },
    { re: /攻击力大于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minAttack: num(m[1]) + 1 }) },
    // 防御力阈值（与攻击力阈值对称；引擎 matchFilter 已支持 maxDefense/minDefense）
    { re: /防御力不大于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ maxDefense: num(m[1]) }) },
    { re: /防御力不小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minDefense: num(m[1]) }) },
    { re: /防御力小于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ maxDefense: num(m[1]) - 1 }) },
    { re: /防御力大于\s*([一二两三四五六七八九十\d]+)/, filter: (m) => ({ minDefense: num(m[1]) + 1 }) },
    // ★ 「Mk坦克」= 名字含 Mk 的坦克（拉丁系列词 + 兵种词）
    { re: /([A-Za-z][A-Za-z0-9\-\.]{0,7})\s*(步兵|坦克|火炮|炮兵|战斗机|轰炸机|太空战机|巡地舰|巡航舰|工事|建筑|空军|陆军|太空单位|太空)/,
      filter: (m) => latinTraitFilter(m[1] + m[2]) || {} },
    // ★ 「其他单位 / 其它单位」= 除自己以外的单位（双方都算）
    { re: /(?:其他|其它)(?:的)?\s*单位/, filter: () => ({ excludeSelf: true, bothSides: true }) },
    // ★ 「USG和星盟单位」「协约国单位」「帝国单位」→ 系列过滤（可两个系列并列 → setIn 数组）。
    //   必须后跟「单位/牌」，否则会把卡名里的系列词（"帝国一号坦克"）也当成过滤条件。
    { re: /(USG|AV76|Av76|av76|星盟|协约国|帝国|帝安|新地|卡尔拉|联合国国家|联合国)\s*(?:和|与|及|、|,)?\s*(USG|AV76|Av76|av76|星盟|协约国|帝国|帝安|新地|卡尔拉|联合国国家|联合国)?\s*(?:单位|牌)/,
      filter: (m) => {
        const out = [];
        [m[1], m[2]].forEach(function (w) {
          if (!w) return;
          const v = SET_WORD_FILTER[w] || SET_WORD_FILTER[w.toUpperCase()] || null;
          if (!v) return;
          (Array.isArray(v) ? v : [v]).forEach(function (x) { if (out.indexOf(x) < 0) out.push(x); });
        });
        return out.length ? { setIn: out } : {};
      } },
    { re: /[“"]([^”"]{2,10})[”"]/, filter: (m) => ({ name: m[1] }) },
    // 词条限定（「场上所有游击单位」→ 按词条过滤）。
    //   ⚠ 必须要求后跟「单位/的」，否则「使所有太空单位获得游击和+2+2」里的裸「游击」
    //   会被当成目标 filter（曾导致 filter 里混入 keyword:guerrilla，目标集合变空）。
    { re: /游击(?:单位|的)/, filter: { keyword: 'guerrilla' } },
    { re: /(?:固守|守护)(?:单位|的)/, filter: { keyword: 'guard' } },
    { re: /闪击(?:单位|的)/, filter: { keyword: 'blitz' } },
    { re: /烟幕(?:单位|的)/, filter: { keyword: 'smokescreen' } },
    { re: /伏击(?:单位|的)/, filter: { keyword: 'ambush' } },
    // 「奋战」与「狂怒」是同一个词条，统一归一到 fury（引擎里两者完全等价）
    { re: /(?:奋战|狂怒)(?:单位|的)/, filter: { keyword: 'fury' } },
    { re: /老兵(?:单位|的)/, filter: { keyword: 'veteran' } },
  ];
  // 动作：返回 { op, ... }，可在 build 里用已解析出的 target/数值
  const ACTIONS = [
    // ★★ 「获得等同于**上一回合被消灭的单位数量**的指挥点」（2026-09-22，制作者报）
    //   ⚠ 必须排在下面那条通用动词规则 `消灭|摧毁` **之前**：那条**不锚定句首**，
    //     会在"上一回合**被消灭**的单位数量"里命中"消灭" → 产出 destroy（还带上"选一个单位"）
    //     → 整句变成"消灭一个单位"，与卡面南辕北辙（制作者实测就是这个现象）。
    // ★ 2026-09-26 修（制作者报「改烂了」）：卡面常把句尾的「指挥点」省掉
    //   （BSUC 后勤第312团：「获得等同于上一回合被消灭单位数量」＋下一句「若其不小于4，则额外获得一个指挥点槽」）。
    //   旧正则**强制要求**句尾有「指挥点」→ 本规则失配 → 被下面那条不锚定的通用「消灭」从中间吃掉
    //   → 凭空产出 destroy（还附带"选一个单位"的靶窗）。现在：被/的/指挥点全可选，
    //   但「指挥点槽」必须排除（那是 gainKreditSlot 的活）。
    { re: /获得\s*等同于\s*上一回合\s*(?:被|所)?\s*(?:消灭|摧毁|阵亡)的?\s*(?:单位|单位数|单位数量|单位数(?:目|量))的?\s*(?:指挥点(?!槽))?/,
      build: () => ({ op: 'gainKredits', amount: { stat: 'lastTurnKilled', of: 'self' }, side: 'self' }) },
    // 「若其不小于N，额外获得一个指挥点槽」——"其"指代前半句的数值
    //   （本项目里就是"上一回合被消灭的单位数"，见 CONDS.lastTurnKilledCompare）。
    //   ⚠ 前缀"若其/若该数量/若此数"**不可选**：否则这条会去抢普通的"获得一个指挥点槽"。
    { re: /^(?:若其|若该数量|若此数)\s*(不小于|大于|至少|不少于|超过|不大于|小于|至多|不超过)\s*([一二两三四五六七八九十\d]+)\s*[，,]?\s*(?:则)?\s*(?:额外)?获得\s*(?:一个|1\s*个)?\s*指挥[点额]槽/,
      build: (m) => {
        const CMP = { '不小于': '>=', '大于': '>', '至少': '>=', '不少于': '>=', '超过': '>',
                      '不大于': '<=', '小于': '<', '至多': '<=', '不超过': '<=' };
        return { op: 'gainKreditSlot', amount: 1, side: 'self',
          condition: { op: 'lastTurnKilledCompare', cmp: CMP[m[1]] || '>=', value: num(m[2]) } };
      } },
    // ⚠ 通用「消灭/摧毁」**不许**吃被动语态的「被消灭 / 被摧毁 / 所消灭」：
    //   「获得等同于上一回合**被消灭**的单位数量」曾被它从中间匹配走 → 整句变成"消灭一个单位"（凭空多一个破坏性动作）。
    //   用后行断言挡掉；「随机消灭一个敌方单位」这类主动语态不受影响。
    { re: /(?<![被所])消灭|(?<![被所])摧毁/, build: () => ({ op: 'destroy' }) },
    // 复制手牌（随机 / 花费筛选 / 全体 / 指定）
    { re: /复制\s*(?:一|1)?\s*(?:张)?\s*(随机)?\s*(所有|全部)?\s*(?:花费不大于\s*([一二两三四五六七八九十\d]+)\s*的)?\s*(?:手牌|牌|一张手牌)/,
      build: (m) => ({ op: 'copyHandCard', side: 'self', sel: (m[1] || m[2]) ? (m[2] && !m[1] ? 'all' : 'random') : 'random', count: 1,
        filter: m[3] ? { maxCost: num(m[3]) } : undefined }) },
    // 群体 / 单体改行动花费
    { re: /使\s*(?:一|1)?\s*(?:个|张|名|辆|架|艘)?\s*(所有|全部|场上所有)?\s*(友方|我方|敌方|对方)?\s*([^，,。]{0,8}?)\s*行动花费\s*([+＋\-−减]\s*[一二两三四五六七八九十\d]+)/,
      build: (m) => {
        const all = !!m[1];
        const side = /敌/.test(m[2] || '') ? 'enemy' : 'friendly';
        const desc = (m[3] || '').trim();
        const amount = num(String(m[4]).replace(/[＋+]/, '').replace(/[减−]/, '-').replace(/\s/g, ''));
        const t = UNIT_TYPES[desc] || null;     // 唯一权威源
        if (/手牌/.test(m[0])) return { op: 'handOpCostMod', side: 'self', amount: amount, target: { sel: 'random' }, filter: t ? { cardType: 'unit' } : undefined };
        if (all) return { op: 'opCostModAll', target: Object.assign({ sel: 'all', side: side }, t ? { filter: { unitType: t } } : {}) , amount: amount };
        return { op: 'opCostMod', target: { sel: 'random', side: side, filter: t ? { unitType: t } : undefined }, amount: amount };
      } },
    { re: /对\s*(敌方|对方|对手|友方|我方|自己)?\s*总部造成\s*([一二两三四五六七八九十\d]+)\s*点伤害/,
      build: (m) => ({ op: 'damageHQ', amount: num(m[2]), side: /敌|对方|对手/.test(m[1] || '') ? 'enemy' : (m[1] ? 'self' : 'enemy') }) },
    { re: /(?:造成|打出|受到)\s*([一二两三四五六七八九十\d]+)\s*点伤害/, build: (m) => ({ op: 'damage', amount: num(m[1]) }) },
    // 并列增益：「获得游击和+2+2」→ 先给词条再给身材
    { re: /获得\s*([\u4e00-\u9fa5]{2,4})\s*和\s*\+([一二两三四五六七八九十\d]+)\s*\+([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'grantAndBuff', keyword: m[1], attack: num(m[2]), defense: num(m[3]) }) },
    // ★★ 「（若可能，）将其 / 其中一张 加入前线」（2026-09-22 制作者报）
    //   把**刚召唤出来的**单位移到前线。
    //   ⚠ 必须排在下一条「将一张X加入支援阵线/前线」的 summon 规则**之前** ——
    //     那条会把"将**其中一张**加入前线"当成"召唤一张名叫『其中一张』的卡"（实测产物就是 summon）✗
    //   `target:{sel:'ref', ref:'summoned'}` 取**刚召唤的第一张**（refUnit 对 summoned 返回 list[0]），
    //   正好是卡面说的"其中一张"；`OPS.move` **自带"若可能"**（对手占着前线 / 已满就静默跳过）。
    { re: /(?:若可能[，,]?\s*)?将(?:其|其中(?:一|1)张|这些牌?中(?:的)?(?:一|1)张)\s*加入前线/,
      build: () => ({ op: 'move', to: 'frontline', target: { sel: 'ref', ref: 'summoned' } }) },
    // ★★ 「并使其获得+Y+Z」——"其"指**刚召唤的那张**（不是"自己"）
    //   ⚠ 必须排在下面通用的「获得+Y+Z → target:'self'」**之前**，否则会加成自己 ✗
    { re: /^并?使(?:其|之|该单位|这些单位中(?:的)?(?:一|1)张)\s*获得\s*\+([一二两三四五六七八九十\d]+)\s*\+([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: { sel: 'ref', ref: 'summoned' } }) },
    // ★★ 「并使其与一个随机敌方单位战斗」（2026-09-22 制作者）——
    //   攻击方 = **刚召唤出来的那张**（srcRef:'summoned'），被打的 = **随机敌方单位**。
    //   引擎 OPS.fight 已支持 srcRef 指定攻击方（不写就用效果来源，原行为不变）。
    { re: /^(?:并)?使(?:其|之|该单位)\s*与\s*(?:一个|1\s*个)?\s*(?:随机)?\s*(?:敌方|对方)?\s*单位\s*战斗/,
      build: () => ({ op: 'fight', target: { sel: 'random', side: 'enemy' }, srcRef: 'summoned' }) },
    // ★★ 「（并）使其获得/具有<词条>」——"其" = **刚加入战场/阵线的那张**（加场动作之后接的段）。
    //   「将一张X加入支援阵线，使其具有闪击」——以前"其"没回指，退化成"玩家选一个友方单位" ✗。
    //   引擎侧：deckToField/handToField 现在会把新单位写进 ctx.summoned（与 summon 一致）。
    { re: /^并?使(?:其|之|该单位)\s*(?:获得|具有|得到)\s*[“"「]?([\u4e00-\u9fa5A-Za-z]{2,4})[”"」]?\s*$/,
      build: (m) => KW_CN[m[1]] ? { op: 'grant', keyword: KW_CN[m[1]], target: { sel: 'ref', ref: 'summoned' } } : null },
    // ★★ 「对（敌方）总部造成等同于<某方>明牌数的伤害」（2026-09-24 制作者）→ 动态数值 damageHQ。
    //   明牌 = 手牌里 revealed 的牌（情报/明牌词条）；"某方"没写默认**敌方**（卡面口径）。
    //   引擎侧：num() 新增 revealedCount 取值。⚠ 放 LONGTAIL4：通用「造成N点伤害」认不了"等同于…"。
    { re: /造成\s*等同于\s*(敌方|对方|友方|我方)?\s*明牌数?\s*的\s*伤害/,
      build: (m) => ({ op: 'damageHQ', side: 'enemy',
        amount: { revealedCount: /友|我/.test(m[1] || '') ? 'self' : 'enemy' } }) },
    // ★★「下一个加入战场的（友方）单位获得X（并失去Y）」（2026-09-25 制作者铁律改版）：
    //   ★ 卡面**没写**「部署：」前缀 → **常驻光环**（passive aura）：来源在场期间，
    //     友方（或敌方）全体持续获得词条并剥除另一词条；部署/召唤/拉进场一视同仁
    //     （recomputeAuras 每轮重放，来源离场/阵亡由 auraKws/auraRmKws 记账自动收回）。
    //     「制作者口径：只要卡面没写『部署：』，就一定是光环效果」——
    //     09-24 effects.js 光环注释（aura.removeKeyword）即为此预留的引擎能力。
    //   ★ 显式写了「部署：下一个…」→ 保持 **once 总部附魔**（一次性）语义。
    //     "自身不算"由 runTrigger 的 hqEnchant 派发排除（ench.source===ev.source）。
    //     ★ 关键前提：summon / deckToField / handToField 拉进场的单位也要广播 unitDeployed
    //       （卫星计划→332 不贴守护的真凶），已在 effects.js 三个进场 op 里补上。
    { re: /^(?:使)?下一个加入战场的(?:的)?(友方|我方|敌方|对方)?\s*单位\s*获得\s*[“"「]?([\u4e00-\u9fa5A-Za-z]{2,4})[”"」]?(?:\s*(?:并|且)\s*失去\s*[“"「]?([\u4e00-\u9fa5A-Za-z]{2,4})[”"」]?)?\s*$/,
      build: (m, opts) => {
        const sideWord = m[1] || '';
        const kw1 = KW_CN[m[2]], kw2 = m[3] ? KW_CN[m[3]] : null;
        if (!kw1 || (m[3] && !kw2)) return null;    // 认不得的词条 → 交回别的规则/报未实现
        // ★ 无显式「部署：」→ 常驻光环（友方默认含自己；侧别从卡面捕获，没写=友方）
        if (!(opts && opts.explicitTrig)) {
          const aura = { target: { sel: 'all', side: /敌|对方/.test(sideWord) ? 'enemy' : 'friendly' }, keyword: kw1 };
          if (kw2) aura.removeKeyword = kw2;
          return { _standaloneEffect: { trigger: 'passive', aura: aura } };
        }
        // 显式「部署：」→ 一次性总部附魔（once）
        const acts = [{ op: 'grant', keyword: kw1, target: { sel: 'ref', ref: 'eventUnit' } }];
        if (kw2) acts.push({ op: 'removeKeyword', keyword: kw2, target: { sel: 'ref', ref: 'eventUnit' } });
        // ⚠ 返回**单个动作对象**：这条规则所在批次不展平数组（返回 [x] 会产出嵌套 [[x]]）
        return { op: 'hqEnchant',
          name: '下一个加入战场的友方单位获得' + m[2] + (m[3] ? '并失去' + m[3] : ''),
          side: 'self', once: true,
          effects: [{ trigger: 'unitDeployed', actions: acts }] };
      } },
    // ★★ 「控制被攻击单位 / 夺取被攻击单位的控制权」（afterAttack 语境，2026-09-22 制作者）
    //   目标 = 事件里的**防守方**（refUnit 'defender' 读 ev.defender）。
    //   引擎 OPS.takeControl 把它从原拥有者板上搬到效果主人板上。
    //   同句后续「该单位」的指代由 P.parse 拆句循环的 pronounOverride 改指 defender。
    { re: /^(?:夺取|控制|获得)\s*(?:对)?\s*(?:被攻击|受到攻击)(?:的)?(?:那(?:个|名))?\s*单位的?\s*(?:控制权)?$/,
      build: () => ({ op: 'takeControl', target: { sel: 'ref', ref: 'defender' } }) },
    // ★★ 「消灭一个单位，**其所有者**抽N张牌」（2026-09-22 制作者）——
    //   "其" = 被消灭的那个单位 → 抽牌的是**那个单位的拥有者**（可能是自己也可能是对手）。
    //   side:{ofTarget:0} = 取本效果第 1 个选靶目标的拥有者（引擎 OPS.draw 已支持）。
    { re: /^(?:并)?其所有者\s*(?:抽|抓)\s*([一二两三四五六七八九十\d]+)\s*张(?:牌|卡)/,
      build: (m) => ({ op: 'draw', count: num(m[1]), side: { ofTarget: 0 } }) },
    // ★ 「完全修复（所有/全部）<阵营>单位」→ healAll（大数值 = 回满）
    //   制作者 2026-09-22 报「友方回合结束时，完全修复所有友方单位」被判"未实现"。
    { re: /完全修复\s*(?:所有|全部)?\s*(友方|我方|敌方|对方)?\s*单位/,
      build: (m) => ({ op: 'healAll', amount: 999,
        target: { sel: 'all', side: /敌|对方/.test(m[1] || '') ? 'enemy' : 'friendly' } }) },
    // ★ 「获得重甲N / 轻甲N …（，直至本回合结束）」→ **带数值**的词条（水箱坦克「被攻击时先获得重甲1」）。
    //   必须排在下面通用「获得/具有 词条」grant **之前**：那条只捕 2-8 个中文字，
    //   「获得重甲」先命中即 break → 数值 1 丢失、整句吃歪。
    //   尾巴「直至本回合结束」一并吃掉（水箱坦克卡面原文；手写层 grant 也未做临时性）。
    { re: /(?:先)?获得\s*(重甲|轻甲|磁反应装甲|海绵装甲|强磁护盾)\s*([一二两三四五六七八九十\d]+)(?:\s*[，,]?\s*直至?本回合结束)?\s*$/, build: (m) => ({ op: 'grant', keyword: KW_CN[m[1]], value: num(m[2]), target: 'self' }) },
    // ⚠「获得（一次）+N+N」：卡面常写「每有一个友方单位，获得**一次**+1+1」——"一次"是次数描述，
    //   由 0.6)「每有」前缀包成 count×times，这里必须容错吞掉，否则整句 unimplemented（2026-09-24 制作者报）。
    { re: /获得\s*(?:一|1)\s*次?\s*\+([一二两三四五六七八九十\d]+)\s*\+([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: 'self' }) },
    { re: /获得\s*\+([一二两三四五六七八九十\d]+)\s*\+([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: 'self' }) },
    { re: /获得\s*(?:一|1)\s*次?\s*\+([一二两三四五六七八九十\d]+)\s*攻击力/, build: (m) => ({ op: 'buff', attack: num(m[1]), target: 'self' }) },
    { re: /获得\s*\+([一二两三四五六七八九十\d]+)\s*攻击力/, build: (m) => ({ op: 'buff', attack: num(m[1]), target: 'self' }) },
    { re: /获得\s*(?:一|1)\s*次?\s*\+([一二两三四五六七八九十\d]+)\s*防御力/, build: (m) => ({ op: 'buff', defense: num(m[1]), target: 'self' }) },
    { re: /获得\s*\+([一二两三四五六七八九十\d]+)\s*防御力/, build: (m) => ({ op: 'buff', defense: num(m[1]), target: 'self' }) },
    { re: /(?:获得|具有|得到)\s*(?:词条)?\s*[“"「]?([\u4e00-\u9fa5A-Za-z\-]{2,8})[”"」]?/, build: (m) => ({ op: 'grant', keyword: m[1] }) },
    // ★ 「双方抽N张牌」→ 两条 draw（自己 + 对手）（胁迫 / 停火）。
    //   必须排在下面「抽N张<卡名>」**之前**：否则「双方」两字会被当成卡名捕获。
    //   手写层的同语义写法就是两条 draw，行为一致。
    //   ⚠ ACTIONS 路径 build 返回**数组**没被展平（LONGTAIL4 才支持）→ 这条放 ACTIONS 会产出
    //   嵌套数组 actions。已移到 LONGTAIL4 区，这里只留注释防再犯。
    // ★ 「抽N张<卡名>」→ 从**自己卡组**里取指定名字的卡（不是"抽 N 张任意牌"）。
    //   必须排在下面的通用「抽N张」之前。卡名位置出现动作词/代词时返回 null，交给通用规则 ——
    //   否则「抽两张牌并使其获得闪击」会被误判成"抽两张『牌并使其获得闪击』"。
    //   落点：effects.js 的 addCardToHand 支持 a.from === 'deck'（按卡名从卡组里取走）。
    //   ⚠ 动作规则表是"正则命中即 break"，**build 返回 null 不会继续往下试**
    //     （这正是"抽两张牌并使其获得闪击"曾经整句静默丢掉的原因）。
    //     所以排除条件必须写进正则：卡名至少 2 字（排除单字"牌/卡"），
    //     且名字里不能出现动作词/代词（并/使/将/对/获得…）。
    { re: /抽\s*([一二两三四五六七八九十\d]+)\s*张\s*[“"「]?((?![^，,。；;、）)】\s]*[并使得将对获造弃毁])[^，,。；;、）)】\s]{2,12})[”"」]?\s*$/,
      build: (m) => ({ op: 'addCardToHand', name: String(m[2] || '').replace(/[“”"「」]/g, '').trim(),
        _quotedName: /[“"「]/.test(m[0]), count: num(m[1]), side: 'self', from: 'deck' }) },
    { re: /抽\s*([一二两三四五六七八九十\d]+)\s*张/, build: (m) => ({ op: 'draw', count: num(m[1]) }) },
    /* ★ 抑制（2026-10-02 Alan 定义）= silence：失去所有对战词条/效果/属性增益，光环也算效果；
     *   后续可再次贴膜。⚠ 必须排在下面的「压制」规则之前，且**排除"抑制总部"**
     *   （那是 suppressHQ：移除挂在总部上的效果，规则在后面单独认）。 */
    { re: /抑制(?!(?:友方|我方|敌方)?总部)/, build: () => ({ op: 'silence' }) },
    { re: /压制|钉住/, build: () => ({ op: 'pin' }) },
    /* ★ 移除（2026-10-02 Alan 定义）：单位离场、不算被消灭（进 removed 区）。
     *   ⚠ 必须锚定「单位」——「从卡组顶移除15张牌」是 mill，别误吃。 */
    { re: /(?:将|把|使)?\s*(?:一个|一名|该|其|所有)?\s*(?:敌方|友方|我方)?\s*单位\s*移除|(?:移除|放逐)\s*(?:一个|该|其|所有)?\s*(?:敌方|友方|我方)?\s*单位/, build: () => ({ op: 'removeUnit' }) },
    // ★ 「撤退至手牌中」= 自己回手（自身语义；泛化的"撤退"才走下面那条）
    { re: /^(?:本单位?)?撤(?:退|回)(?:至|到|回)?\s*(?:手牌|手中|手里)中?$/, build: () => ({ op: 'retreat', target: 'self' }) },
    { re: /撤退|撤回(?:手牌|手中)|移回手牌/, build: () => ({ op: 'retreat' }) },
    { re: /移动(?:到|至)?\s*(前线|支援阵线|支援线)/, build: (m) => ({ op: 'move', to: /前线/.test(m[1]) ? 'frontline' : 'support' }) },
    // 「加入战场 / 召唤 / 部署到前线」——**必须有卡名或 filter**（由 parseCardOp 负责）。
    //   这里只作兜底：认不出卡名就返回 null（交给别的规则或如实报"缺卡名"），
    //   **绝不产出无卡名的 summon** —— 那会变成"卡池中找不到「undefined」"而静默失效。
    { re: /加入战场|召唤|部署到(?:前线|支援阵线)/, build: () => null },
    { re: /加入手牌|置于手中|放入手中/, build: () => ({ op: 'addCardToHand' }) },
    { re: /洗入卡组/, build: () => ({ op: 'shuffleIn' }) },
    { re: /(?:将)?攻击力(?:设为|变为|降为|减少到)\s*0/, build: () => ({ op: 'setStats', attack: 0 }) },
    // 敌方失去指挥点槽 / 指挥点
    { re: /(?:敌方|对方|对手)失去\s*([一二两三四五六七八九十\d]+)\s*个?指挥点槽/, build: (m) => ({ op: 'loseKreditSlots', amount: num(m[1]), side: 'enemy' }) },
    { re: /(?:敌方|对方|对手)失去\s*([一二两三四五六七八九十\d]+)\s*个?指挥点(?!槽)/, build: (m) => ({ op: 'loseKredits', amount: num(m[1]), side: 'enemy' }) },
    // ★★ 「部署N辆 <体系/卡名>」= 召唤 N 张（2026-09-22 制作者例：「部署两辆 Mk坦克」）
    //   这里的"部署"是**动词**（放上场），不是触发头 —— 触发头是「部署：」**带冒号**，
    //   而这条正则要求"部署"后面紧跟数字，所以不会抢触发句。
    //   体系词交给 resolveCardRef（→ filter:{system:'Mk坦克'}）；普通卡名走 name。
    { re: /^(?:部署|召唤|派出)\s*([一二两三四五六七八九十\d]+)\s*[辆张名架艘个]?\s*([^，,。；;]{1,12}?)\s*$/,
      build: (m) => {
        if (/^(?:前线|支援阵线|支援线|战场)$/.test(m[2].trim())) return null;
        const ref = resolveCardRef(m[2].trim(), {});
        if (!ref) return null;
        const out = { op: 'summon', count: num(m[1]), side: 'self' };
        if (ref.filter) out.filter = ref.filter;
        else if (ref.name) out.name = ref.name;
        else if (ref.cardId) out.cardId = ref.cardId;
        return out;
      } },
    // 将一张「X」加入支援阵线 / 战场 → 召唤
    { re: /将\s*(?:一|1)?\s*张?\s*[“"「]?([^”"」，。]{2,12})[”"」]?\s*加入\s*(?:支援阵线|支援线|战场|前线)/, build: (m) => ({ op: 'summon', name: m[1], count: 1, side: 'self' }) },
    // 「攻击力等同于其防御力」这类互相取值
    { re: /攻击力\s*(?:等?同于|等于|变为|设为|成为|变成)\s*(?:其|该单位的?)?防御力/, build: () => ({ op: 'setStats', attack: 'defense' }) },
    { re: /防御力\s*(?:等?同于|等于|变为|设为|成为|变成)\s*(?:其|该单位的?)?攻击力/, build: () => ({ op: 'setStats', defense: 'attack' }) },
    { re: /攻击力与防御力(?:互换|交换|互调)/, build: () => ({ op: 'setStats', attack: 'defense', defense: 'attack' }) },
    { re: /(?:将)?防御力(?:设为|变为|降为)\s*([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'setStats', defense: num(m[1]) }) },
    { re: /获得\s*([一二两三四五六七八九十\d]+)\s*个?指挥点槽/, build: (m) => ({ op: 'gainKreditSlot', amount: num(m[1]) }) },
    { re: /获得\s*([一二两三四五六七八九十\d]+)\s*个?指挥点(?!槽)/, build: (m) => ({ op: 'gainKredits', amount: num(m[1]) }) },
    { re: /总部获得\s*\+?([一二两三四五六七八九十\d]+)\s*防御力/, build: (m) => ({ op: 'hqMaxUp', amount: num(m[1]) }) },
    { re: /升为老兵|升级为老兵|成为老兵|变成老兵/, build: () => ({ op: 'upgradeSelf' }) },
    { re: /(?:恢复|治疗)\s*([一二两三四五六七八九十\d]+)\s*点/, build: (m) => ({ op: 'heal', amount: num(m[1]) }) },
    // 负面/免疫类常驻规则
    { re: /无法攻击总部|不能攻击总部/, build: () => ({ op: 'grantMod', mod: 'noAttackHQ', target: 'self' }) },
    { re: /无法攻击(?:空军|空中单位)/, build: () => ({ op: 'grantMod', mod: 'noAttackAir', target: 'self' }) },
    /* ★「无法被抑制」= noSuppress（2026-10-02 新增常驻修正）。必须与「无法被压制」分开：
     *   抑制 = silence（失去词条/效果/增益），压制 = pin（钉住）。两个免疫是两种东西。 */
    { re: /无法被?\s*抑制|不会?被抑制/, build: () => ({ op: 'grantMod', mod: 'noSuppress', target: 'self' }) },
    { re: /无法被(?:压制)|不会?被压制/, build: () => ({ op: 'grantMod', mod: 'noPin', target: 'self' }) },
    { re: /不会?受到反击伤害/, build: () => ({ op: 'grantMod', mod: 'noRetal', target: 'self' }) },
    { re: /具有免疫|获得免疫/, build: () => ({ op: 'grantMod', mod: 'immune', target: 'self' }) },
    { re: /无视(?:敌方)?单位效果/, build: () => ({ op: 'grantMod', mod: 'ignoreEnemyEffects', target: 'self' }) },
    // ★ 2026-09-22（制作者报）：「本单位无视指令」必须产出**常驻**（passive + aura），
    //   不能是写在 actions 里的 `grantMod`。原因：`u.mods` **每回合重置**，
    //   而 actions 里的 grantMod 只在**触发那一刻**加一次 → 部署当回合有效、下回合就没了
    //   （产物看着像对的，实际只生效一回合）。
    //   与「友方X单位具有+N攻击力」同一条铁律：**"具有 / 无视" = 常驻 → passive aura**。
    //   aura 支持 `mod` 字段（远征旗舰的"在支援阵线的所有目标具有无视敌方指令"就是这么编的）。
    { re: /无视指令/, build: () => ({ _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'self' }, mod: 'ignoreOrders' } } }) },
    { re: /无视护甲/, build: () => ({ op: 'grantMod', mod: 'pierce', target: 'self' }) },
    { re: /无视(?:对战)?词条/, build: () => ({ op: 'grantMod', mod: 'ignoreCombatKw', target: 'self' }) },
    { re: /受到(?:的)?伤害翻倍/, build: () => ({ op: 'grantMod', mod: 'orderDamageDouble', target: 'self' }) },
    { re: /(?:复制|再获得)一张(?:本单位的)?复制/, build: () => ({ op: 'addCardToHand', self: true, count: 1 }) },
    // 抉择分支里省略了"加入手牌"的写法：「或者一张奋起反抗」
    { re: /^(?:或者?|或)?\s*(?:将)?\s*([一二两三四五六七八九十\d]+)\s*张\s*[“"「]?([^”"」，。]+?)[”"」]?\s*$/, build: (m) => ({ op: 'addCardToHand', name: m[2], count: num(m[1]) }) },
  ];
  // 词条中文名 → 引擎关键字
  const KW_CN = {
    闪击: 'blitz', 固守: 'guard', 守护: 'guard', 烟幕: 'smokescreen', 伏击: 'ambush', 动员: 'mobilize',
    狂怒: 'fury', 奋战: 'valor', 冲击: 'impact', 游击: 'guerrilla', 流亡: 'exile', 老兵: 'veteran',
    重甲: 'armor', 轻甲: 'lightArmor', 硬铝弹: 'tsekep', 反制: 'counter', 隐蔽: 'conceal', 压制: 'pin',
    磁反应装甲: 'magnetic', 强磁护盾: 'shield', 海绵装甲: 'sponge', 收缴: 'confiscate', 打捞: 'salvage',
    情报: 'intel', 维修: 'repair',
  };

  // 只写兵种/类别（"将随机太空战机加入同一阵线"）→ 用 filter 随机取，而不是按卡名找
  //   ⚠ 直接引用上面的 UNIT_TYPES（唯一权威源），不再另抄一份
  const TYPE_WORD_FILTER = UNIT_TYPES;
  const SET_WORD_FILTER = {
    // 英文缩写系列名（卡面直接写 USG / AV76）——与中文系列名同一张表，避免两处不一致
    USG: 'USG', usg: 'USG', AV76: 'av76', Av76: 'av76', av76: 'av76',
    联合国国家: 'UN', 联合国: 'UN', 星盟: '星盟', 协约国: ['USG', 'av76'],
    帝国: 'deran', 帝安: 'deran', 新地: 'av76', 卡尔拉: 'av76',
  };
  /* ──────────────────────────────────────────────────────────────────────
   * 原语：「<阵营><词条>单位」—— 按**词条**筛选的单位选择器
   *   词条 = 兵种词（步兵/坦克/太空/地面…；UNIT_TYPES）
   *        × 战斗词条（奋战/游击/老兵/重甲…；KW_FILTER）
   *        × 系列名（USG/星盟/帝国/协约国…；SET_WORD_FILTER）
   *   写法：友方坦克单位 / 我方步兵单位 / 敌方游击单位 / 友方太空单位 / 场上所有星盟单位
   *   产出：{ side:'friendly', filter:{ unitType:'tank' } } —— side/filter 由组装阶段贴到
   *        动作的 target 上（「一个」→ 选靶声明，「所有」→ 群体选择器）。
   *   ⚠ 词表只有这一份（TRAIT_MAP）：新增词条只在这里加一次，别在各条正则里另抄。
   *   ⚠ 词条写进 filter 时**只认单位身上实际带的引擎关键字**：奋战/狂怒在引擎里已归一为
   *     fury（engine.js kwMap: valor → fury），所以 filter 必须写 fury ——
   *     写 valor 会匹配不到任何单位（空集合、效果静默失效）。
   */
  const KW_FILTER = Object.assign({}, KW_CN, { 奋战: 'fury', 狂怒: 'fury' });
  const TRAIT_MAP = (function () {
    const m = {};
    Object.keys(UNIT_TYPES).forEach(function (k) { m[k] = { unitType: UNIT_TYPES[k] }; });
    Object.keys(KW_FILTER).forEach(function (k) { if (k !== '反制') m[k] = { keyword: KW_FILTER[k] }; });
    Object.keys(SET_WORD_FILTER).forEach(function (k) { m[k] = { setIn: [].concat(SET_WORD_FILTER[k]) }; });
    return m;
  })();
  const TRAIT_ALT = Object.keys(TRAIT_MAP)
    .sort(function (a, b) { return b.length - a.length; })   // 长词优先："太空单位" 必须赢过 "太空"
    .join('|');
  P.KW_FILTER = KW_FILTER;
  P.TRAIT_WORDS = TRAIT_MAP;
  // 词 → filter（去首尾「的」）。查不到返回 null（**不猜**）。
  P.traitFilterOf = function (word) {
    const w = String(word == null ? '' : word).replace(/^的+/, '').replace(/的+$/, '').trim();
    return (w && TRAIT_MAP[w]) ? Object.assign({}, TRAIT_MAP[w]) : null;
  };
  const UNIT_SEL_RE = new RegExp('(友方|我方|自己的?|敌方|对方|对手)\\s*的?\\s*(' + TRAIT_ALT + ')\\s*的?\\s*(?:单位|牌)');
  P.unitSelectorOf = function (text) {
    const m = String(text == null ? '' : text).match(UNIT_SEL_RE);
    if (!m) return null;
    const f = P.traitFilterOf(m[2]);
    if (!f) return null;
    return { side: /敌|对方|对手/.test(m[1]) ? 'enemy' : 'friendly', filter: f, word: m[2], raw: m[0] };
  };
  // 词条限定进 FILTERS。放在既有条目**之后**：Object.assign 是后来者覆盖，
  //   既有的「奋战 → fury」归一不能被这里的生成项改掉。
  Object.keys(KW_FILTER).forEach(function (w) {
    if (w === '反制') return;
    FILTERS.push({ re: new RegExp(w + '(?:单位|的)'), filter: { keyword: KW_FILTER[w] } });
  });
  // 正面动作（决定"没写阵营时"给谁）
  const POSITIVE = { buff: 1, grant: 1, grantAndBuff: 1, heal: 1, gainKredits: 1, gainKreditSlot: 1, draw: 1, hqMaxUp: 1, addCardToHand: 1, summon: 1, shuffleIn: 1 };


  /* ---- 长尾通用句式（第二批） ---- */
  // 「友方（相邻）X具有+N攻击力/防御力」的目标选择器。
  //   ⚠ 「相邻」不能塞进 filter 文本（会变成无效卡名"相邻陆军"），而是 `adjacentTo:'self'`；
  //     兵种词要单独取（「陆军」→ unitType:[infantry,tank,artillery]）。
  const auraTargetOf = function (adj, word) {
    const t = { sel: 'all', side: 'friendly' };
    const f = {};
    const w = String(word || '').replace(/^的/, '').trim();
    // ★ 限定词按**同一份词表**解析：兵种 / 战斗词条 / 系列名 都算。
    //   ⚠ 原来只查 UNIT_TYPES，其它一律当**卡名**：`友方游击单位具有+1攻击力` 被编成
    //     `filter:{name:'游击单位'}` —— 名字里根本没这五个字，光环永远匹配 0 个单位（静默失效）。
    //   ⚠ 捕获到的词**自带「单位」**（"友方坦克单位具有+1攻击力" → w='坦克单位'），
    //     查表前必须先剥掉，否则照样落到"卡名"分支。
    const bare = w.replace(/单位$/, '').replace(/的$/, '').trim();
    const kv = bare ? P.traitFilterOf(bare) : null;
    if (kv) Object.assign(f, kv);
    // ⚠ 泛词排除要判 **w**（原词），不是 bare：「友方单位」bare=''（空串不命中排除集），
    //   曾把 f.name='单位' 当**卡名**编进 filter → matchFilter 按卡名模糊匹配，
    //   没有一张卡名字里带"单位"两个字 → 选靶永远为空、光环/buff 全体静默失效
    //   （2026-09-24 复合充能测试第一次踩出来）。
    else if (w && !/^(?:单位|所有|全体|任意|全部)$/.test(w)) f.name = w;
    if (adj === '相邻') f.adjacentTo = 'self';
    if (Object.keys(f).length) t.filter = f;
    return t;
  };
  const LONGTAIL = [
    // ★★ 「友方（相邻）某类单位具有+N攻击力/防御力」→ **持续光环**（passive aura）。
    //   必须是 `trigger:'passive'` 的独立效果块：由 recomputeAuras 每次重算时应用，
    //   来源单位阵亡/被沉默即失效；**这张牌之后才登场的单位同样吃得到**。
    //   ⚠ 原来走 auraBuff（部署时一次性把加成挂到当时在场的单位上）——
    //     后果：加成只覆盖"部署那一刻已经在场"的单位，之后登场的同类单位没有加成，
    //     而且来源死了加成还留着。这是 Alan 报的「持续性效果原语缺失」的一类根因。
    { re: /友方\s*(相邻|所有|全体|任意)?\s*([\u4e00-\u9fa5]{1,6}?)具有\s*\+([一二两三四五六七八九十\d]+)\s*攻击力/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: auraTargetOf(m[1], m[2]), attack: num(m[3]) } } }) },
    { re: /友方\s*(相邻|所有|全体|任意)?\s*([\u4e00-\u9fa5]{1,6}?)具有\s*\+([一二两三四五六七八九十\d]+)\s*防御力/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: auraTargetOf(m[1], m[2]), defense: num(m[3]) } } }) },
    // 部署：若场上有友方X，获得-N行动花费
    { re: /若场上有?友方([\u4e00-\u9fa5]{1,6})[，,]?\s*获得\s*-([一二两三四五六七八九十\d]+)\s*行动花费/, build: (m) => ({ op: 'condOpCost', filter: m[1], amount: -num(m[2]) }) },
    // 部署：-N-N，直至下一友方回合开始
    { re: /-([一二两三四五六七八九十\d]+)\s*-([一二两三四五六七八九十\d]+)\s*[，,]?\s*直至下一(?:个)?友方回合开始/, build: (m) => ({ op: 'debuffTemp', attack: num(m[1]), defense: num(m[2]) }) },
    // 部署：结束回合
    { re: /^结束回合$/, build: () => ({ op: 'endTurnNow' }) },
    // 下一回合敌方失去N个指挥点（槽）
    { re: /下一回合敌方失去([一二两三四五六七八九十\d]+)个?指挥点槽?/, build: (m) => ({ op: 'nextTurnKredits', amount: -num(m[1]), side: 'enemy' }) },
    // 移除所有友方单位
    { re: /移除所有友方单位/, build: () => ({ op: 'destroyAll', target: { sel: 'all', side: 'friendly' } }) },
    // 抑制友方总部 = 移除挂在总部上的效果（引擎 OPS.suppressHQ）
    { re: /抑制(?:友方|我方)?总部/, build: () => ({ op: 'suppressHQ', side: 'self' }) },
    // （「使总部获得加N防御力」已由 LONGTAIL3 的"总部获得"族统一处理，含友方/敌方/双方）
    // 「或者<卡名>」这样的抉择分支
    { re: /^(?:或者|或)\s*[“"]?([\u4e00-\u9fa5A-Za-z][\u4e00-\u9fa5A-Za-z0-9\.]{1,12})[”"]?$/,
      build: (m) => (poolHasName(m[1]) ? { op: 'addCardToHand', name: m[1], count: 1 } : null) },
  ];

  /* ★★ 外置原语规则（数据驱动）——加新原语**不用改 js**
   *   规则写在 game/data/primitives.json，由 `node game/tools/build-primitives.js`
   *   生成本文件同目录的 primitives-extra.js（内联 g.KG_PRIMITIVES_EXTRA）。
   *   这里把"声明式规则"编译成和内置规则一样的 {re, build}，追加到 LONGTAIL 末尾。
   *
   *   声明式格式（见 primitives.json 的 _说明）：
   *     { id, re, op, args:{字段: "$1" | {"$1":"str"} | {"$1":"map",...} | 常量}, targets:[...] }
   *   "$1" = 第 1 个捕获组，自动 num()（支持中文数字）。 */
  function declToRule(d) {
    if (!d || !d.re || !d.op) return null;
    let re;
    try { re = new RegExp(d.re); } catch (e) { return null; }
    return {
      re: re,
      build: function (m) {
        const val = (v) => {
          if (v == null) return v;
          // {"$1":"map", "敌方":"enemy"} —— 捕获组命中某个词时映射成另一个值
          if (typeof v === 'object' && !Array.isArray(v)) {
            const keys = Object.keys(v);
            const ref = keys.find(k => /^\$\d+$/.test(k));
            if (ref) {
              const got = m[parseInt(ref.slice(1), 10)];
              // {"$1":"str"} = 取捕获组**原样字符串**（"$1" 默认会 num() 转数字，
              //   像「转换为一个USG单位」的 USG 是名字不是数字，必须这么写）
              if (v[ref] === 'str' || v[ref] === 'string') return got;
              const map = v;
              if (got != null && map[got] != null) return map[got];
              // 映射表里没写这个词 → 用 v.$else / v.default，都没有就用原词
              return map.$else != null ? map.$else : (map.default != null ? map.default : got);
            }
            // 普通对象：**递归替换**每一层（支持 filter:{set:"$1"} 这种嵌套参数）
            const o = {};
            Object.keys(v).forEach(k => { if (k.indexOf('$') === 0) return; o[k] = val(v[k]); });
            return o;
          }
          if (typeof v === 'string' && /^\$\d+$/.test(v)) {
            const got = m[parseInt(v.slice(1), 10)];
            return num(got);                       // 自动转数字（中文数字也认）
          }
          return v;
        };
        const act = { op: d.op };
        Object.keys(d.args || {}).forEach(k => { const v = val(d.args[k]); if (v !== undefined) act[k] = v; });
        // ⚠ LONGTAIL 的契约：build 返回**动作对象**本身（调用方读 act.op），
        //   不是 {actions:[...]} —— 返回后者会被再包一层，产出 {actions:[{actions:[...]}]}。
        return act;
      },
    };
  }
  (function () {
    const g = typeof window !== 'undefined' ? window : globalThis;
    const ex = (g.KG_PRIMITIVES_EXTRA && g.KG_PRIMITIVES_EXTRA.rules) || [];
    ex.forEach(d => { const r = declToRule(d); if (r) LONGTAIL.push(r); });
  })();

  P.LONGTAIL = LONGTAIL;


  /* ---- 第三批：让原语组合器完全取代旧专用正则 ---- */
  /* 总部获得类：「使<友方|敌方|双方>总部获得 +N防御力 / N点护甲 / 词条 / “持续效果”」
   *   阵营词 → self（友方/我方）| enemy（敌方/对方）| both（双方/任意，拆成两条动作）
   *   带引号 / 冒号后跟一段"某时…"的文字 → 编译成总部附魔（hqEnchant）
   */
  const HQ_SIDE = { 友方: 'self', 我方: 'self', 自己: 'self', 敌方: 'enemy', 对方: 'enemy', 对手: 'enemy', 双方: 'both', 两方: 'both', 任意: 'both' };
  const HQ_KW_CN = { 免疫: 'immune' };
  function hqSideList(word) {
    const s = HQ_SIDE[String(word == null ? '' : word).replace(/[使是]/g, '')] || 'self';
    return s === 'both' ? ['self', 'enemy'] : [s];
  }
  function hqGainStat(op, word, amount) {
    if (amount == null) return null;
    return hqSideList(word).map(function (sd) { return { op: op, amount: amount, side: sd }; });
  }
  function hqGainKeyword(word, kwCn) {
    const kw = HQ_KW_CN[kwCn] || KW_CN[kwCn] || null;
    if (!kw) return null;
    return hqSideList(word).map(function (sd) { return { op: 'hqKeyword', keyword: kw, side: sd }; });
  }
  // 「使友方总部获得：“友方太空单位部署时，使其行动花费减为0”」→ 交给编译器把引号里的持续效果编译出来
  function hqEnchantFromText(word, inner) {
    inner = String(inner || '').replace(/\s*\n\s*/g, ' ').trim();
    if (!inner) return null;
    const C = root.KG && root.KG.compiler;
    if (!C || !C.compile) return null;
    let sub = null;
    try { sub = C.compile(inner, { cardType: 'order' }); } catch (e) { return null; }
    const effects = ((sub && sub.effects) || []).filter(function (e) { return (e.actions || []).length; });
    if (!effects.length) return null;
    return {
      op: 'hqEnchant',
      name: inner.slice(0, 40),
      side: HQ_SIDE[word || ''] === 'enemy' ? 'enemy' : 'self',
      effects: effects,
    };
  }

  /* 「获得」后面的**并列效果**片段词表（身材 / 词条 / 行动花费 / 无法攻击）。
   *   原语「<阵营><词条>单位 + 并列效果」用它把"获得"后面整串拆成多个动作，
   *   全部指向**同一个目标** —— 「使一个友方坦克单位获得+1+1,奋战和守护」不再只吃掉前半句。 */
  const KW_ALT = Object.keys(KW_CN).sort(function (a, b) { return b.length - a.length; }).join('|');
  // ★ 数值前的符号 **+ / ＋ / "加"** 都认：卡面写「具有**加2攻击力**和冲击」时没有 + 号，
  //   以前只认 `\+N攻击力` → 整句认不出 → 标"未实现"（友方游击单位那张卡）。
  const GRANT_ITEM = '(?:[+＋加]?\\s*[一二两三四五六七八九十\\d]+\\s*[+＋]\\s*[一二两三四五六七八九十\\d]+'
    + '|[+＋加]?\\s*[一二两三四五六七八九十\\d]+\\s*攻击力'
    + '|[+＋加]?\\s*[一二两三四五六七八九十\\d]+\\s*防御力'
    + '|[-−]\\s*[一二两三四五六七八九十\\d]+\\s*点?行动花费'
    + '|行动花费\\s*[+＋\\-−减]\\s*[一二两三四五六七八九十\\d]+'
    + '|无法攻击|' + KW_ALT + ')';
  const GRANT_TAIL = '(' + GRANT_ITEM + '(?:\\s*(?:和|与|及|、|，|,|并)\\s*' + GRANT_ITEM + ')*)';
  // 把「获得…」后面的一个片段变成一条动作（并列增益规则与常驻光环规则共用，避免两份解析漂移）。
  //   返回 null = 这段认不出来（调用方据此**整句放弃**，不猜）。
  function grantItemAct(it, target, opts) {
    const o = opts || {};
    it = String(it || '').replace(/^(?:使其|将其|具有|获得|得到|拥有)/, '').trim();   // 「和具有流亡」这类写法
    let mm;
    if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*[+＋]\s*([一二两三四五六七八九十\d]+)$/))) return { op: 'buff', attack: num(mm[1]), defense: num(mm[2]), target: target };
    if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*攻击力$/))) return { op: 'buff', attack: num(mm[1]), target: target };
    if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*防御力$/))) return { op: 'buff', defense: num(mm[1]), target: target };
    if ((mm = it.match(/^([-−])\s*([一二两三四五六七八九十\d]+)\s*点?行动花费$/))) return { op: o.all ? 'opCostModAll' : 'opCostMod', amount: -num(mm[2]), target: target };
    if ((mm = it.match(/^行动花费\s*([+＋\-−减])\s*([一二两三四五六七八九十\d]+)$/))) return { op: o.all ? 'opCostModAll' : 'opCostMod', amount: (/[+＋]/.test(mm[1]) ? 1 : -1) * num(mm[2]), target: target };
    if (it === '无法攻击') return { op: 'grantMod', mod: 'cannotAttack', target: target };
    if (KW_CN[it]) return { op: 'grant', keyword: KW_CN[it], target: target };
    return null;
  }
  P.grantItemAct = grantItemAct;
  // 光环版片段：身材/行动花费写进 aura 本体；词条必须**单独一条 aura**（aura.keyword 只有一格）。
  function auraItemsOf(items) {
    const stat = {}, keywords = [];
    let ok = true;
    items.forEach(function (it) {
      it = String(it || '').replace(/^(?:具有|获得|得到|拥有)/, '').trim();
      let mm;
      // ★ 同 GRANT_ITEM：符号 + / ＋ / "加" 都认（「加2攻击力」这种没有 + 号的写法）
      if ((mm = it.match(/^[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*攻击力$/))) stat.attack = (stat.attack || 0) + num(mm[1]);
      else if ((mm = it.match(/^[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*防御力$/))) stat.defense = (stat.defense || 0) + num(mm[1]);
      else if ((mm = it.match(/^[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*[+＋]\s*([一二两三四五六七八九十\d]+)$/))) { stat.attack = (stat.attack || 0) + num(mm[1]); stat.defense = (stat.defense || 0) + num(mm[2]); }
      else if ((mm = it.match(/^行动花费\s*([+＋\-−减])\s*([一二两三四五六七八九十\d]+)$/))) stat.opCost = (stat.opCost || 0) + (/[+＋]/.test(mm[1]) ? 1 : -1) * num(mm[2]);
      else if ((mm = it.match(/^([-−])\s*([一二两三四五六七八九十\d]+)\s*点?行动花费$/))) stat.opCost = (stat.opCost || 0) - num(mm[2]);
      else if (KW_FILTER[it]) keywords.push(KW_FILTER[it]);
      else ok = false;
    });
    return ok ? { stat: stat, keywords: keywords } : null;
  }
  P.auraItemsOf = auraItemsOf;
  const LONGTAIL3 = [
    // ── 总部获得类（最具体，先试）─────────────────────────────────
    { re: /^[使是]?\s*(友方|我方|敌方|对方|双方|两方|任意)?\s*总部\s*(?:获得|具有|得到)\s*[：:]?\s*[“"「]([\s\S]+)[”"」]\s*$/,
      build: (m) => hqEnchantFromText(m[1], m[2]) },
    { re: /^[使是]?\s*(友方|我方|敌方|对方|双方|两方|任意)?\s*总部\s*(?:获得|具有|得到)?\s*(?:加|＋|\+)?\s*([一二两三四五六七八九十\d]+)\s*点?\s*防御力/,
      build: (m) => hqGainStat('hqMaxUp', m[1], num(m[2])) },
    { re: /^[使是]?\s*(友方|我方|敌方|对方|双方|两方|任意)?\s*总部\s*(?:获得|具有|得到)?\s*(?:加|＋|\+)?\s*([一二两三四五六七八九十\d]+)\s*点?\s*(?:护甲|装甲)/,
      build: (m) => hqGainStat('hqArmor', m[1], num(m[2])) },
    { re: /^[使是]?\s*(友方|我方|敌方|对方|双方|两方|任意)?\s*总部\s*(?:获得|具有|得到)\s*[：:]?\s*[“"「]?(免疫|闪击|固守|守护|烟幕|伏击|狂怒|奋战|冲击|游击|动员)/,
      build: (m) => hqGainKeyword(m[1], m[2]) },
    // 「造成N点伤害，平均分配至敌方所有目标」→ damageSplit（通用"平均分配"原语，
    //   不是白鲸计划专用）。敌方所有单位参与均分，并把敌方总部也计入分母（evenTo:'hq'）。
    { re: /造成\s*([一二两三四五六七八九十\d]+)\s*点伤害\s*[，,]?\s*平均分配(?:至|给)\s*(?:敌方|对方|对手)?\s*(?:所有)?目标/,
      build: (m) => ({ op: 'damageSplit', total: num(m[1]), target: { sel: 'all', side: 'enemy' }, evenTo: 'hq' }) },
    // ★ 「获得等同于<某单位>的花费的指挥点」——**动态数值**（2026-09-22）
    //   典型：「友方消灭敌方单位时，获得等同于该敌方单位的花费的指挥点」
    //   amount 用 num() 的**对象形式** `{stat:'cost', of:'eventUnit'}`：
    //     refUnit 对 'eventUnit' 是**优先读 ev.deadUnit**（= 刚被消灭的那个单位）→ 取它的原始花费。
    //   ⚠ 必须排在「获得N个指挥点」**之前**（那条只认纯数字，认不了"等同于…花费"）。
    { re: /(?:额外)?获得\s*等同于\s*[^，,。]{0,16}?花费的?\s*指挥点(?!槽)/,
      build: () => ({ op: 'gainKredits', amount: { stat: 'cost', of: 'eventUnit' }, side: 'self' }) },
    // 获得N个指挥点 / 指挥点槽（「指挥额槽」是数据 typo，一并容错）
    { re: /(?:额外)?获得\s*([一二两三四五六七八九十\d]+)\s*个?指挥[点额]槽/, build: (m) => ({ op: 'gainKreditSlot', amount: num(m[1]), side: 'self' }) },
    { re: /(?:额外)?获得\s*([一二两三四五六七八九十\d]+)\s*个?指挥点(?!槽)/, build: (m) => ({ op: 'gainKredits', amount: num(m[1]), side: 'self' }) },
    // 开发一张X（X 用后面的限定条件拼卡池）
    // ⚠ 分隔符必须含**句号**：卡面「开发一张协约国空军。」以句号收尾，
    //   只认逗号的话整句匹配失败 → 落入旧的 parseSentence 兜底 → 产出
    //   `{op:'discover', filter:{}}`（空卡池）+ "需要人工确认"。
    { re: /开发\s*(?:一张|1\s*张)?\s*([^，,。]{0,14}?)(?:[，,。]|$)/, build: (m) => ({ op: 'discoverCard', desc: (m[1] || '').trim() }) },
    // 使[<阵营>][一张|所有]手牌获得-N花费
    //   ⚠ 必须锚定句首：不锚的话「使友方场上、手中、卡组里的所有单位获得减1花费，…+2+2」
    //     会被这条吃掉前半句、其余整段静默丢失。
    //   ★ 2026-09-22（制作者报）：**限定词与阵营都必须读出来**。原来只写死
    //     `count:1, choose:true, side:'self'`，于是「使友方**所有**手牌获得-1花费」被编成
    //     "选择一张手牌减费"（"所有"整段蒸发）、「使**敌方**所有手牌…」还会错改自己的手牌。
    //   ★ 更要紧的是**为什么这条一直没暴露**：原语层认不出"所有"句式 → 0) 复合句拆开后
    //     这一段 `P.parse` 返回 null → allOk=false → 整句退回"碰运气"，
    //     某条原语在句中匹配到后面的动作就 return，**前半句静默消失**。
    //     一个整句的处理边界必须是**句号**，不是"某条操作原语匹配到哪"。
    { re: /^使\s*(友方|我方|敌方|对方|对手|双方|两方)?\s*(一张|1\s*张|所有|全部|全体|每个)?\s*(?:的)?\s*手牌[^，,。]*?获得\s*(-?[一二两三四五六七八九十\d]+)\s*花费/,
      build: (m) => {
        const who = /敌|对方|对手/.test(m[1] || '') ? 'enemy'
          : /双方|两方/.test(m[1] || '') ? 'any' : 'self';
        const all = /所有|全部|全体|每个/.test(m[2] || '');   // ⚠「任意」不算 all（= 玩家随便挑一张）
        const n = num(m[3]);
        const out = {
          op: 'setHandCost', mode: 'reduce', side: who,
          count: all ? 99 : 1,          // 99 = 引擎侧"改到没有可改的为止"
          choose: !all,                 // 只有"一张"才让玩家选
          amount: n > 0 ? -n : n,
        };
        if (!all) out.prompt = '选择一张手牌减费';
        return out;
      } },
    // 「（本牌）具有减N花费」——省写形式，"本牌"常被省略（如「侦察」：若敌方手中具有明牌，具有减2花费）
    //   语义 = 本牌自身减费（`self` + `choose:false`，不需要玩家选 —— 就是指这张打出的卡）
    { re: /^(?:使)?(?:本牌|此牌|这张牌)?\s*(?:具有|获得)\s*(?:减|[-−])\s*([一二两三四五六七八九十\d]+)\s*花费(?!槽)/,
      build: (m) => ({ op: 'setHandCost', mode: 'reduce', amount: -num(m[1]), count: 1, choose: false, side: 'self' }) },
    // 使一个单位获得闪击和行动花费减为0
    { re: /获得闪击和行动花费减为0/, build: () => ({ op: 'grant', keyword: 'blitz' }) },
    { re: /行动花费减为0|行动花费为0/, build: () => ({ op: 'setOpCost', value: 0 }) },
    /* ── A7. 「友方<词条>单位具有 <并列效果>」整句一次处理 ─────────────────
     *   「友方游击单位具有+1攻击力和闪击」原来只吃到「+1攻击力」，**"和"后面的词条整段丢掉**。
     *   这里把"具有"后面的整串按同一套片段词表拆开：身材/行动花费合成一条 aura，
     *   每个词条单独一条 aura（aura.keyword 只有一格）。认不出的片段 → 返回 null 交给别的规则。 */
    { re: new RegExp('^友方\\s*(相邻|所有|全体|任意)?\\s*([\\u4e00-\\u9fa5]{1,6}?)\\s*具有\\s*' + GRANT_TAIL + '$'),
      build: (m) => {
        const spec = auraTargetOf(m[1], m[2]);
        const items = String(m[3] || '').split(/\s*(?:和|与|及|、|，|,|并)\s*/).filter(Boolean);
        const parsed = items.length ? auraItemsOf(items) : null;
        if (!parsed) return null;
        const acts = [];
        if (Object.keys(parsed.stat).length) {
          acts.push({ _standaloneEffect: { trigger: 'passive', aura: Object.assign({ target: spec }, parsed.stat) } });
        }
        parsed.keywords.forEach(function (k) {
          acts.push({ _standaloneEffect: { trigger: 'passive', aura: { target: Object.assign({}, spec), keyword: k } } });
        });
        if (!acts.length) return null;
        return { _compound: true, actions: acts };
      } },
    /* ── 原语：「<阵营><词条>单位 + 并列效果」─────────────────────────────
     *   「使一个友方坦克单位获得+1+1,奋战和守护」
     *     → 玩家选一个**友方坦克** → buff +1+1、grant 奋战、grant 守护（同一个目标）
     *   「使场上所有黑盾单位获得+2+2，行动花费+1」
     *     → 所有名字含「黑盾」的单位 buff +2+2 且行动花费 +1
     *   词条 = 兵种 / 战斗词条 / 系列名（P.traitFilterOf）；查不到再按卡池名兜底（黑盾这类系列词）。
     *   ⚠ 原来这类句子只会命中「获得+N+N」那一条规则，**逗号后面的词条/行动花费整段丢掉**
     *     （deran/command/_22、deran/command/_3、USG/commands/_16 都只剩一半效果）。 */
    { re: new RegExp('^(?:使\\s*)?(?:(所有|全部|全体|场上所有)\\s*)?(?:一[个名辆架艘张]|1\\s*[个名辆艘张])?\\s*'
        + '(友方|我方|自己的?|敌方|对方|对手)?\\s*(' + TRAIT_ALT + '|[\\u4e00-\\u9fa5A-Za-z0-9]{0,6})?\\s*单位\\s*[=＝]?\\s*(?:获得|得到)\\s*'
        + GRANT_TAIL + '$'),
      build: (m) => {
        // 自身语义（本单位/该单位/此单位）不走这条 —— 那是 target:'self'，不是"选一个友方单位"
        if (/本单位|该单位|此单位|自身/.test(m[0])) return null;
        // ★ 「使**其**/之 获得…」是**代词**（指事件主角 / 刚召唤出来的单位）→ 不走这条
        //   （否则会被改写成选靶 t1）。正确路径是「并?使其获得+Y+Z」那条规则。
        if (/^并?使\s*(?:其|之|这些|那些)/.test(m[0])) return null;
        // ⚠ 正则里的「单位」**必须保留必选**（2026-09-22 实测）：把它改成可选会放宽过头，
        //   「使其获得+1+1」以及一批"并列动作"句（监听里的 +2+2 且 无法攻击）都会被这条抢走，
        //   产出"选择一个单位"。体系词那条路径**同样要求写「单位」**
        //   （「使所有 Mk坦克单位 获得+1+1」；这与项目里「使所有黑盾单位获得+2+2」的既有写法一致）。
        const items = String(m[4] || '').split(/\s*(?:和|与|及|、|，|,|并)\s*/).filter(Boolean);
        const all = !!m[1];
        const side = m[2] ? (/敌|对方|对手/.test(m[2]) ? 'enemy' : 'friendly')
                          : (/敌方|对方|对手/.test(m[0]) ? 'enemy' : 'friendly');
        let trait = m[3] ? P.traitFilterOf(m[3]) : null;
        // ★★ 「体系」优先（2026-09-22）：卡面写的是**体系名**（Mk坦克 / 黑盾 / 伞兵…）
        //   → filter:{system:'X'}（匹配该体系旗下所有卡）。
        //   ⚠ 这类写法**后面不必跟"单位"**（「使所有 Mk坦克 获得+1+1」），所以正则里的"单位"
        //     改成了可选；否则整句不匹配，Mk坦克 会被当成"没有筛选条件"→ 加成所有友方 ✗
        if (!trait && m[3]) { const sysW = systemWordIn(m[3]); if (sysW) trait = { system: sysW }; }
        if (!trait && m[3] && poolHasName(m[3])) trait = { name: m[3] };    // 「黑盾单位」这类系列词
        if (!trait) {   // 兜底：整句交给通用 FILTERS 词表（「USG和星盟单位」这种并列系列名）
          const f = {};
          FILTERS.forEach(function (fl) {
            const mm = m[0].match(fl.re);
            if (mm) Object.assign(f, typeof fl.filter === 'function' ? fl.filter(mm) : fl.filter);
          });
          if (Object.keys(f).length) trait = f;
        }
        const target = all ? { sel: 'all', side: side } : 't1';
        const acts = [];
        items.forEach(function (it) {
          const a = grantItemAct(it, target, { all: all });
          if (a) acts.push(a);
        });
        if (!acts.length || acts.length !== items.length) return null;     // 有认不出的片段 → 交给别的规则，不猜
        if (all) {
          if (trait) acts.forEach(function (x) { if (x.target && x.target.sel === 'all') x.target.filter = Object.assign({}, trait); });
          return { _compound: true, actions: acts };
        }
        const declare = [{ id: 't1', side: side, kind: 'unit', filter: trait || undefined,
          prompt: '选择一个' + (side === 'enemy' ? '敌方' : '友方') + '单位' }];
        return { _compound: true, declare: declare, actions: acts };
      } },
    // 「使其获得减1行动花费」：**带代词**时作用于事件主角（否则默认一个友方单位）。
    //   ⚠ 正则必须锚定句首：原来不锚定，会把「使一个友方坦克单位获得-2行动花费」整句吃掉，
    //     产出 {sel:'random', side:'friendly'} —— 词条丢了、指向性还变成随机。
    { re: /^(?:使)?\s*(本单位|此单位|自身|其|它|该单位|该目标)?\s*获得\s*([-−＋+减加])\s*([一二两三四五六七八九十\d]+)\s*点?\s*行动花费/,
      build: (m) => {
        const amount = (/[-−减]/.test(m[2]) ? -1 : 1) * num(m[3]);
        // 「本单位获得减N行动花费」= 自身（target:'self'）；「使其获得…」= 事件主角（_pronoun）
        if (/本单位|此单位|自身/.test(m[1] || '')) return { op: 'opCostMod', amount: amount, target: 'self' };
        return { op: 'opCostMod', amount: amount, target: { sel: 'random', side: 'friendly' }, _pronoun: true };
      } },
    // 「（使其）获得+N+N和无法攻击」= 身材 + 无法攻击（并列增益，别把后半句丢掉）
    //   只认句首的"获得…"（即"使其"已被代词规则剥掉的形式），不影响"使一个友方单位获得+2+2和…"
    { re: /^获得\s*\+([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)\s*和\s*无法攻击/,
      build: (m) => [{ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: 'self', _pronoun: true },
        { op: 'grantMod', mod: 'cannotAttack', target: 'self', _pronoun: true }] },
    // 「使其获得<并列效果>」（代词已在触发式里被剥成"获得…"）→ 同样多段共用一个目标。
    //   ⚠ _needPronoun：只有**事件主角存在**时才用这条；缺主语的「获得+1+1和闪击」该由玩家选目标，
    //     不能错算成"随机一个友方单位"（那种句子仍走下面的常规动作表）。
    { re: new RegExp('^(?:使|令)?\\s*(?:其|它|该单位)?\\s*获得\\s*' + GRANT_TAIL + '$'),
      build: (m) => {
        const items = String(m[1] || '').split(/\s*(?:和|与|及|、|，|,|并)\s*/).filter(Boolean);
        const acts = [];
        items.forEach(function (it) {
          let mm;
          if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*[+＋]\s*([一二两三四五六七八九十\d]+)$/))) acts.push({ op: 'buff', attack: num(mm[1]), defense: num(mm[2]) });
          else if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*攻击力$/))) acts.push({ op: 'buff', attack: num(mm[1]) });
          else if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*防御力$/))) acts.push({ op: 'buff', defense: num(mm[1]) });
          else if ((mm = it.match(/^([-−])\s*([一二两三四五六七八九十\d]+)\s*点?行动花费$/))) acts.push({ op: 'opCostMod', amount: -num(mm[2]) });
          else if ((mm = it.match(/^行动花费\s*([+＋\-−减])\s*([一二两三四五六七八九十\d]+)$/))) acts.push({ op: 'opCostMod', amount: (/[+＋]/.test(mm[1]) ? 1 : -1) * num(mm[2]) });
          else if (it === '无法攻击') acts.push({ op: 'grantMod', mod: 'cannotAttack' });
          else if (KW_CN[it]) acts.push({ op: 'grant', keyword: KW_CN[it] });
        });
        if (!acts.length || acts.length !== items.length) return null;
        acts.forEach(function (x) { x._pronoun = true; });
        return { _needPronoun: true, _compound: true, actions: acts };
      } },
  ];
  P.LONGTAIL3 = LONGTAIL3;

  /* ─────────────────────────────────────────────────────────────────────
   * 第四批：对战修正族 / 花费取值族 / 剩余高频长尾
   *
   * 设计原则（原语组合器铁律）：**能分解成原语词汇的就分解成原语**。
   *   - 「对战X +N攻击力」不是新原语 —— 它是「常驻修正（grantMod）」的一个特例：
   *     mod=vsType, unitType=X, attack=+N。引擎侧早已支持（见 effects.js OPS.grantMod）。
   *     所以这里只是**把卡面中文翻译成已有的 grantMod 词汇**，不新增 op。
   *   - 「获得等同于其防御力的指挥点」同理 —— 是 gainKredits 的 amount 取了个动态值
   *     （{stat:'defense'}），动态取值系统早就在 num() 里实现了。
   */
  // 「对战<兵种>时(具有/造成/伤害) …」的兵种词 → unitType（走同一份 UNIT_TYPES，避免第二套词表）
  function vsTypeFromText(text) {
    const t = String(text || '');
    for (const k of TYPE_KEYS_BY_LEN) if (t.indexOf(k) >= 0) return UNIT_TYPES[k];
    return null;
  }
  const LONGTAIL4 = [
    /* ── 「攻击X单位时将其Y」/「攻击X时将其Y」──
     *   卡面范例：「攻击太空单位时将其压制」（蜂鸟战斗机）。
     *   语义要点（这是**必须**落在原语里的三条，否则自动解析与运行时不一致）：
     *     ① 「将其」的"其"= **被攻击的那个单位**（defender），不是让玩家选靶。
     *        早期实现把本句型落到通用选靶，产出 {side:'any'} 的声明式目标 →
     *        打出后弹出"选择一个单位"，玩家可以选中**自己的**单位，
     *        表现为"攻击时压制了自己的太空单位"。这是用户实际报到的 bug。
     *     ② 「攻击X时」是**条件**：只有 defender 属于 X 时才触发。
     *        落到条件原语 defenderIsType（引擎侧读 ctx.event.defender + KG.isType）。
     *     ③ 动作词用同一张「压制/消灭/伤害…」小词表，压在 conditional.then 里。
     *
     *   ⚠ 必须排在「对战X时…」族**之前**：那些规则以「对战」开头，本句型以「攻击」开头，
     *     两者不冲突；但本句型会吃走"攻击…时"前缀，必须早于通用选靶流程。 */
    { re: /^攻击\s*[“"「]?([\u4e00-\u9fa5]{1,6}?)(?:单位)?[”"」]?\s*时\s*(?:将|把|使)?\s*(?:其|它)\s*(压制|抑制|钉住|撤退|消灭|摧毁)/,
      build: (m) => {
        const t = vsTypeFromText(m[1]);
        if (!t) return null;
        const actWord = m[2];
        let thenAct;
        if (/压制|钉住/.test(actWord)) thenAct = { op: 'pin', turns: 1, target: { sel: 'ref', ref: 'defender' } };
        else if (/抑制/.test(actWord)) thenAct = { op: 'silence', target: { sel: 'ref', ref: 'defender' } };   // ★ 2026-10-02：抑制≠压制
        else if (/撤退/.test(actWord)) thenAct = { op: 'retreat', target: { sel: 'ref', ref: 'defender' } };
        else thenAct = { op: 'destroy', target: { sel: 'ref', ref: 'defender' } };
        return {
          op: 'conditional',
          condition: { op: 'defenderIsType', type: t },
          then: [thenAct],
        };
      } },
    /* ── A0. 「场上所有友方单位获得减N行动花费和+M攻击力（，直至本回合结束）」──
     *   一个 buff 同时减行动花费 + 加攻击力（总攻），duration 由"直至本回合结束"决定。 */
    { re: /^场上所有?友方单位\s*(?:都)?获得\s*(?:减|[-−])\s*([一二两三四五六七八九十\d]+)\s*点?行动花费\s*和\s*\+?([一二两三四五六七八九十\d]+)\s*攻击力[，,]?\s*(直至(?:本|该)?回合结束)?/,
      build: (m) => ({ op: 'buff', attack: num(m[2]), opCostMod: -num(m[1]), duration: m[3] ? 'turn' : undefined, target: { sel: 'all', side: 'friendly' } }) },
    /* ── 「将其移至前线 / 支援阵线」──
     *   卡面范例：「友方单位部署时，将其移至前线，并使其获得+2+2」——「其」= 事件主角。 */
    { re: /^[将把]\s*(?:其|该单位|它)\s*(?:移|移动|移上|移往)(?:至|到|上)?\s*(前线)/,
      build: () => ({ op: 'move', to: 'frontline', _pronoun: true }) },
    { re: /^[将把]\s*(?:其|该单位|它)\s*(?:移|移动|移回|退回)(?:至|到|回)?\s*(支援阵线|支援线)/,
      build: () => ({ op: 'move', to: 'support', _pronoun: true }) },
    /* ── 「将其压制并抑制」──
     *   抑制 = **沉默**（制作者 2026-09-20 定义：失去对战词条/特效/获得的增益）；
     *   压制 = pin。两个动作指向同一个目标（事件主角）。 */
    { re: /^[将把]\s*(?:其|该单位|它|该目标)\s*(?:压制|抑制|钉住)\s*并\s*(?:压制|抑制|钉住)/,
      build: () => [{ op: 'pin', _pronoun: true }, { op: 'silence', _pronoun: true }] },
    /* ── 「将其复制到友方手牌中」──
     *   把上一步 chooseHandCard **选中的那张**（可能在对手手里）复制一份进我的手牌。
     *   复制后把 ctx.chosenHandInst 指向**复制体**，后面的「然后将其转换为X」才会作用在复制体上。 */
    { re: /^[将把]\s*(?:其|该牌|这张牌|该卡牌)\s*(?:复制|拷贝)(?:一份)?到\s*(?:友方|我方|你的)?\s*手牌中?/,
      build: () => ({ op: 'copyHandCard', from: 'chosen', side: 'self', sel: 'chosen' }) },
    /* ── 「敌方随机弃一张牌，并将该牌的复制加入手中」──
     *   前半句 OPS.discard 会把"刚弃掉的那张"打点，后半句 copyLastDiscarded 复制它。 */
    { re: /^(?:敌方|对方|对手)?\s*随机弃\s*(?:一|1)?\s*张牌[，,]?\s*(?:并|然后)?\s*(?:将|把)该牌的?复制\s*(?:加入|放到|置于)\s*(?:我方|友方|你的)?\s*(?:手牌|手中)/,
      build: () => [{ op: 'discard', count: 1, mode: 'random', side: 'enemy' }, { op: 'copyLastDiscarded', side: 'self' }] },
    { re: /^(?:敌方|对方|对手)?\s*随机弃\s*(?:一|1)?\s*张牌\s*$/,
      build: () => ({ op: 'discard', count: 1, mode: 'random', side: 'enemy' }) },
    /* ── 「使卡组顶的单位获得+N攻击力和<词条>，并将其加入战场（，如果可能，加入前线）」──
     *   deckToField 已支持内联 keyword + buff（星盟/c/-15 同款），这里只做句式→op 的翻译。 */
    { re: /^[使是]?\s*(?:卡组|牌库)顶\s*(?:的)?\s*(?:单位|那张牌|卡牌)\s*获得\s*\+([一二两三四五六七八九十\d]+)\s*攻击力\s*和\s*([\u4e00-\u9fa5]{2,4})[，,]?\s*(?:并|然后)?\s*(?:将|把)其加入战场/,
      build: (m) => ({ op: 'deckToField', to: 'frontline', ifPossible: true, keyword: KW_FILTER[m[2]] || m[2], buff: { attack: num(m[1]) } }) },
    /* ── 「位于支援阵线的所有友方目标具有无视敌方指令」──
     *   常驻修正做成**光环**（aura.mod）：来源阵亡/被沉默即失效，recomputeAuras 每轮重放。 */
    { re: /^[使是]?\s*(友方|我方)?\s*(?:位于|在)\s*(支援阵线|支援线|前线)\s*(?:的)?\s*(?:所有)?\s*(?:友方|我方)?\s*目标\s*具有\s*(无视敌方指令|无视指令|不受指令影响|无法被指令指向)/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: {
        target: { sel: 'all', side: 'friendly', zone: /前线/.test(m[2]) ? 'frontline' : 'support' },
        mod: /无法被指令指向/.test(m[3]) ? 'immuneOrder' : 'ignoreOrders' } } }) },
    /* ── 「场上每有一个<词条>，本单位具有减N行动花费」── 动态 opCost 光环 */
    { re: /^(?:场上)?每有\s*(?:一|1)?\s*(?:个|张|名|辆|架|艘)?\s*([\u4e00-\u9fa5A-Za-z]{1,6}?)\s*(?:单位)?\s*[，,]?\s*(?:本单位|该单位|此单位|这张卡)\s*具有\s*(?:减|[-−])\s*([一二两三四五六七八九十\d]+)\s*行动花费/,
      build: (m) => {
        const w = String(m[1] || '').trim();
        const f = P.traitFilterOf(w) || P.typeFilterFromText(w) || latinTraitFilter(w);
        if (!f) return null;
        return { _standaloneEffect: { trigger: 'passive', aura: {
          target: { sel: 'self' }, opCost: { count: { sel: 'all', side: 'friendly', filter: f }, times: -num(m[2]) } } } };
      } },
    /* ── 「将其返回手牌」──
     *   卡面范例：「友方单位部署时，将其返回手牌」——「其」= **刚部署的那个单位**（事件主角）。
     *   ⚠ 不能落到通用选靶：那会弹出"选择一个单位"，玩家可以选中自己别的单位。
     *   触发前缀已由编译器把 eventRef 传进来（unitDeployed → eventUnit），_pronoun 在本层被替换。 */
    { re: /^[将把]\s*(?:其|该单位|该卡牌|它|该目标)\s*(?:返回|移回|送回|收回|撤退回?)\s*(?:到)?\s*手牌/,
      build: () => ({ op: 'returnToHand', _pronoun: true }) },
    /* ── 「从<阵营>N张手牌中选择一张」+「将其转换为“X”」──
     *   卡面范例：「从敌方三张手牌中选择一张，将其转换为“补给”；将一张复制加入手牌。」
     *   逗号把两句拆开各自解析：第一条 → chooseHandCard（count=N = 拿 N 张候选），
     *   第二条 → transformHandCard（目标 = 上一条选中的那张，引擎按 ctx.chosenHandInst 取）。
     *   ⚠ 转换目标必须是**卡名**（引号里，或确实命中卡池的名字）；「转换为一个USG单位」这类
     *     兵种描述由 LONGTAIL3 的「转换为一个X单位」规则负责，别在这里抢（抢了会拿它当卡名）。 */
    { re: /^从\s*(敌方|对方|对手|友方|我方)?\s*(?:的)?\s*([一二两三四五六七八九十\d]+)\s*张手牌中\s*(?:选择|挑选|选|抽取|查看)\s*(?:其中\s*)?(?:一|1)\s*张/,
      build: (m) => ({
        op: 'chooseHandCard',
        side: (m[1] && /敌|对方|对手/.test(m[1])) ? 'enemy' : 'self',
        count: num(m[2]),
        prompt: '从' + (m[1] || '') + '手牌中选择一张',
      }) },
    { re: /^[将把]\s*其\s*(?:转换|变化|变)\s*(?:为|成)\s*[“"「]([^”"」,，。；;]{1,12})[”"」]/,
      build: (m) => ({ op: 'transformHandCard', target: { sel: 'ref', ref: 'chosenHandCard' }, name: m[1].trim() }) },
    { re: /^[将把]\s*其\s*(?:转换|变化|变)\s*(?:为|成)\s*([\u4e00-\u9fa5A-Za-z][\u4e00-\u9fa5A-Za-z0-9\.]{0,11})\s*$/,
      build: (m) => (poolHasName(m[1].trim()) ? { op: 'transformHandCard', target: { sel: 'ref', ref: 'chosenHandCard' }, name: m[1].trim() } : null) },
    // ── 复合规则（_compound：多动作 + 自带选靶声明，用于代词跨动作指代）──
    // 「使一个单位获得闪击和行动花费减为0」→ grant + setOpCost 指向同一个选靶
    { re: /^使\s*(?:一|1)?\s*(?:个|名|辆|架|艘)?\s*单位\s*获得\s*(闪击|固守|守护|烟幕|伏击|奋战|狂怒|游击|冲击)\s*和\s*行动花费减为0/,
      build: (m) => ({ _compound: true, declare: [{ id: 't1', side: 'any', kind: 'unit', prompt: '选择一个单位' }],
        actions: [{ op: 'grant', keyword: KW_CN[m[1]], target: 't1' }, { op: 'setOpCost', value: 0, target: 't1' }] }) },
    // 「抑制一个友方单位，然后使其获得"友方总部具有免疫"」→ pin + hqImmune 指向同一目标
    { re: /^抑制一个友方单位[，,]?\s*(?:然后)?[使令]?其获得\s*[“"「]?友方总部具有免疫[”"」]?/,
      build: () => ({ _compound: true, declare: [{ id: 't1', side: 'friendly', kind: 'unit', count: 1, prompt: '选择一个友方单位' }],
        actions: [{ op: 'pin', target: 't1', turns: 1 }, { op: 'grantMod', mod: 'hqImmune', target: 't1' }] }) },
    // 「（指向一个单位，）每有一个<敌方|友方>单位使该单位获得+N+M」→ 动态数值（数量 × N/M）
    //   落点：数值表达式 {count:{side}} 由引擎的 num() 求值（effects.js）。
    //   ⚠ 只支持"+1+1"这种"每个 +1"的写法：引擎的动态数值原语 {count:{side}} 直接取数量，
    //     无法再乘一个系数（"每有一个 +2+2"需要引擎扩展，先不硬猜）。
    { re: /每有一个\s*(敌方|对方|友方|我方)\s*单位[，,]?\s*[使令]?\s*(?:该|此|其)?单位?\s*获得\s*\+1\s*\+1/,
      build: (m) => {
        const side = /敌|对方/.test(m[1]) ? 'enemy' : 'friendly';
        return { _compound: true, declare: [{ id: 't1', side: 'any', kind: 'unit', count: 1, prompt: '选择一个单位' }],
          actions: [{ op: 'buff', target: 't1', attack: { count: { side: side } }, defense: { count: { side: side } } }] };
      } },
    /* ── 「每有一张"X"，对战Y时具有+N攻击力」（卫星联盟通用驱逐舰）──
     *   ⚠ 引擎**没有**"按某张卡的数量缩放攻击力"的原语：vsType 的 attack 只在挂载时求值一次，
     *     动态值 num() 只能取场上单位数 / 单位属性，数不了"手里或卡组里有几张某卡"。
     *   所以这里落成**每张 +N 的静态形态**（与手写层 data/effects 里那张卡的写法一致），
     *   并标 uncertain → 编译器会写「需人工确认」，绝不假装完整。要真做，得先在引擎加原语。 */
    { re: /^每有\s*(?:一|1)?\s*张\s*[“"「]?([^”"」]{1,20})[”"」]?\s*[，,]?\s*对战\s*[“"「]?(步兵|坦克|火炮|炮兵|战斗机|轰炸机|太空战机|巡地舰|巡航舰|工事|建筑|空军|陆军|太空单位|太空)[”"」]?\s*(?:单位)?(?:时)?\s*(?:具有|造成|获得|伤害)?\s*[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*(?:点)?(攻击力|伤害)/,
      build: (m) => {
        const t = vsTypeFromText(m[2]);
        if (!t) return null;
        /* ★★ 改成**真常驻**（2026-09-26 制作者：「星盟通用大驱应该是常驻特效」）：
         *   engine.js attackPowerAgainst 的 addUp() **支持动态值** {count:spec,times:N}
         *   —— 每次攻击时现算「场上有几张学名卡」，所以「每有一张」是精确的、不是一次近似；
         *   而且写 passiveRules 不受「summon/deckToField 不派发 deploy」这个坑影响。
         *   旧写法 deploy+grantMod 只在挂载那一刻求值一次，且直接进场的副本吃不到。 */
        const rule = {};
        rule[(m[4] === '伤害') ? 'damage' : 'attack'] = {
          count: { sel: 'all', side: 'friendly', filter: { nameIncludes: m[1] } },
          times: num(m[3]),
        };
        const pr = { vsType: {} };
        pr.vsType[t] = rule;
        return { _standaloneEffect: { trigger: 'passive', passiveRules: pr } };
      } },
    /* ── A. 对战修正族（攻击方视角：打某类目标时攻击力/伤害变化）──
     *    「对战太空单位时具有+1攻击力」「对战重甲单位具有+4攻击力」
     *    「对战坦克时伤害加1」「对陆军和总部造成的伤害+2」
     *    「对战步兵伤害翻倍」
     *    —— 全部落到已有的 grantMod(mod=vsType)，只是把中文翻成统一词汇。
     *    兵种词用**词表枚举**（不是贪婪的 [\u4e00-\u9fa5]{1,6}），否则「坦克时伤害加1」
     *    会被整段吃走，导致类型识别失败。 */
    // ★ 「双方抽N张牌」→ 两条 draw（自己 + 对手）（胁迫 / 停火）。
    //   放 LONGTAIL4（先于 ACTIONS 的「抽N张<卡名>」执行）：「双方」两字否则会被当成卡名捕获。
    //   ⚠ 不能放 ACTIONS：ACTIONS 路径 build 返回**数组**没被展平（会产出嵌套数组 actions）。
    { re: /双(?:方|边)\s*抽\s*([一二两三四五六七八九十\d]+)\s*张(?:牌|卡)?/, build: (m) => [{ op: 'draw', count: num(m[1]), side: 'self' }, { op: 'draw', count: num(m[1]), side: 'enemy' }] },
    // ★ 「对双方总部造成N点伤害」→ 两条 damageHQ（自己 + 敌方）（迫在眉睫）。
    //   必须在 ACTIONS 的通用「对X总部造成N伤害」之前（LONGTAIL4 先执行）——
    //   否则「双方」被丢掉，只打敌方总部（hq 血量差 4，difftest 实锤）。
    { re: /对\s*双方\s*总部\s*造成\s*([一二两三四五六七八九十\d]+)\s*点伤害/,
      build: (m) => [{ op: 'damageHQ', side: 'self', amount: num(m[1]) }, { op: 'damageHQ', side: 'enemy', amount: num(m[1]) }] },
    // ★ 「将N张X加入双方手牌(中)」→ 两条 addCardToHand（自己 + 对手）（炸弹战争）。
    //   ⚠⚠ 此规则**不会生效**：parseCardOp（0.65，早于 LONGTAIL4）先吃「将N张X加入手牌」，
    //   「双方」已改在 parseCardOp 返回前识别。留注释防再犯。
    // ★ 「对战<兵种>（单位）（时）具有伏击」→ 常驻 passiveRules.ambushOnlyVs（FC-33战斗机）。
    //   伏击默认对"能反击的目标"触发；限定兵种必须写进 ambushOnlyVs（engine.js 对战结算读它，
    //   支持 'air' 或任意兵种词、支持数组）。⚠ 不能落进通用「获得/具有 词条」grant ——
    //   那会变成"无条件获得伏击"（对战非空军也吃伏击），限定整段丢失。
    //   必须放在下面「对战X时具有+N攻击力」**之前**（同前缀，先认词条变体）。
    //   词表枚举（同上）：认不出/不是伏击 → 正则不匹配，交给后续规则，绝不硬猜。
    { re: /^对战\s*[“"「]?(步兵|坦克|火炮|炮兵|战斗机|轰炸机|太空战机|巡地舰|巡航舰|工事|建筑|空军|陆军|太空单位|太空)[”"」]?\s*(?:单位)?(?:时)?\s*具有\s*伏击\s*$/,
      build: (m) => {
        const t = (m[1] === '空军') ? 'air' : vsTypeFromText(m[1]);
        if (!t) return null;
        return { _standaloneEffect: { trigger: 'passive', passiveRules: { ambushOnlyVs: t } } };
      } },
    { re: /^对战\s*[“"「]?(步兵|坦克|火炮|炮兵|战斗机|轰炸机|太空战机|巡地舰|巡航舰|工事|建筑|空军|陆军|太空单位|太空|重甲单位|装甲单位|重甲|装甲|护甲)[”"」]?\s*(?:单位)?(?:时)?\s*(?:具有|造成|获得|伤害)?\s*[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*(?:点)?(攻击力|伤害)/,
      build: (m) => {
        const w = String(m[1]);
        // ★ 「攻击力」与「伤害」是**两码事**：攻击力提升单位数值（会被"攻击力不小于X"之类读到），
        //   伤害加成只在结算伤害时 +N。以前这条规则把两者都写成 attack，
        //   「对战坦克时伤害加1」被落成了 +1攻击力（USG第775团，用户报的错）。
        const key = (m[3] === '伤害') ? 'damage' : 'attack';
        // 「重甲单位」不是兵种，而是"带护甲的单位" → 引擎已有 vsArmor（按目标护甲>0 时加攻）
        if (/重甲|装甲|护甲/.test(w)) return { op: 'grantMod', mod: 'vsArmor', value: num(m[2]), target: 'self' };
        const t = vsTypeFromText(w);
        if (!t) return null;
        const out = { op: 'grantMod', mod: 'vsType', unitType: t, target: 'self' };
        out[key] = num(m[2]);
        return out;
      } },
    // 「对<兵种>(和总部)造成的伤害+N」——**这条正则只匹配"伤害"**，所以一律是 damage，不是 attack。
    //   （UNTED-LPD-7「对地面单位造成的伤害+4」、锤级巡地舰「对陆军和总部造成的伤害+2」）
    { re: /^对\s*([\u4e00-\u9fa5]{1,6})(?:和总部|与总部)?\s*(?:单位)?\s*(?:所)?(?:造成)?的?伤害\s*\+?\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => {
        const w = m[1], hasHq = /和总部|与总部/.test(m[0]);
        const t = vsTypeFromText(w) || 'infantry';
        const out = [{ op: 'grantMod', mod: 'vsType', unitType: t, damage: num(m[2]), target: 'self' }];
        // 打总部时同样只是伤害加成（vsHq 用对象形式区分 attack / damage）
        if (hasHq) out.push({ op: 'grantMod', mod: 'vsHq', damage: num(m[2]), attack: num(m[2]), target: 'self' });
        return out;
      } },
    // ★ 多兵种并列 + 可含"总部"：「对战陆军、坦克和炮兵时获得+2攻击力」「对陆军和总部造成的伤害+2」
    //   → 每个兵种各产出一条 vsType；含"总部"再产出一条 vsHq（打总部攻击加成）。
    //   旧的单兵种规则只吃一个词，多兵种并列会整句失配 → 删"和总部"DSL 没变化（用户报的 bug）。
    { re: /^对战\s*([\u4e00-\u9fa5、，,\s和与]+?)(?:单位)?(?:时)?\s*(?:具有|造成|获得|伤害)?\s*[+＋加]?\s*([一二两三四五六七八九十\d]+)\s*(?:点)?(攻击力|伤害)/,
      build: (m) => {
        const raw = String(m[1] || '');
        const out = [];
        const key = (m[3] === '伤害') ? 'damage' : 'attack';   // 同单兵种规则：攻击力 ≠ 伤害
        // 拆"总部"出来单独处理
        const hasHq = /总部/.test(raw);
        const typesRaw = raw.replace(/总部/g, '').split(/[、，,\s和与]+/).filter(Boolean);
        // ★ 去重：族词（如「陆军」→ [infantry,tank,artillery]）会覆盖后面的「坦克」「炮兵」，
        //   否则「对战陆军、坦克和炮兵」会产出 陆军族 + 坦克 + 炮兵 三条重复。
        const covered = new Set();
        typesRaw.forEach(function (w) {
          if (/重甲|装甲|护甲/.test(w)) { out.push({ op: 'grantMod', mod: 'vsArmor', value: num(m[2]), target: 'self' }); return; }
          const t = vsTypeFromText(w);
          if (!t) return;
          if (Array.isArray(t)) {
            if (t.some(function (x) { return covered.has(x); })) return;   // 已被前面的族词覆盖
            t.forEach(function (x) { covered.add(x); });
          } else {
            if (covered.has(t)) return;
            covered.add(t);
          }
          const e = { op: 'grantMod', mod: 'vsType', unitType: t, target: 'self' };
          e[key] = num(m[2]);
          out.push(e);
        });
        if (hasHq) {
          const e = { op: 'grantMod', mod: 'vsHq', target: 'self' };
          e[key] = num(m[2]);
          out.push(e);
        }
        return out.length ? out : null;
      } },
    // ★ 「对战<兵种>伤害翻倍」→ **常驻被动规则**（passiveRules.vsType.<type>.double）。
    //   ⚠ 不能写成 actions 里的 grantMod(target:self)：recomputeAuras 第 2 步只重放
    //     **幂等的自目标常驻修正**，明确排除了带 unitType/double/attack 的那些
    //     （见 effects.js 里那段注释）→ 写 actions 会**静默失效**（380装甲车曾因此完全没有效果）。
    { re: /^对战\s*[“"「]?([\u4e00-\u9fa5]{1,6})[”"」]?\s*(?:单位)?\s*(?:时)?\s*伤害\s*翻倍/,
      build: (m) => {
        const t = vsTypeFromText(m[1]);
        if (!t) return null;
        return { _standaloneEffect: { trigger: 'passive', passiveRules: { vsType: (function () { const o = {}; o[t] = { double: true }; return o; })() } } };
      } },

    /* ── B. 受到某类伤害翻倍 / 免疫某类反击 ── */
    { re: /(?:本单位)?受到指令伤害翻倍/, build: () => ({ op: 'grantMod', mod: 'takeDoubleFrom', unitType: 'order', target: 'self' }) },
    { re: /^本?单位?不会?受到来自\s*([\u4e00-\u9fa5]{1,6})\s*的?反击伤害/,
      build: (m) => { const t = vsTypeFromText(m[1]); return { op: 'grantMod', mod: 'noRetalFrom', unitType: t || 'infantry', target: 'self' }; } },
    { re: /^不会?受到\s*([\u4e00-\u9fa5]{1,6})\s*的?反击伤害/,
      build: (m) => { const t = vsTypeFromText(m[1]); return { op: 'grantMod', mod: 'noRetalFrom', unitType: t || 'space', target: 'self' }; } },
    { re: /^无视\s*([\u4e00-\u9fa5]{2,6})\s*的?(?:对战)?词条/, build: () => ({ op: 'grantMod', mod: 'ignoreCombatKw', target: 'self' }) },
    { re: /^不受单位效果影响/, build: () => ({ op: 'grantMod', mod: 'ignoreEnemyEffects', target: 'self' }) },
    // 「无法失去冲击，无法被抑制」= 两条并列的"免疫类"常驻修正，必须都产出
    //   ⚠ 第二条按捕获词分支：抑制 → noSuppress，压制 → noPin（2026-10-02 之前写死 noPin 是错的）
    { re: /^无法失去\s*([\u4e00-\u9fa5]{2,4})\s*[，,]\s*无法被?\s*(压制|抑制)\s*$/,
      build: (m) => [{ op: 'grantMod', mod: 'cantLose', keyword: KW_CN[m[1]] || m[1], target: 'self' },
        { op: 'grantMod', mod: m[2] === '抑制' ? 'noSuppress' : 'noPin', target: 'self' }] },
    { re: /无法失去\s*([\u4e00-\u9fa5]{2,4})/, build: (m) => ({ op: 'grantMod', mod: 'cantLose', keyword: KW_CN[m[1]] || m[1], target: 'self' }) },
    // ★「本单位受到指令重甲减伤」（制作者 2026-09-28 正式口径）：带这句的单位，
    //   **自己的重甲数值**对指令伤害也生效（mods.armorVsOrder → armorAgainst 生效）。
    //   旧实现（armorBonus+1）是近似糊弄，已废弃。轻甲变体暂无正式口径，保留 armorBonus 兜底。
    { re: /(?:本单位)?受到指令\s*重甲\s*(?:减伤|减免)/, build: () => ({ op: 'grantMod', mod: 'armorVsOrder', value: true, target: 'self' }) },
    { re: /(?:本单位)?受到指令\s*轻甲\s*(?:减伤|减免)/, build: () => ({ op: 'armorBonus', amount: 1, target: 'self' }) },

    /* ── C. 花费/数值取值族（动态数值，复用 num() 的 {stat} 机制）──
     *   注意：这几条的"其"指的不是"另一个目标"，而是事件的来源（打出的那张指令 / 本卡自身），
     *   所以都**硬定 target=self**，不用 _pronoun（否则会被代词兜底规则改成"随机友方"）。 */
    { re: /获得\s*等同于其?花费的?防御力/, build: () => ({ op: 'buff', defense: { stat: 'eventCardCost', of: 'self' }, target: 'self' }) },
    { re: /获得\s*等同于其?防御力的?指挥点/, build: () => ({ op: 'gainKredits', side: 'self', amount: { stat: 'lastDiscardedDefense' } }) },
    { re: /获得\s*等同于其?攻击力的?防御力/, build: () => ({ op: 'setStats', defense: 'attack', target: 'self' }) },
    // 「使一个单位失去等同于其花费的防御力」= **减去**该单位自己的花费（不是"设为"），
    //   减到 0 就把它消灭。曾经用 setStats + {stat:'cost'}：既把语义搞成赋值，
    //   又因为指令卡没有 self 单位、num 兜底返回 0，实际效果是"防御力变 0 且不死"。
    // 「使一个单位失去等同于其花费的防御力」→ loseDefense(byCost)：按**选定目标**的花费扣防御力，
    //   归零即消灭（引擎 killIfNoDefense）。
    //   ⚠ 卡面写的是「一个单位」（没写敌方）→ 应交给玩家任选，原来写死 {sel:'random',side:'enemy'} 是错的。
    { re: /使一个单位失去\s*等同于其?花费的?防御力/,
      build: () => ({ _compound: true, declare: [{ id: 't1', side: 'any', kind: 'unit', count: 1, prompt: '选择一个单位' }],
        actions: [{ op: 'loseDefense', byCost: true, target: 't1' }] }) },

    /* ── D. 能力类（引擎已有专用原语）── */
    { re: /^本单位?可以在同一回合内移动并攻击/, build: () => ({ op: 'canMoveAndAttack', target: 'self' }) },
    { re: /^也可以?在同一回合内移动并攻击/, build: () => ({ op: 'canMoveAndAttack', target: 'self' }) },
    // ★ 卡面例外（制作者 2026-09-28）：「本单位受到指令重甲减伤」——
    //   只有**带这句效果**的单位，其重甲才对指令伤害也生效；普通重甲不受影响。
    { re: /^本单位?(?:受到)?指令(?:伤害)?(?:时)?(?:的)?重甲(?:减伤|减免)/, build: () => ({ op: 'grantMod', mod: 'armorVsOrder', value: true, target: 'self' }) },

    /* ── E. 复用既有 op 的短句 ── */
    // 「弃掉手中的空袭」→ discard（按卡名，mode=named）：把"手中所有<卡名>"弃掉
    { re: /^弃掉手中(?:的|所有)?\s*[“"「]?([\u4e00-\u9fa5A-Za-z0-9\.\-]{2,12})[”"」]?\s*$/, build: (m) => ({ op: 'discard', mode: 'named', name: m[1], side: 'enemy' }) },
    // ★★ 「选择并弃一张X」→ 弃**自己**手牌里的一张 X（制作者 2026-09-24 定基准：
    //   弃牌只能弃手牌，且**默认是自己的牌**；只有卡面明写「敌方弃…」才弃对方）。
    //   X 映射到 filter.cardType，交给 OPS.discard 的 choose 模式只列出符合条件的牌
    //   （「一张单位」不该让人从指令牌里挑）。
    { re: /^选择并弃\s*(?:一|1)?\s*(?:张)?\s*(单位|指令|卡牌|手牌)/, build: (m) => {
      const f = { '单位': { cardType: 'unit' }, '指令': { cardType: 'order' } }[m[1]] || null;
      const act = { op: 'discard', count: 1, mode: 'choose', side: 'self', prompt: '选择并弃一张' + (m[1] || '牌') };
      if (f) act.filter = f;
      return act;
    } },
    // 「从三张X中抽取一张」→ **discover**（从若干张里**选择**一张，不是"开发"）
    //   ⚠ 与 `develop` 分工：「开发」= 从特定卡池复制；「从N张X中（选择/抽取）一张」= discover
    { re: /^从\s*([一二两三四五六七八九十\d]+)\s*张\s*([^，,。]{0,14}?)\s*中(?:抽取|选择|选)(?:一张|其一)/,
      build: (m) => {
        const d = m[2], f = {};
        const UNIT_WORD = /单位|坦克|步兵|炮兵|火炮|战斗机|轰炸机|巡洋舰|巡地舰|驱逐舰|太空|空军|陆军|舰|工事/;
        if (UNIT_WORD.test(d)) f.cardType = 'unit';
        const t = vsTypeFromText(d); if (t) f.unitType = t;
        // 阵营/系列词：中文走 SET_WORD_FILTER，英文缩写直接映射
        const EN_SET = { USG: 'USG', AV76: 'av76', UN: 'UN', SPK: 'SPK' };
        for (const w in SET_WORD_FILTER) if (d.indexOf(w) >= 0) { f.set = Array.isArray(SET_WORD_FILTER[w]) ? SET_WORD_FILTER[w][0] : SET_WORD_FILTER[w]; break; }
        if (f.set == null) { const em = d.match(/USG|AV76|av76|UN\b/); if (em) f.set = EN_SET[em[0]] || em[0]; }
        if (/精英/.test(d)) f.rarity = 'gold';
        return { op: 'discover', filter: f };
      } },
    // 「（使其）也算作是<兵种>」→ extraTypes（仅卡面明写"也算作X"时）
    { re: /^(?:使其)?也算作是?\s*([\u4e00-\u9fa5]{2,4})\s*$/, build: (m) => { const t = vsTypeFromText(m[1]); return t ? { op: 'grantMod', mod: 'extraTypes', unitType: t, target: 'self' } : null; } },
    { re: /^获得加\s*([一二两三四五六七八九十\d]+)\s*攻击力/, build: (m) => ({ op: 'buff', attack: num(m[1]), target: 'self' }) },
    { re: /^行动花费加\s*([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'opCostMod', amount: num(m[1]), target: 'self' }) },
    // 「友方战斗机行动花费减1」→ **常驻光环**（passive aura + aura.opCost）
    //   ⚠ 原来走 auraBuff（部署时一次性挂 dynMods）：只有部署那一刻在场的战斗机减费，
    //     之后登场的吃不到、来源阵亡减费还在（同 星盟/u/-4 的坏味道）。
    { re: /^友方\s*([\u4e00-\u9fa5]{1,6}?)\s*行动花费减\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => {
        const bare = String(m[1]).replace(/单位$/, '').replace(/的$/, '').trim();
        const kv = bare ? P.traitFilterOf(bare) : null;
        const f = kv || { name: m[1] };
        return { _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'friendly', filter: f }, opCost: -num(m[2]) } } };
      } },
    // ★★ 「友方单位造成的对战伤害+1」→ **持续光环**（passive aura）。
    //   语义：来源单位在场时，友方单位攻击结算伤害 +N；来源阵亡/被沉默即失效。
    //   ⚠ 不能走 auraBuff（那是部署时一次性挂 dynMods，来源死了加成还在），
    //     必须产出 trigger:'passive' 的独立效果块，由 recomputeAuras 每次重算时应用。
    //   落点：mods.extraDamageDealt（attackPowerAgainst 结算时加算，engine.js 唯一消费点）。
    { re: /(?:友方|你的|我方)\s*单位\s*造成的?\s*对战伤害\s*(?:增加|提升|提高)?\s*\+?\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'friendly' }, combatDamage: num(m[1]) } } }) },
    // ★★ 「友方单位造成的伤害+1」→ 同上（不含"对战"二字的宽式写法）。
    //   ⚠ 必须放在"对战伤害"规则**之后**（先让专用句式吃掉"对战伤害"），
    //     且要求"(友方|你的|我方)单位造成的"前缀 + 数量符号 —— "对一个单位造成2点伤害"
    //     是 damage 动作（无前缀、无 +/-），不会被这条误吃。
    { re: /(?:友方|你的|我方)\s*单位\s*造成的?\s*伤害\s*(?:增加|提升|提高)?\s*\+?\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'friendly' }, combatDamage: num(m[1]) } } }) },
    // ★★ 「敌方单位造成的伤害-1」→ 光环削弱**敌方全体**的输出（target side:'enemy'）
    { re: /(?:敌方|对方)\s*单位\s*造成的?\s*伤害\s*(?:减少|降低)?\s*-\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'enemy' }, combatDamage: -num(m[1]) } } }) },
    // ★★ 「友方单位受到的伤害-1」→ 光环减伤（target friendly，extraDamageTaken）
    { re: /(?:友方|你的|我方)\s*单位\s*受到的?\s*伤害\s*(?:减少|降低)?\s*-\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'friendly' }, takenDamage: -num(m[1]) } } }) },
    // ★★ 「受到的伤害-1」（自身常驻，非光环）→ extraDamageTaken。
    //   grantMod(target:self) 会被 recomputeAuras 第 2 步当作常驻 mods 应用（机制现成）。
    //   ⚠ 必须放在上面几条"XX单位造成的/受到的"之后 —— 本条正则最宽，先跑会吃掉它们。
    { re: /受到的?\s*伤害\s*(?:减少|降低)?\s*([+-])\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ op: 'grantMod', mod: 'extraDamageTaken', value: (m[1] === '-' ? -1 : 1) * num(m[2]), target: 'self' }) },
    // 「获得磁反应装甲1」等带数值词条
    { re: /^获得\s*([\u4e00-\u9fa5]{2,6})\s*([一二两三四五六七八九十\d])\s*$/, build: (m) => { const kw = KW_CN[m[1]] || null; return kw ? { op: 'grant', keyword: kw, value: num(m[2]), target: 'self' } : null; } },
    // 「场上存在敌方单位时，无法攻击敌方总部」
    { re: /场上存在(?:敌方|对方)单位时?\s*[，,]?\s*无法攻击(?:敌方|对方)?总部/, build: () => ({ op: 'grantMod', mod: 'noAttackHQ', target: 'self', cond: 'foeUnitOnBoard' }) },
    { re: /场上存在巡航舰时?\s*[，,]?\s*无法攻击/, build: () => ({ op: 'grantMod', mod: 'cannotAttack', target: 'self', cond: 'cruiserOnBoard' }) },
    // 「若前线有友方单位则减2行动花费」→ condOpCost（已有原语，filter 用空串表示"任意友方"）
    { re: /若前线有友方单位则?减\s*([一二两三四五六七八九十\d]+)\s*行动花费/, build: (m) => ({ op: 'condOpCost', filter: '', amount: -num(m[1]) }) },
    // 「本回合内，友方使用的指令效果翻倍」→ orderDoubleTurn
    { re: /本回合内?\s*[，,]?\s*友方使用的指令效果翻倍/, build: () => ({ op: 'orderDoubleTurn', side: 'self' }) },
    // 「将敌方卡组顶的卡牌置于手牌中」→ enemyDeckToHand
    { re: /将敌方卡组顶的?卡牌?置于手牌/, build: () => ({ op: 'enemyDeckToHand' }) },
    // 「弃掉位于卡组顶的五张牌」→ mill（从自己卡组顶磨）
    { re: /弃掉位于卡组顶的?\s*([一二两三四五六七八九十\d]+)\s*张/, build: (m) => ({ op: 'mill', count: num(m[1]), side: 'self' }) },
    // 「抽数张牌直至手牌数为N」→ drawUntil（handSize）
    { re: /抽数张牌直至手牌数为\s*([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'drawUntil', handSize: num(m[1]), side: 'self' }) },
    { re: /下一回合无法抽牌/, build: () => ({ op: 'noDrawNextTurn', side: 'self' }) },
    // ★ 「下个敌方回合开始时，敌方无法抽牌 / 无法增加指挥点槽」——两个"无法"可单独写
    //   （side 默认 enemy；也可写"下个友方回合"→ side:self）
    { re: /下个?(?:敌方|对方|对手)?回合(?:开始时)?[，,]?\s*(?:敌方|对方|对手)?无法抽牌\s*(?:(?:和|及|、|与|，|,)\s*(?:无法)?(?:增加|获得)(?:指挥点)?槽?)?/, build: (m) => {
        const out = [{ op: 'noDrawNextTurn', side: 'enemy' }];
        if (/增加|获得/.test(m[0] && m[0].slice(m[0].indexOf('抽牌'))) ) out.push({ op: 'noKreditSlotNextTurn', side: 'enemy' });
        return out;
      } },
    { re: /下个?(?:友方|我方)?回合(?:开始时)?[，,]?\s*无法抽牌/, build: () => ({ op: 'noDrawNextTurn', side: 'self' }) },
    { re: /下个?(?:敌方|对方|对手)?回合(?:开始时)?[，,]?\s*(?:敌方|对方|对手)?无法(?:增加|获得)(?:指挥点)?槽?([\s\S]*)$/,
      build: (m) => {
        // 「…无法增加指挥点槽**和抽牌**」= 两条抑制都要产出（以前只产 noKreditSlotNextTurn，"不能抽牌"静默丢失）
        const out = [{ op: 'noKreditSlotNextTurn', side: 'enemy' }];
        if (/抽牌|抽一张|抽卡/.test(m[1] || '')) out.push({ op: 'noDrawNextTurn', side: 'enemy' });
        return out;
      } },
    { re: /下个?(?:友方|我方)?回合(?:开始时)?[，,]?\s*无法(?:增加|获得)(?:指挥点)?槽?/, build: () => ({ op: 'noKreditSlotNextTurn', side: 'self' }) },
    // 「与一个敌方单位战斗」→ fight
    { re: /^与一个?敌方单位战斗/, build: () => ({ op: 'fight', target: { sel: 'random', side: 'enemy' } }) },
    // 「将随机太空战机加入同一阵线」→ summon（带 filter 随机）
    { re: /将随机\s*([\u4e00-\u9fa5]{2,6})\s*加入同一阵线/, build: (m) => { const t = vsTypeFromText(m[1]); return { op: 'summon', filter: t ? { unitType: t } : {}, side: 'self', count: 1 }; } },
    // 「该单位部署时将其移至下一阵线」→ move
    { re: /该单位部署时将其移至下一阵线/, build: () => ({ op: 'move', to: 'frontline', _pronoun: true }) },
    // 「并完全修复本单位」→ healAll（大数值 = 满血）
    { re: /并完全修复本单位/, build: () => ({ op: 'healAll', amount: 999, target: 'self' }) },
    // 「使场上、手牌中和卡组中的所有单位获得X」→ buffCardsInPiles（跨区群体增益）
    { re: /^使\s*场上[、，,]?\s*手牌中?\s*(?:和|、)?\s*卡组中?\s*的?所有单位\s*获得\s*([^，,。]{1,12})/,
      build: (m) => {
        const d = (m[1] || '').trim();
        // 「轻甲1和重甲1」这类词条列表 → keywords 数组（buffCardsInPiles 的 keywords 字段）
        const kwArr = [];
        const kwRe = /(轻甲|重甲|闪击|固守|守护|烟幕|伏击|奋战|狂怒|冲击|游击|硬铝弹|磁反应装甲|强磁护盾|海绵装甲)\d*/g;
        let k;
        while ((k = kwRe.exec(d))) {
          const cn = k[1];
          const kw = KW_CN[cn];
          if (kw) kwArr.push(kw);
        }
        if (kwArr.length) {
          // buffCardsInPiles 只附魔手牌+卡组，场上单位要再用 grantAll 逐个授词条（防弹涂料）
          const acts = [{ op: 'buffCardsInPiles', side: 'self', filter: { cardType: 'unit' }, keywords: kwArr }];
          kwArr.forEach(kw => acts.push({ op: 'grantAll', target: { sel: 'all', side: 'friendly' }, keyword: kw, value: 1 }));
          return acts;
        }
        return { op: 'buffCardsInPiles', scopes: ['board', 'hand', 'deck'], desc: d };
      } },
    // ── 第十三轮补的长尾（复用既有 op，编译器覆盖此前"手写专属"句式）──
    // 「对战X时伤害加N」—— 语序是「伤害+加N」（数字在末尾），与上面那条「具有+N攻击力」不同
    { re: /^对战\s*[“"「]?(步兵|坦克|火炮|炮兵|战斗机|轰炸机|太空战机|巡地舰|巡航舰|工事|建筑|空军|陆军|太空单位|太空|重甲单位|装甲单位|重甲|装甲|护甲)[”"」]?\s*(?:单位)?(?:时)?\s*伤害\s*[+＋加]\s*([一二两三四五六七八九十\d]+)\s*$/,
      build: (m) => {
        const w = String(m[1]);
        if (/重甲|装甲|护甲/.test(w)) return { op: 'grantMod', mod: 'vsArmor', value: num(m[2]), target: 'self' };
        const t = vsTypeFromText(w);
        return t ? { op: 'grantMod', mod: 'vsType', unitType: t, attack: num(m[2]), target: 'self' } : null;
      } },
    // 「敌方弃N张牌」→ 敌方随机弃牌
    { re: /^(?:敌方|对方|对手)\s*弃\s*([一二两三四五六七八九十\d]+)\s*张牌/, build: (m) => ({ op: 'discard', count: num(m[1]), mode: 'random', side: 'enemy' }) },
    // 「弃掉所有手牌」→ discardAll
    { re: /^弃掉所有手牌/, build: () => ({ op: 'discardAll', side: 'self' }) },
    // 「使指挥点槽等于N」→ setKreditSlots（等于，不是 +N）
    { re: /^使指挥点槽等于\s*([一二两三四五六七八九十\d]+)/, build: (m) => ({ op: 'setKreditSlots', value: num(m[1]), side: 'self' }) },
    // 「从卡组顶移除N张牌」→ removeDeckTop
    { re: /^从卡组顶移除\s*([一二两三四五六七八九十\d]+)\s*张牌/, build: (m) => ({ op: 'removeDeckTop', count: num(m[1]), side: 'self', to: 'removed' }) },
    // 「弃掉手牌和卡组中的所有单位」→ discardAll + discardFromDeck（跨区弃单位）
    { re: /^弃掉手牌和卡组中的所有单位/, build: () => [{ op: 'discardAll', side: 'self', filter: { cardType: 'unit' } }, { op: 'discardFromDeck', side: 'self', filter: { cardType: 'unit' } }] },
    // 「将数张X洗入卡组，直至卡组数为N」→ shuffleInUntil
    { re: /^将数张\s*[“"「]?([^”"」，。]{2,12})[”"」]?\s*洗入卡组[，,]?\s*直至卡组数为\s*([一二两三四五六七八九十\d]+)/,
      build: (m) => ({ op: 'shuffleInUntil', name: m[1], deckSize: num(m[2]), side: 'self' }) },
    // 「前线至多有N个单位」→ frontlineMaxOverride（引擎读 u.mods.frontlineMaxOverride 取最小值）
    { re: /前线至多有\s*([一二两三四五六七八九十\d]+)\s*个单位/, build: (m) => ({ op: 'grantMod', mod: 'frontlineMaxOverride', value: num(m[1]), target: 'self' }) },
    // 「使一个地表单位上升到太空」→ 追加太空身份（复用 extraTypes，最小单位原语）
    { re: /^使\s*(?:一|1)?\s*(?:个|名|辆|架|艘)?\s*(?:地表|地面)\s*单位\s*上升到太空/, build: () => ({ op: 'grantMod', mod: 'extraTypes', unitType: 'space', target: { sel: 'random', side: 'friendly' } }) },
    // 「其行动花费+N」→ 代词"其"指向事件主角（无 eventRef 退回随机友方）
    { re: /^其行动花费\s*([+＋\-−减])\s*([一二两三四五六七八九十\d]+)\s*$/, build: (m) => ({ op: 'opCostMod', amount: (/[-−减]/.test(m[1]) ? -1 : 1) * num(m[2]), target: { sel: 'random', side: 'friendly' }, _pronoun: true }) },
    // 「获得闪击和奋战」→ 两条 grant（并列双词条）
    { re: /^获得\s*([\u4e00-\u9fa5]{2,4})\s*和\s*([\u4e00-\u9fa5]{2,4})\s*$/, build: (m) => { const k1 = KW_CN[m[1]] || null, k2 = KW_CN[m[2]] || null; if (!k1 && !k2) return null; return [k1 ? { op: 'grant', keyword: k1, target: 'self' } : null, k2 ? { op: 'grant', keyword: k2, target: 'self' } : null].filter(Boolean); } },
    // 「选择一张手牌，将其随机转换为一个X单位」→ chooseHandCard + transformHandCard
    { re: /^选择一张手牌[，,]?\s*将其随机转换为一个\s*([\u4e00-\u9fa5A-Za-z]+)\s*单位/,
      build: (m) => { const set = SET_WORD_FILTER[m[1]] || m[1]; return [{ op: 'chooseHandCard', side: 'self', prompt: '选择一张手牌转换为 ' + m[1] + ' 单位' }, { op: 'transformHandCard', target: { sel: 'ref', ref: 'chosenHandCard' }, set: Array.isArray(set) ? set[0] : set }]; } },
    // 「并随机获得其中一个的攻击力」→ 把刚召唤的单位攻击力复制到自己（forEach + buff）
    //   （splitTop 会先按「并」拆句，所以这里「并」是可选的）
    { re: /^(?:并)?随机获得其中一个的(?:攻击力|防御力|生命)/,
      build: (m) => ({ op: 'forEach', target: { sel: 'ref', ref: 'summoned' }, as: 't', actions: [{ op: 'buff', target: 'self', attack: { stat: 'attack', of: 't' } }] }) },
    // 「本回合，友方单位部署时，将其移至前线」→ hqEnchant + untilTurnEnd（本回合到期）
    { re: /^本回合[，,]?\s*友方单位部署时[，,]?\s*将其移至前线/,
      build: () => ({ op: 'hqEnchant', name: '本回合友方部署移至前线', untilTurnEnd: true, effects: [{ trigger: 'unitDeployed', owner: 'self', actions: [{ op: 'move', target: { sel: 'ref', ref: 'eventUnit' }, to: 'frontline' }] }] }) },
    // 「将其加入前线」→ 把"刚加入手牌的那张卡"放到前线（chooseFromHand toField）
    { re: /^将其加入(?:前线|支援阵线)/,
      build: (m) => ({ op: 'chooseFromHand', side: 'self', filter: { cardType: 'unit' }, mode: 'toField', to: /前线/.test(m[0]) ? 'frontline' : 'support' }) },
    // 「将总花费等同于指挥点槽数的随机X单位加入支援阵线，直到阵线已满」→ 抉择第一选项
    //   （手写简化为 repeat(deckToField to support) —— "直到满"用重复 5 次近似）
    { re: /^将总花费等同于指挥点槽数的?/,
      build: () => ({ op: 'deckToField', to: 'support', filter: { cardType: 'unit' } }) },
    // 抉择里省略动词的纯卡名分支（「或者新地及卡尔拉扩展研发」→ 加入手牌）。
    //   ⚠ 必须**同时**满足两个条件才认：(a) 命中卡池；(b) 文本**不像句子**。
    //   只有 poolHasName 不够 —— 卡名判断依赖卡池，而"像不像句子"能独立挡掉
    //   「额外获得一个指挥额槽」这类被误当卡名的整句（曾因此污染大量卡）。
    { re: /^[“"「]?([\u4e00-\u9fa5A-Za-z][\u4e00-\u9fa5A-Za-z0-9\.\-]{1,13})[”"」]?\s*$/,
      build: (m) => {
        const n = m[1];
        // 含动词/连接词/条件词 = 是句子，不是卡名
        if (/(获得|造成|抽|弃|消灭|摧毁|使|将|对|随机|加入|所有|如果|若|则|并且?|和|然后|每|一回合|免疫|无法|无视|受到|恢复|移动|部署|压制|抑制|撤退|复制|开发|洗入|置于|回复|反击|选择)/.test(n)) return null;
        /* ★ 牌Q 卡包（2026-09-25）：**机制词**绝不能被当成卡名。
         *   踩过的坑：「拖延。」这句是延迟机制的声明，但池里恰好有一张卡叫「拖延」→
         *   被编成 addCardToHand{name:'拖延'} —— 静默错解（还因此混过了未实现清单）。 */
        if (/^(拖延|预报|穿插|袭击|智像|铝舰甲|白天|黑夜|时间|天气|薄雾|狂风|落雪|蓝天)$/.test(n)) return null;
        return poolHasName(n) ? { op: 'addCardToHand', name: n, count: 1 } : null;
      } },
    // 「获得收缴」等"本单位获得某词条"→ target=self（而不是选一个友方单位）
    { re: /^获得\s*(收缴|打捞|游击|闪击|固守|守护|烟幕|伏击|奋战|狂怒|冲击|老兵|硬铝弹|强磁护盾|磁反应装甲|海绵装甲)\s*$/,
      build: (m) => { const kw = KW_CN[m[1]]; return kw ? { op: 'grant', keyword: kw, target: 'self' } : null; } },
    // 「并完全修复本单位」→ healAll
    { re: /^并完全修复本单位/, build: () => ({ op: 'healAll', amount: 999, target: 'self' }) },
    // 「将目标单位的攻击力设为0」→ target 是"被攻击的单位"（defender），不是让玩家选靶
    { re: /^将目标单位的?(?:攻击力|防御力)?(?:设为|变为|降为)\s*0/, build: () => ({ op: 'setStats', attack: 0, target: { sel: 'ref', ref: 'defender' } }) },
    // 「本单位无视指令、反制和单位效果」→ 三个常驻修正（一条句子里并列多个 mod）
    { re: /^本单位?无视\s*指令\s*[、，,]?\s*反制\s*[、，,]?\s*(?:和|及)?\s*单位效果/,
      build: () => [{ op: 'grantMod', mod: 'ignoreOrders', target: 'self' }, { op: 'grantMod', mod: 'ignoreEnemyEffects', target: 'self' }, { op: 'grantMod', mod: 'counterImmune', target: 'self' }] },
    { re: /^本单位?无视\s*指令\s*[、，,]?\s*(?:和|及)?\s*单位效果/,
      build: () => [{ op: 'grantMod', mod: 'ignoreOrders', target: 'self' }, { op: 'grantMod', mod: 'ignoreEnemyEffects', target: 'self' }] },
    // 「使其获得"友方总部具有免疫"」→ 给该单位挂 hqImmune（引擎读 u.mods.hqImmune）
    { re: /^使其获得\s*[“"「]?友方总部具有免疫[”"」]?/, build: () => ({ op: 'grantMod', mod: 'hqImmune', target: 'self', _pronoun: true }) },
    { re: /^获得\s*[“"「]?友方总部具有免疫[”"」]?/, build: () => ({ op: 'grantMod', mod: 'hqImmune', target: 'self' }) },
    // 「将一张复制洗入卡组」→ copyToDeck（复制本卡进卡组）
    { re: /^将一张复制洗入卡组/, build: () => ({ op: 'copyToDeck', of: 'self', self: true }) },
    // 「本回合友方单位每行动一次，下一个友方回合开始时，额外抽一张牌并获得两个指挥点」
    //   → 总部附魔（untilTurnEnd）+ unitActed 监听
    { re: /^本回合友方单位每行动一次[，,]?\s*下一个友方回合开始时[，,]?\s*额外抽一张牌并获得两个指挥点/,
      build: () => ({ op: 'hqEnchant', name: '联合工业政策', untilTurnEnd: true,
        effects: [{ trigger: 'unitActed', owner: 'self', actions: [{ op: 'nextTurnDrawPending', amount: 1, side: 'self' }, { op: 'nextTurnKredits', amount: 2, side: 'self' }] }] }) },
    // 「将随机X加入同一阵线」→ summon 到同一阵线（to:'sameZone'）
    { re: /^将随机\s*([\u4e00-\u9fa5]{2,6})\s*加入同一阵线/,
      build: (m) => { const t = vsTypeFromText(m[1]); return { op: 'summon', filter: t ? { unitType: t } : {}, side: 'self', count: 1, to: 'sameZone' }; } },
    // 「随机将一张X加入手牌」→ 卡名（命中卡池）或按兵种/系列随机。
    //   ⚠ 不能无条件把 X 当卡名（「随机Mk坦克」在卡池里不存在 → 效果静默失效）
    { re: /^随机将一张\s*([\u4e00-\u9fa5A-Za-z]{2,10})\s*加入手牌/,
      build: (m) => {
        const ref = resolveCardRef(m[1], false);
        if (!ref) return null;
        const a = { op: 'addCardToHand', random: true };
        if (ref.name) a.name = ref.name; else a.filter = ref.filter;
        return a;
      } },
    // ★ 牌Q（2026-09-25）：「使敌方支援线所有单位获得“本单位被指向或攻击时，消灭本单位。”」
    //   必须放在 LONGTAIL4（早于通用 ACTIONS 的「消灭」）—— 否则整句会被**从中间匹配走**
    //   （只剩下 destroy 全体敌方支援线），引号里的触发限定静默丢失。
    { re: /^使敌方支援线所有单位获得\s*[“"「]?本单位被指向或攻击时[，,]?\s*消灭本单位[。”"「\s]*$/,
      build: () => ([
        { op: 'grantEffect', target: { sel: 'all', side: 'enemy', zone: 'support' }, effectTrigger: 'targeted',
          effects: [{ op: 'destroy', target: { sel: 'self' } }], label: '被指向时消灭自己' },
        { op: 'grantEffect', target: { sel: 'all', side: 'enemy', zone: 'support' }, effectTrigger: 'attacked',
          effects: [{ op: 'destroy', target: { sel: 'self' } }], label: '被攻击时消灭自己' },
      ]) },
  ];
  P.LONGTAIL4 = LONGTAIL4;
  P.vsTypeFromText = vsTypeFromText;

  P.KW_CN = KW_CN;

  /* ------------------------------------------------ 卡牌操作（必须带卡名） */
  // 「将N张<卡名>加入手牌 / 加入支援阵线 / 洗入卡组」这类句式**必须**解析出卡名，
  // 否则引擎只能报"卡池中找不到「undefined」"（静默失效）。所以放在所有动作匹配之前。
  const CARD_OP_VERBS = [
    // 「加入手牌 / 加入双方手牌中 / 置于手中」——"中/里/双方"是修饰，卡名在它们之前
    { re: /加入\s*(?:双方)?\s*手牌(?:中|里)?|置于\s*手中|放入\s*手中/, op: 'addCardToHand' },
    // 「置入卡组底 / 置于卡组顶」→ shuffleIn（带 to 方向）
    { re: /(?:置入|置于|放入)\s*卡组\s*(底|顶)|(?:置入|置于|放入)\s*卡组(?:底部|顶部)/, op: 'shuffleIn' },
    // 「加入总部相邻处 / 加入同一条阵线 / 加入本阵线」→ 都是 summon（进场，不是进手牌）
    { re: /加入\s*(?:总部\s*)?相邻处|加入\s*(?:同一条|同一|该|本)\s*阵线|加入\s*总部\s*(?:旁|边)/, op: 'summon' },
    { re: /加入\s*(?:战场|支援阵线|支援线|前线)|召唤/, op: 'summon' },
    { re: /洗入\s*卡组|加入\s*卡组(?:顶|底)?/, op: 'shuffleIn' },
  ];
  // 卡池里有没有名字包含它的卡？（有卡池信息时才判断，避免误伤）
  function poolHasName(name) {
    const clean = String(name).replace(/[“”"「」]/g, '').trim();
    if (!clean) return false;
    // 运行时：优先用 KG.pool（id → 卡）
    //   ⚠ **大小写不敏感**比对：卡面常写全大写的「AV76突击师」，卡池里是「Av76突击师」
    //     （严格 indexOf 会判为"不是卡名"→ 效果退化成"没有卡名"而失效）。
    const lc = clean.toLowerCase();
    // ⚠ 词条参照卡（`referenceCard:true` 的说明卡，如「太空战机 / SpaceCraft」「巡航舰」）
    //   **不算卡池成员** —— 否则「将随机太空战机加入同一阵线」会被判成"按卡名找太空战机"，
    //   而那张是说明卡，召唤会落空。
    const isPool = function (c) { return c && !c.referenceCard; };
    const pool = root.KG && root.KG.pool;
    if (pool && Object.keys(pool).length) {
      return Object.keys(pool).filter(function (id) { return isPool(pool[id]); })
        .some(function (id) { return String(pool[id].name || '').toLowerCase().indexOf(lc) >= 0; });
    }
    // ★ 编译时（node / coverage.js）：KG.pool 还没建，但 **KG_CARDS 一定有**（cards.js 已加载）。
    //   必须用 KG_CARDS 判断，**绝不能在没有卡池信息时返回 true** ——
    //   那会让任何中文串都被当成卡名（曾导致「额外获得一个指挥额槽」被解析成
    //   `addCardToHand(name:'额外获得一个指挥额槽')`，覆盖率虚高、落盘后 17 条行为测试失败）。
    const arr = root.KG_CARDS;
    if (Array.isArray(arr) && arr.length) {
      return arr.filter(isPool).some(function (c) { return String(c.name || '').toLowerCase().indexOf(lc) >= 0; });
    }
    return false;      // 拿不到任何卡池信息 → 保守：不认为是卡名
  }
  // 既不是卡名、又带兵种/系列词 → 用 filter 随机取（"随机新星联盟太空单位""联合国国家的卡牌"）
  function filterFromWords(name) {
    const f = {};
    for (const k in SET_WORD_FILTER) if (name.indexOf(k) >= 0) { f.set = SET_WORD_FILTER[k]; break; }
    for (const k in TYPE_WORD_FILTER) if (name.indexOf(k) >= 0) { f.unitType = TYPE_WORD_FILTER[k]; break; }
    return Object.keys(f).length ? f : null;
  }
  /* ★★ 卡名识别（制作者规则：**单位名字都是有引号的**）
   *   带引号（“X” / 「X」 / "X"）→ **一定是卡名**，不必查池；
   *   无引号 → **必须命中卡池**才算卡名；否则退化为"按兵种/系列"的 filter；
   *   两者都不成立 → null（**绝不产出无效卡名** —— 引擎只会报"卡池中找不到「随机Mk坦克」"而静默失效）。
   */
  function resolveCardRef(rawName, quoted) {
    const n = String(rawName || '').replace(/[“”"「」]/g, '').replace(/随机\s*/g, '').trim();
    if (!n) return null;
    // ★★ **兵种/系列词优先于别名表**：「太空战机」「陆军」是**兵种**（卡面写"将随机太空战机
    //    加入同一阵线"是"随机召唤一个该兵种单位"），而别名表里「太空战机」指向的是**词条参照卡**。
    //    若不优先兵种，会变成"召唤那张说明卡"（zbk1母舰曾因此召唤出参照卡）。
    if (TYPE_WORD_FILTER[n]) return { filter: { unitType: TYPE_WORD_FILTER[n] } };
    // ★ 「研发」是一**组**卡，不是某一张：制作者确认 = **3 费研发**
    //   （新地及卡尔拉研发 / 帝国安保初级研发 / 联合SWX政府研发）。
    //   「随机将一张研发加入手牌」（帝国统治）因此固定为 nameIncludes:'研发' + maxCost:3 的随机取一张。
    const GROUP_FILTERS = { 研发: { nameIncludes: '研发', maxCost: 3 } };
    if (GROUP_FILTERS[n]) return { filter: GROUP_FILTERS[n] };
    // ★ 别名表：卡面常用简称/别名（「星盟通用大驱」→ 星盟/u/-16）。取**最长**匹配键。
    const al = root.KG_ALIASES || (root.KG && root.KG.aliases) || null;
    if (al) {
      let best = null;
      Object.keys(al).forEach(function (k) {
        if (!k) return;
        if (n === k || n.indexOf(k) >= 0 || k.indexOf(n) >= 0) { if (!best || k.length > best.length) best = k; }
      });
      if (best && al[best]) return { cardId: al[best], name: n };
    }
    if (quoted) return { name: n };
    // ★★ 「体系」必须排在**卡名子串匹配之前**（2026-09-22 实测回归）：
    //   卡池里有一张卡就叫「Mk」，于是 `poolHasName('Mk坦克')` 会因为**子串**命中它，
    //   「部署两辆 Mk坦克」就变成"召唤那张叫 Mk 的卡"（还带上 {unitType:'tank', name:'Mk'}）。
    //   体系词优先于卡名子串，正是制作者要的语义（体系名就是用来指"这一族"的）。
    const sysW = systemWordIn(n);
    if (sysW) return { filter: { system: sysW } };
    /* ★ 系列名兜底**必须排在卡名子串匹配之前**（Alan 09-28「将一张随机进攻洗入卡组」——
     *   「进攻」= set「进攻模式」那一套 11 张 token。若放后面，poolHasName 的**子串**匹配
     *   会先命中那张就叫「进攻」的指令卡（av76/command/-17），整句变成"洗入那张指令"——
     *   不是制作者要的"整套随机"。set 命中取**最短**（最精确）。
     *   （"进攻" 是 set 的**前缀**才认，避免"X模式"误吃"X模式·第二期"以外的怪东西。） */
    {
      const arr0 = root.KG_CARDS || (root.KG && root.KG.pool ? Object.keys(root.KG.pool).map(function (k) { return root.KG.pool[k]; }) : null);
      const list0 = Array.isArray(arr0) ? arr0 : (arr0 || []);
      let hitSet = null;
      for (const c of list0) {
        const st = (c && !c.referenceCard) ? String(c.set || '').trim() : '';
        if (st && (st === n || st.indexOf(n) === 0)) { if (!hitSet || st.length < hitSet.length) hitSet = st; }
      }
      if (hitSet) return { filter: { set: hitSet } };
    }
    if (poolHasName(n)) return { name: n };
    const base = filterFromWords(n);
    if (base) {
      const f = Object.assign({}, base);
      // ★ 系列限定：「Mk坦克」→ {unitType:'tank', nameIncludes:'Mk'}
      //   （"坦克"前的部分就是系列名，交给引擎按卡名子串筛）
      for (const k in TYPE_WORD_FILTER) {
        const idx = n.indexOf(k);
        if (idx > 0) { f.nameIncludes = n.slice(0, idx); break; }
      }
      return { filter: f };
    }
    // ★ 「黑盾单位」「帝国坦克」这类"系列名 + 单位/牌"：卡池里若有卡名含该系列，
    //   就按卡名子串筛（nameIncludes）。这是"给某个系列建卡池"的通用兜底。
    const core = n.replace(/(单位|卡牌|牌)$/, '').trim();
    if (core && core !== n && core.length >= 2) {
      const arr = root.KG_CARDS || (root.KG && root.KG.pool) || null;
      const list = Array.isArray(arr) ? arr : (arr ? Object.keys(arr).map(function (k) { return arr[k]; }) : []);
      if (list.some(function (c) { return c && String(c.name || '').indexOf(core) >= 0; })) {
        return { filter: { nameIncludes: core } };
      }
    }
    return null;
  }
  P.resolveCardRef = resolveCardRef;

  /* ★★ 「体系」词表（2026-09-22 制作者提出）
   *   卡牌的一个**元数据字段** `system`，与 attack / cost / unitType **同级**，
   *   在制卡台里直接填写；**不进 DSL、也不进对战词条**。
   *   它就是"作战体系 / 家族"这一维度：Mk坦克 / WgL战斗机 / 黑盾 / 航母 / 伞兵 …
   *
   *   词表**从卡池动态扫出来**（卡的 system 字段），所以制作者在制卡台填一个新体系、
   *   保存后就能在卡面里引用它，**不用改代码**。
   *   ⚠ 另外允许一个"临时词表" `__kgNewSystems`：编辑器里正在编的那张卡还没进池，
   *     把它的体系先塞进去，这样"部署两辆 Mk坦克"（而本卡自己就是 Mk坦克体系）也能认。 */
  function systemWords() {
    const out = {};
    const FX2 = root.KG && root.KG.effects;
    const add = function (s) { if (s != null && String(s).trim()) out[String(s).trim()] = true; };
    if (FX2 && FX2.systemWords) FX2.systemWords().forEach(add);
    const pool = root.KG && root.KG.pool;
    if (pool) (Array.isArray(pool) ? pool : Object.keys(pool).map(function (k) { return pool[k]; }))
      .forEach(function (c) { if (c && c.system != null) (Array.isArray(c.system) ? c.system : [c.system]).forEach(add); });
    const extra = root.__kgNewSystems;
    if (Array.isArray(extra)) extra.forEach(add);
    // 长的优先，避免「黑盾」把「黑盾第二团」这种更具体的体系名吃掉
    return Object.keys(out).sort(function (a, b) { return b.length - a.length; });
  }
  // 文本里出现的第一个体系词（没有 → null）
  function systemWordIn(text) {
    const s = String(text || '');
    const ws = systemWords();
    for (let i = 0; i < ws.length; i++) if (s.indexOf(ws[i]) >= 0) return ws[i];
    return null;
  }
  P.systemWords = systemWords;
  P.systemWordIn = systemWordIn;
  const CN_COUNT_RE = '[0-9０-９一二两三四五六七八九十]+';
  // 「将三张“经济复苏”」→ { name:'经济复苏', count:3 }
  function pickCardName(head) {
    let h = String(head || '').trim();
    if (!h) return null;
    h = h.replace(/^[^：:]{0,14}[：:]/, '');                       // 抉择：/ 部署：
    h = h.replace(/^(?:并|然后|且|，|,)\s*/, '');
    h = h.replace(/^(?:如果|若|假如)[^，,。]{0,16}[，,]\s*/, '');   // 「如果行动花费不小于8，将一张…」
    h = h.replace(/^(?:或者?|或)\s*/, '');
    h = h.replace(/^随机\s*/, '');
    h = h.replace(/^[使令]\s*(?:其|它|该单位|该卡)\s*/, '');        // 「使其加入战场」→ 没有卡名
    h = h.replace(/^(?:将|把|取)\s*/, '');
    let unknown = false;
    const um = h.match(/^(?:数张|若干张?|随机数量(?:的)?|一些|多张)\s*/);
    if (um) { unknown = true; h = h.slice(um[0].length); }
    let count = 1;
    const cm = h.match(new RegExp('^(' + CN_COUNT_RE + ')\\s*(?:张|个|名|辆|架|艘)?\\s*'));
    if (cm) { count = num(cm[1]) || 1; h = h.slice(cm[0].length); }
    // ★ 是否**带引号**（制作者规则：单位名字都是有引号的 → 带引号=一定是卡名）
    const quoted = /[“"「][^”"」]{1,20}[”"」]/.test(h);
    h = h.replace(/^[“"「]/, '').replace(/[”"」]\s*$/, '').trim();
    // ★ 剥掉悬空的连接词（「将一张蜂鸟突击舰**和**」→ 卡名是"蜂鸟突击舰"）
    h = h.replace(/[和与及、]\s*$/, '').trim();
    // 代词 / 空名 → 认不出是哪张卡
    if (!h || /^(?:其|它|此|该|这)$/.test(h)) return null;
    return { name: h, count: count, unknown: unknown, quoted: quoted };
  }
  // 返回动作数组；认不出卡名时返回 null（交给后面的通用规则，至少会报"缺少卡名"）
  function parseCardOp(s) {
    let op = null, verb = null;
    for (const c of CARD_OP_VERBS) { const m = s.match(c.re); if (m) { op = c.op; verb = m[0]; break; } }
    if (!op) return null;
    const headRaw = s.slice(0, s.indexOf(verb));
    // ★ 动词前若已出现句读（，。；），说明这是**多动作句子里的后一段**，不是"将N张X <动词>"。
    //   此时必须交回 splitTop 拆句，否则会把整句吞成一个卡牌操作
    //   （曾把「额外获得一个指挥额槽，如果…，对所有敌方单位造成1点伤害，将一张复制洗入卡组」
    //    整句吞成单个 shuffleIn，前两个动作全丢）。
    if (/[，,。；;]/.test(headRaw)) return null;
    // 「将卡组顶的单位加入战场」→ 不是按卡名找，是"卡组顶那张单位直接进场"
    if (/卡组顶|牌库顶/.test(headRaw)) {
      return op === 'summon' ? [{ op: 'deckToField', to: /前线/.test(s) ? 'frontline' : 'support' }] : null;
    }
    const random = /随机/.test(headRaw);
    // 「一张A和一张B」→ 两张不同的卡
    let parts = [headRaw];
    if (/张[^，,。]*[和、]/.test(headRaw)) {
      parts = headRaw.replace(/^[^将把]*?(?=[将把])/, '').split(/[和、]/);
    }
    const acts = [];
    for (const part of parts) {
      const pk = pickCardName(part);
      if (!pk) continue;
      const a = { op: op, count: pk.count };
      // 引号是卡面作者给出的明确卡名声明。后续 compiler 的统一名称校验必须保留它，
      // 否则「BS-20A1坦克」会因与卡池「BS-20A1 坦克」仅差空格而被误降级成坦克筛选。
      if (pk.quoted) a._quotedName = true;
      if (pk.unknown) delete a.count;                       // 张数不明 → 用引擎默认值
      const isCopy = /复制$/.test(pk.name) && op !== 'summon';
      // ★ 统一走 resolveCardRef（带引号=卡名；无引号必须命中卡池；否则退化为兵种/系列 filter）。
      //   都不成立 → **不产出**，绝不设无效 name —— 引擎对无效卡名只会报
      //   "卡池中找不到「随机Mk坦克」"然后静默失效。
      const ref = resolveCardRef(pk.name, pk.quoted);
      if (isCopy) {
        a.self = true;                                      // 「将一张复制加入手牌 / 加入卡组顶」= 复制自己
      } else if (ref && ref.cardId) {
        a.cardId = ref.cardId;                              // 别名命中 → 直接给卡 id
        a.name = ref.name;
        if (random) a.random = true;
      } else if (ref && ref.name) {
        a.name = ref.name;
        if (random) a.random = true;
      } else if (ref && ref.filter) {
        a.filter = ref.filter;                              // 「随机Mk坦克」→ 按坦克类随机
        if (random) a.random = true;
      } else {
        continue;                                           // 认不出卡名/兵种 → 交给别的规则
      }
      if (op === 'shuffleIn') {
        if (/底/.test(verb)) a.to = 'bottom';
        else if (/顶/.test(verb)) a.to = 'top';
      }
      if (op === 'summon') {
        // 「加入同一阵线」= 跟来源单位同一条线（引擎 to='sameZone'）
        // ★ 「加入总部相邻处 / 总部旁」= 挨着总部放（引擎 to='hqAdjacent'），
        //   与「加入支援阵线」（固定放最右边）是两回事（制作者指出）。
        if (/总部\s*(?:相邻处|旁|边)/.test(verb)) a.to = 'hqAdjacent';
        else if (/同一阵线|同一条阵线/.test(verb)) a.to = 'sameZone';
        else if (/前线/.test(verb)) a.to = 'frontline';
      }
      // ★ 「其花费减为0」这类**后置花费修饰**（「强力支援」：
      //   "将三张火药机加入手牌，其花费减为0"）。
      //   "其"回指本句刚加入手牌的那些牌 —— 所以修饰必须落在**这一句产出的实例**上。
      //   ⚠ 判据要看**整句 s**，不能看 headRaw：headRaw 只截到动词（"将三张火药机"），
      //     修饰语在动词之后。
      //   引擎侧：instCost 首行 `if (inst.costSet != null) return inst.costSet` —— 机制现成。
      if (op === 'addCardToHand') {
        const toM = s.match(/其花费\s*(?:减为|变为|设为|降为|改为)\s*([0-9０-９一二两三四五六七八九十]+)/);
        const downM = s.match(/其花费\s*减少\s*([0-9０-９一二两三四五六七八九十]+)\s*点/);
        if (toM) a.costTo = num(toM[1]);
        else if (downM) a.costMod = -num(downM[1]);
      }
      acts.push(a);
    }
    if (!acts.length) return null;
    // ★ 「加入双方手牌(中)」→ 每条动作克隆一份给对手（炸弹战争）。
    //   ⚠ parseCardOp 在 LONGTAIL4 **之前**执行（0.65），"双方"必须在这里读——
    //     放到后面的规则里永远轮不到（被本函数按单阵营产出后直接 return）。
    if (op === 'addCardToHand' && /双(?:方|边)/.test(s)) {
      return acts.concat(acts.map(a => Object.assign({}, a, { side: 'enemy' })));
    }
    // ★ 「将数张X加入支援阵线，直到阵线已满」→ 反复召唤直到阵线没有空位
    //   （手写层的写法：repeat{times:{supportFree:'self'}} + summon）
    if (op === 'summon' && /(?:直到|直至)\s*阵线已满/.test(s)) {
      return [{ op: 'repeat', times: { supportFree: 'self' }, actions: acts }];
    }
    return acts;
  }

  /* --------------------------------------------------------- 条件原语组合
   * 一句条件子句（「如果 X」里的 X）→ 一个**条件原语**。
   * 能拆的用通用比较原语 compare（{left:取值, cmp:比较符, value:右值}），
   * 拆不了的（"被消灭了吗"这种要看事件状态的）才做成独立条件原语。
   * 新增一种条件写法 = 往 COND_RULES 里加一行，不用改解析主流程。
   */
  // 比较词表（唯一源）：所有条件/过滤规则共用。
  //   ⚠ 原来没有「不少于 / 不多于 / 不足 / 超过」—— 而卡面里「不少于」是最常见的写法之一，
  //     于是这些条件**一条都认不出来**（buildCondition 返回 null）→ 上层把条件子句当动作的一部分，
  //     目标/阵营全错（「若敌方手牌不少于3张，抽一张牌」会编成"给对手抽一张"）。
  const CN_LT = '不大于|不超过|不多于|小于等于|至多|最多|不足|低于|≤';
  const CN_GT = '不小于|不少于|不低于|大于等于|至少|超过|多于|高于|≥';
  const CN_EQ = '等于|为|是|＝|==';
  const CN_NUM = '([一二两三四五六七八九十\\d]+)';
  function cmpFromWord(word) {
    const w = String(word || '').trim();
    if (/^(大于|超过|多于|高于)$/.test(w)) return '>';
    if (/^(小于|不足|低于)$/.test(w)) return '<';
    if (new RegExp('^(' + CN_EQ + ')$').test(w)) return '==';
    if (new RegExp('^(' + CN_GT + ')$').test(w)) return '>=';
    if (new RegExp('^(' + CN_LT + ')$').test(w)) return '<=';
    return null;
  }
  const COND_RULES = [
    // ★★ 「（如果）场上有X」→ controlsType >= 1。
    //   X 可以是兵种/词条词（「场上有友方坦克」），**也可以是卡名**（「场上有"空投师"」）。
    //   以前这类没有条件规则认领 → buildCondition 返回 null → 上层把条件子句当**动作**解析：
    //   "场上（的）空投师"被当成目标 → 后面的"获得+2+2"打到那张卡身上，条件整个丢掉。
    { re: /^\s*(?:如果|若)?\s*场上有\s*([^，,。]{2,12})\s*$/,
      build: (m) => {
        const raw = String(m[1] || '').replace(/[“”"「」]/g, '').trim();
        if (!raw) return null;
        const side = /敌|对方|对手/.test(raw) ? 'enemy' : 'self';
        const w = raw.replace(/^(?:友方|我方|敌方|对方|对手)/, '').replace(/(?:的)?单位$/, '').trim();
        if (!w || /^(?:任意|任何|单位|目标)$/.test(w)) return null;
        const f = P.traitFilterOf(w) || P.typeFilterFromText(w) || { nameIncludes: w };
        return { op: 'controlsType', side: side, cmp: '>=', value: 1, filter: f };
      } },
    // 「（若）无剩余指挥点」= 指挥点 == 0（是一种省事的俗写，必须单独列一条）
    //   ⚠ 「无剩余指挥点」这种**不带「若」**的省写以前认不出来（只认 若无/没有/不剩/用光/耗尽）
    //     → 条件被当动作解析 / 整句"未实现"（10(b)坦克「若无剩余指挥点，则行动花费减为0」）。
    { re: /(?:若无|没有|不剩|用光|耗尽|无|没)剩余?指挥点|剩余指挥点为?\s*0/, build: () => ({ op: 'compare', left: { kredits: 'self' }, cmp: '==', value: 0 }) },
    // 剩余指挥点 / 指挥点 —— 统一走 compare，比较符由原文决定（过去只能写 ≥）
    { re: new RegExp('剩余指挥点\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'compare', left: { kredits: 'self' }, cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    { re: new RegExp('(?:剩余)?指挥点\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'compare', left: { kredits: 'self' }, cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    // 「（单位）行动花费 X」——比较某单位的行动花费（缺省取事件主角/本单位）
    { re: new RegExp('(?:单位)?行动花费\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'opCost', cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    // ★ 数量类条件补全：「若敌方手牌不少于3张」「如果场上友方单位不少于2个」。
    //   ⚠ 这两类**最常见的条件**以前完全没有规则认领 → buildCondition 返回 null →
    //     上层只能把条件子句留在句子里当**动作**的一部分解析 →
    //     目标/阵营全错（「若敌方手牌不少于3张，抽一张牌」会编成"给对手抽一张"）——静默错误。
    //   每补一条规则 = 给"条件 × 动作"的排列组合补上一格。
    { re: new RegExp('(友方|我方|敌方|对方|对手)?\\s*(?:的)?\\s*(?:手牌|手中有?)\\s*(?:数量|张数)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM + '\\s*张?'),
      build: (m) => ({ op: 'handSize', cmp: cmpFromWord(m[2]), value: num(m[3]), side: /敌|对方|对手/.test(m[1] || '') ? 'enemy' : 'self' }) },
    { re: new RegExp('(友方|我方|敌方|对方|对手)?\\s*(?:的)?\\s*(?:卡组|牌库)\\s*(?:中)?(?:有)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'deckSize', cmp: cmpFromWord(m[2]), value: num(m[3]), side: /敌|对方|对手/.test(m[1] || '') ? 'enemy' : 'self' }) },
    // 「（场上）<阵营><词条>单位（数量）不少于N个」→ unitCount + 兵种/词条过滤
    //   ⚠ 比较词必须在「单位」**之后**：否则会抢走「你控制多于三个帝安单位」这类写法。
    { re: new RegExp('(?:场上)?\\s*(友方|我方|敌方|对方|对手)?\\s*(?:的)?\\s*([\\u4e00-\\u9fa5]{0,6}?)\\s*单位(?:数量|个数)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM + '\\s*(?:个|名|辆|架|艘|支)?'),
      build: (m) => {
        const side = /敌|对方|对手/.test(m[1] || '') ? 'enemy' : 'friendly';
        const w = (m[2] || '').replace(/单位$/, '').trim();
        const f = w ? (P.traitFilterOf(w) || P.typeFilterFromText(w)) : null;
        const spec = { side: side };
        if (f) spec.filter = f;
        return { op: 'unitCount', cmp: cmpFromWord(m[3]), value: num(m[4]), spec: spec };
      } },
    // 手牌数 / 卡组数（无阵营的省写形式，兜底）
    { re: new RegExp('(?:手牌|手中有?)\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'handSize', cmp: cmpFromWord(m[1]), value: num(m[2]), side: 'self' }) },
    { re: new RegExp('(?:卡组|牌库)\\s*(?:中)?(?:有)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'deckSize', cmp: cmpFromWord(m[1]), value: num(m[2]), side: 'self' }) },
    // 总部血量
    { re: new RegExp('(?:敌方|对方)?总部(?:防御力|生命|血量)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'compare', left: { hq: /敌方|对方/.test(m[0]) ? 'enemy' : 'enemy' }, cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    // ★ 单位身材比较：「（若）防御力不小于5」「若其攻击力大于3」「如果目标防御力不小于8」
    //   取值走**已有的通用 stat 取值**（num() 支持 {stat:'defense'|'attack', of:'self'|'target'}），
    //   不新增 op —— 这里只负责把中文翻译成 compare 的词汇。
    //   ⚠ 主语决定 of：带「其/该单位/该目标/目标/它」= 选定的那个单位(target)；
    //     否则是本单位(self)。搞反会让条件拿错单位比、静默恒真/恒假。
    { re: new RegExp('(?:其|该单位|该目标|这个单位|它|目标|敌方单位)?\\s*(?:的)?防御力\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'compare', left: { stat: 'defense', of: /其|该单位|该目标|这个单位|它|目标|敌方单位/.test(m[0]) ? 'target' : 'self' }, cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    { re: new RegExp('(?:其|该单位|该目标|这个单位|它|目标|敌方单位)?\\s*(?:的)?攻击力\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'compare', left: { stat: 'attack', of: /其|该单位|该目标|这个单位|它|目标|敌方单位/.test(m[0]) ? 'target' : 'self' }, cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    // 回合数
    { re: new RegExp('回合数?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM),
      build: (m) => ({ op: 'turnAtLeast', cmp: cmpFromWord(m[1]), value: num(m[2]) }) },
    // 「若可能将所有友方单位移至前线」= 我方前线还有空位
    { re: /(?:可能)?将所有?友方单位(?:都)?移(?:至|到)前线/, build: () => ({ op: 'hasRoom', side: 'self', zone: 'frontline' }) },
    // ── 以下是要看事件/场面的，拆不开 → 独立条件原语 ──
    { re: /被消灭|被摧毁|已阵亡|被击杀/, build: () => ({ op: 'targetDead' }) },
    // ★ 「其已被压制 / 该单位已被压制」→ targetPinned（看**选定目标**的压制状态）
    //   抗敌：「压制一个敌方单位，如果其已被压制，随机消灭一个敌方单位」
    //   ⚠ 必须放在 /(?:仍)?存活|未…/ 之后、且**不能**被"压制"这个动作词抢走 ——
    //   buildCondition 只在**条件子句**上跑，所以这里安全。
    { re: /(?:其|该单位|该目标|这个单位|它)?\s*(?:已经|已|处于)?\s*(?:被)?压制(?:状态)?/, build: () => ({ op: 'targetPinned' }) },
    { re: /(?:仍)?存活|未(?:被)?消灭|未阵亡/, build: () => ({ op: 'targetAlive', target: 'self' }) },
    // ★ 「（如果）是单位 / 是总部」→ targetIsUnit / targetIsHQ（看本次声明的目标是什么）
    { re: /^(?:它|其|该目标|该单位|目标|这)?\s*是(?:一个|个)?\s*(?:单位|目标单位)\s*$/, build: () => ({ op: 'targetIsUnit' }) },
    { re: /^(?:它|其|该目标|这)?\s*是(?:敌方|对方)?\s*总部\s*$/, build: () => ({ op: 'targetIsHQ' }) },
    { re: /前线没有单位|前线为空/, build: () => ({ op: 'frontlineEmpty', side: 'self' }) },
    // ★ 「（若）本单位/这张卡 在前线（/支援阵线）」→ unitInZone（看自己所在的阵线）
    { re: /(?:本单位?|这张卡|该单位|它)(?:是否)?在\s*(前线)/, build: () => ({ op: 'unitInZone', target: 'self', zone: 'frontline' }) },
    { re: /(?:本单位?|这张卡|该单位|它)(?:是否)?在\s*(支援阵线|支援线)/, build: () => ({ op: 'unitInZone', target: 'self', zone: 'support' }) },
    { re: /有(?:其他)?同名单位/, build: () => ({ op: 'unitCount', cmp: '>=', value: 2, spec: { side: 'friendly', filter: { sameName: true } } }) },
    { re: /(?:其|该卡|这张卡)?不在构筑(?:内|里)/, build: () => ({ op: 'discoveredCardNotInDeck' }) },
    { re: /敌方手中?具有?明牌|对手有?明牌/, build: () => ({ op: 'revealedInEnemyHand' }) },
    { re: /(?:已经)?控制前线|占据前线/, build: () => ({ op: 'controlFrontline', side: 'self' }) },
    { re: /友方单位(?:数量)?(?:少于|小于)敌方/, build: () => ({ op: 'unitCountLess' }) },
    // 「（你）控制多于N个<阵营>单位」→ unitCount > N + set 过滤（帝安 = deran）
    //   ⚠ 必须排在下一条「控制<兵种>」之前：否则「控制多于N个帝安单位」会被
    //     当成"控制帝安单位"（兵种词解析失败 → 条件变成"控制任意单位"）。
    { re: /(?:你|我方|友方)?控制多于\s*([一二两三四五六七八九十\d]+)\s*个\s*([\u4e00-\u9fa5A-Za-z]{1,4})单位/,
      build: (m) => { const set = SET_WORD_FILTER[m[2]] || null; return { op: 'unitCount', cmp: '>', value: num(m[1]), spec: { side: 'friendly', filter: set ? { set: set } : {} } }; } },
    // ★ 「（如果）友方/你 控制<兵种>」→ controlsType（「如果友方控制巡航舰，则随机消灭一个敌方单位」）
    //   这是**条件子句**的经典写法，之前**没有任何规则认领** → buildCondition 返回 null →
    //   上层的 "如果…" 分支被迫 `cond = null; body = 原始整句`，整句于是落到通用动作解析：
    //   动作词命中"消灭"，再从**整串**里扫配额/阵营/限定词，于是把条件子句里的
    //   「一个」「友方」「巡航舰」误当成**目标**的属性 → 产出 destroy{random, 友方, cruiser}
    //   —— 条件丢失 + 目标反了（本条修复的就是这个）。
    //   - 兵种词走 P.typeFilterFromText（长词优先，权威表 UNIT_TYPES），支持巡航舰/太空战机/空军/陆军…
    //   - 不带兵种词（或"任意单位"）→ 退回"控制任意单位"（unitCount >= 1）。
    //   - 明确排除带数量比较词的写法（"控制多于/少于/至少N个"），那些由上面的规则或
    //     通用 compare 负责，避免抢词。
    { re: /^(?:如果|若|假如)?\s*(?:你|我方|友方|自己)?\s*控制\s*(?![多少于不少于至多最多])\s*([\u4e00-\u9fa5A-Za-z]{0,6}?)\s*(?:单位|的)?\s*$/,
      build: (m) => {
        const kv = m[1] ? P.traitFilterOf(m[1]) : null;      // 兵种 / 战斗词条 / 系列名 都算
        const tf = P.typeFilterFromText(m[1] || '');
        const c = { op: 'controlsType', side: 'self', cmp: '>=', value: 1 };
        if (kv) c.filter = kv;
        else if (tf && tf.unitType) c.unitType = tf.unitType;
        else if (m[1] && !/任意|任何|单位/.test(m[1])) return null;   // 有词但认不出兵种 → 不硬猜
        return c;
      } },
    // ★ 「场上有（友方/敌方）X单位」→ controlsType >= 1（「若场上有友方轰炸机，降低等同于…费用」）。
    //   这条以前也认不出来 → 条件被当成动作的一部分 / 整句"未实现"。
    { re: /(?:场上)?\s*有\s*(友方|我方|敌方|对方|对手)?\s*(?:的)?\s*([\u4e00-\u9fa5]{1,6}?)\s*(?:单位|的)?\s*$/,
      build: (m) => {
        const w = (m[2] || '').replace(/单位$/, '').trim();
        if (!w || /^(?:任意|任何|单位)$/.test(w)) return null;      // 认不出词 → 不硬猜
        const side = /敌|对方|对手/.test(m[1] || '') ? 'enemy' : 'self';
        const f = P.traitFilterOf(w) || P.typeFilterFromText(w);
        if (f) return { op: 'controlsType', side: side, cmp: '>=', value: 1, filter: f };
        // ★ 不是兵种/词条/系列词 → **当卡名处理**（「如果场上有"空投师"」这类）。
        //   以前这里直接 return null → 整句落到"选择原语"上，把"场上的空投师"当成**目标**，
        //   于是后面的"获得+2+2"打到了那张卡身上，而不是自己（条件也整个丢了）。
        if (/^[\u4e00-\u9fa5A-Za-z0-9·「」“”"\-]{2,12}$/.test(w)) {
          return { op: 'controlsType', side: side, cmp: '>=', value: 1, filter: { nameIncludes: w } };
        }
        return null;
      } },
    // ★ 「（如果）友方占领了前线」→ controlFrontline
    { re: /(?:友方|我方|你)?\s*(?:占领|占据|控制)了?\s*前线/, build: () => ({ op: 'controlFrontline', side: 'self' }) },
    // ★ 「（如果）你控制数量不小于N的<词条>单位」→ unitCount（**数量词在兵种词前**的语序）
    { re: new RegExp('(?:你|我方|友方|敌方|对方)?\\s*控制\\s*(?:数量|个数)?\\s*(' + CN_GT + '|' + CN_LT + '|' + CN_EQ + '|大于|小于)\\s*' + CN_NUM + '\\s*(?:个|名|辆|架|艘|支)?\\s*(?:的)?\\s*([\\u4e00-\\u9fa5A-Za-z]{0,8}?)\\s*(?:单位)?\\s*$'),
      build: (m) => {
        const w = (m[3] || '').replace(/单位$/, '').trim();
        const spec = { side: 'friendly' };
        // ⚠ 顺序：拉丁系列词优先（「Mk坦克」必须是 {name:'Mk',unitType:'tank'}；
        //   交给 typeFilterFromText 会只剩 unitType:'tank'，把 Mk 限定丢掉）
        const f = w ? (latinTraitFilter(w) || P.traitFilterOf(w) || P.typeFilterFromText(w)) : null;
        if (f) spec.filter = f;
        return { op: 'unitCount', cmp: cmpFromWord(m[1]), value: num(m[2]), spec: spec };
      } },
    // ★ 「（若）是本回合部署的第一个单位」→ firstUnitThisTurn（引擎已有该条件原语）
    { re: /(?:是|为)?\s*本回合(?:部署|打出)的?(?:第一个|第1个|首个)\s*单位/, build: () => ({ op: 'firstUnitThisTurn' }) },
    // ★ 「（若）前线有（友方/我方）单位」→ 我方前线单位数 >= 1
    { re: /(?:我方|友方|你)?\s*前线(?:上)?有\s*(?:友方|我方)?\s*单位/, build: () => ({ op: 'unitCount', cmp: '>=', value: 1, spec: { side: 'friendly', zone: 'frontline' } }) },
    // 「场上存在其它X」→ unitCount >= 2（同名，含自己）
    { re: /场上存在其(?:它|他)([\u4e00-\u9fa5A-Za-z0-9]{2,12})/, build: () => ({ op: 'unitCount', cmp: '>=', value: 2, spec: { side: 'friendly', filter: { sameName: true } } }) },
    // ★ 「（如果/若）(场上)存在X」→ unitCount >= 1（USG第5师「如果存在空投师，获得+2+2」）。
    //   与上一条的区别：没有"其它"= 不排除自己 → 数量下限 1。
    //   兵种/词条/系列词走同一份词表；都不是 → 用**卡池真名**兜底（nameIncludes），
    //   卡池里没有的词**不硬猜**（返回 null 让上层如实报"条件认不出"）。
    { re: /^(?:场上)?\s*存在\s*(?:一(?:个|名|辆|架|艘))?\s*(友方|我方|敌方|对方|对手)?\s*([\u4e00-\u9fa5A-Za-z0-9·]{2,12})\s*$/,
      build: (m) => {
        const w = String(m[2] || '').trim();
        if (!w || /^(?:任意|任何|单位)$/.test(w)) return null;
        const side = /敌|对方|对手/.test(m[1] || '') ? 'enemy' : 'friendly';
        const f = P.traitFilterOf(w) || P.typeFilterFromText(w);
        const spec = { side: side };
        if (f) spec.filter = f;
        else if (poolHasName(w)) spec.filter = { nameIncludes: w };
        else return null;
        return { op: 'unitCount', cmp: '>=', value: 1, spec: spec };
      } },
  ];
  P.COND_RULES = COND_RULES;
  P.cmpFromWord = cmpFromWord;
  // 一句条件文字 → 条件原语；认不出返回 null（调用方会退回"无条件"）
  function buildCondition(text) {
    let t = String(text || '').trim().replace(/^[，,]?\s*(?:则|就|那么|那)\s*/, '');
    if (!t) return null;
    // ★ 「A，并且 B」/「A 且 B」/「A，同时 B」→ and 复合条件。
    //   卡面常把两个条件并列（「如果友方占领了前线，并且你控制数量不小于2的Mk坦克，则…」），
    //   以前只认第一个子句、甚至整句认不出来 → 条件丢失或"未实现"。
    //   ⚠ 有一个子句认不出来就**整体放弃** —— 绝不只吞一半条件（那会变成"看起来有条件"的错效果）。
    const parts0 = t.split(/\s*(?:，|,)?\s*(?:并且|而且|同时|以及|且)\s*/)
      .map(function (x) { return x.replace(/^[，,]\s*/, '').trim(); }).filter(Boolean);
    if (parts0.length > 1) {
      const items = parts0.map(function (x) { return buildCondition(x); });
      if (items.length === parts0.length && items.every(Boolean)) return { op: 'and', items: items };
      return null;
    }
    for (const r of COND_RULES) {
      const m = t.match(r.re);
      if (m) { const c = r.build(m); if (c) return c; }
    }
    return null;
  }
  P.buildCondition = buildCondition;

  /* --------------------------------------------------------- 组合解析 */
  // 返回 { actions:[...], targets:[...] } 或 null
  // 「友方部署一辆Mk坦克后，升为老兵」→ trigger:unitDeployed + 条件(eventUnitIs)
  P.parseDeployUpgrade = function (sentence) {
    const s = String(sentence || '');
    // 数量词可选：「一辆 / 两辆 / 2辆 / 三名 …」。⚠ 旧正则只认「一辆」，
    //   「两辆Mk坦克」的"两"掉进名字捕获组 → name:'两辆Mk'（用户截图实锤）。
    const m = s.match(/友方(?:单位)?部署\s*(?:([一二两三四五六七八九十\d]+)\s*[辆个张名架艘])?\s*[“"]?([^”"，,。\s]+?)[""]?\s*(?:后|之后|时)[，,]?\s*(.*)$/);
    if (!m) return null;
    const count = m[1] ? (num(m[1]) || 1) : 1;
    const want = m[2];
    const body = P.parse(m[3], { eventRef: 'eventUnit' });
    if (!body) return P.unparsed(s, '部署升级后的效果无法识别');
    if (body.unparsed || body.uncertain || !(body.actions && body.actions.length) || body.cardFields) {
      return P.unparsed(s, '部署升级效果未能完整解析');
    }
    // 拆「系列名 + 兵种词」（长词优先，避免「突击舰」被「舰」截胡）：
    //   「Mk坦克」→ nameIncludes:'Mk' + unitType:'tank'
    //   「坦克」  → unitType:'tank'（任意坦克，无系列限定）
    //   「Mk」    → nameIncludes:'Mk'
    const TYPES = P.UNIT_TYPES || {};
    let unitType = null, nameIncludes = null, filter = null;
    const keys = Object.keys(TYPES).sort(function (a, b) { return b.length - a.length; });
    for (const k of keys) {
      const idx = want.indexOf(k);
      if (idx >= 0) {
        unitType = TYPES[k];
        const rest = (want.slice(0, idx) + want.slice(idx + k.length)).trim();
        if (rest) nameIncludes = rest;
        break;
      }
    }
    // ★ 「游击单位」这类**词条 + 单位**：走同一份词表（P.traitFilterOf），产出 matchFilter 口径的 filter。
    //   ⚠ 原来一律落到 nameIncludes:'游击单位' —— 名字里没有这五个字，永远升不了级。
    //   「任意单位 / 一个单位」= 不限定（filter 为空）。
    if (!unitType && !nameIncludes && want) {
      const bare = String(want)
        .replace(/^(?:其他|其它|别的|任意|任何)/, '')
        .replace(/单位$/, '')
        .replace(/^(?:一|1)[个名辆架艘支]/, '')
        .trim();
      const tf = bare ? P.traitFilterOf(bare) : null;
      if (tf) filter = tf;
      else if (bare && poolHasName(bare)) nameIncludes = bare;          // 「Mk」这种按卡池名兜底
      else if (bare && !/^(?:任意|任何|其他)$/.test(bare)) nameIncludes = want;   // 认不出的原样保留（如实）
    }
    // ★ 升级条件必须写进**卡面级字段 `upgradeOn`**：引擎 upgradeCheck 读 def.upgradeOn
    //   做 `upgradeProgress` 累计，到 `count` 才 tryVeteran。effect 的 condition
    //   是纯布尔判定，**没有"累计 N 次"的能力** —— count 语义只能走这里。
    const up = { trigger: 'unitDeployed', side: 'self', count: count, excludeSelf: true };
    if (nameIncludes) up.nameIncludes = nameIncludes;
    if (unitType) up.unitType = unitType;
    if (filter) up.filter = filter;
    return {
      trigger: 'unitDeployed',
      actions: body.actions,
      targets: body.targets,
      cardFields: { upgradeOn: up },
    };
  };

  P.expandAction = function (act) {
    if (act && act.op === 'grantAndBuff') {
      const kw = (P.KW_CN || {})[act.keyword] || act.keyword;
      return [{ op: 'grant', keyword: kw, target: 't1' }, { op: 'buff', attack: act.attack, defense: act.defense, target: 't1' }];
    }
    return null;
  };

  // 按顶层分隔符拆句：引号“…”里的逗号不当作分隔符（"总部获得：“某时，某效果”"要被整体看待）
  function splitTop(s) {
    const guards = [];
    let masked = String(s).replace(/[“「][\s\S]*?[”」]/g, function (m) {
      guards.push(m);
      return '\u0001' + (guards.length - 1) + '\u0001';
    });
    // ★ 「和」后面跟**动作词**时才当分隔符：「造成2点伤害和抽一张牌」要拆成两段动作。
    //   ⚠ 不能无条件按「和」拆：名词并列（USG和星盟单位 / 攻击力和防御力）、
    //     并列增益（获得+1+1和奋战 / 获得游击和+2+2）都在"和"上连着，拆了会散架。
    // ★★ 「选择并X」是**一个连动动作**（选择并弃 / 选择并消灭 / 选择并移除…），
    //   这里的"并"不是两个动作的分隔符。不保护的话会拆成「选择」+「弃一张单位」：
    //   前段解析不出动作 → allOk=false → **整句放弃拆句** → 只剩一条规则通吃，
    //   另一半动作**静默消失**（warnings 还是空的，看着像解析成功）。
    //   实测（2026-09-24）：「选择并弃一张单位，获得等同于其防御力的指挥点」
    //   曾只产出 gainKredits，弃牌动作整个没了 → 打出去是 0 效果。
    masked = masked.replace(/选择并/g, '选择\u0002');
    return masked.split(/(?:并且|而且|同时|以及|然后|并|且)|和(?=(?:抽|获得|得到|消灭|摧毁|造成|压制|抑制|移|召唤|开发|随机|额外|弃|洗|恢复|治疗|复制|失去|选择|对|将|使|令|回|加入|使用|攻击|结束|翻开|揭示))|，(?=使)|,(?=使)|，|,|；|;|：|:/)
      .map(function (x) { return x.trim().replace(/\u0002/g, '并').replace(/\u0001(\d+)\u0001/g, function (_, i) { return guards[+i]; }); })
      .filter(Boolean);
  }

  /* 「如果/若…」条件子句可以出现在句子**中段**（「额外获得三个指挥点槽，如果你控制
   * 巡航舰，随机消灭一个敌方单位」），此时必须把它和**后面的动作**当一个整体交给
   * 0.6) 条件分支，**不能**在 0) 的顶层拆句里切开 —— 否则条件子句成了孤立片段
   * （"如果你控制巡航舰" 自己没动作 → P.parse 返回 null → allOk=false），
   * 拆句整体失败后**整串**落到通用解析，于是条件丢失 + 目标被条件词污染。
   * 这里检测"存在中段条件"：分隔符（，；等）后面紧跟条件连词。
   * 返回该条件连词的起始下标，没有则返回 -1。 */
  // ⚠ 「若可能 / 若有空位 / 若有空间」**不是条件**（是"尽可能"的修饰语）→ 用负向前瞻排除。
  //   不排除的话：`；若可能，` 会被判成"中段条件句" → **整句跳过拆分** →
  //   只落一条规则、后面的动作整段丢掉（实测「…；若可能，将其中一张加入前线，并使其获得+1+1」）。
  const COND_LEAD_RE = /(?:^|[，,；;：:])\s*(如果|若|假如|只要|当)\s*(?!可能|有空位|有空间)(?!.*(?:否则))/;
  function midSentenceCondIndex(s) {
    const m = String(s).match(COND_LEAD_RE);
    if (!m) return -1;
    // 只认**非句首**的条件（句首条件本来就由 0.6) 处理，0) 也已被 `^(如果|若…)` 拦住）
    return m.index + (m[0].length - m[0].replace(/^[，,；;：:]\s*/, '').length) - (m[1] ? m[1].length : 0);
  }
  function hasMidCondition(s) {
    const str = String(s);
    if (/^(?:如果|若|假如)/.test(str)) return false;   // 句首条件：交给 0.6)
    // ⚠ 同样要排除「若可能 / 若有空位」——它不是条件，误判会让整句不拆句（见 COND_LEAD_RE 的注释）
    return /[，,；;：:]\s*(?:如果|若|假如|只要|当)\s*(?!可能|有空位|有空间)/.test(str);
  }


  /* ================================================================ 牌Q 卡包句式兜底（2026-09-25 导入）
   * 位置：parse() 里**所有既有解析都失败之后**（见末尾 paiqRules 调用）。
   *   因此这里只会把「未实现」变成「能实现」，**不可能抢掉任何既有句式**（零回归风险）。
   * 约定：只产出引擎已有的 op；要玩家选目标的句式自己往 declare 里推一条。
   * 加规则写在这里，并在图注里写清样例卡。 */
  function paiqDeclare(declare, side, filter, label) {
    const id = 't' + (declare.length + 1);
    declare.push({ id: id, side: side || 'any', kind: 'unit',
      filter: (filter && Object.keys(filter).length) ? filter : undefined,
      prompt: '选择一个' + (label || '单位') });
    return id;
  }
  function paiqRules(s0, opts, declare) {
    //   「反之/否则」开头 = "否则…"，与直接写动作同义（放良先锋458组）。
    const s = String(s0 || '').replace(/^[，,、；;]\s*/, '').replace(/^(?:反之|否则)\s*[，,]?\s*/, '');
    let m;
    const ALLY = { sel: 'all', side: 'friendly' };
    const ENEMY = { sel: 'all', side: 'enemy' };

    /* 1) 失去 N 个指挥点 / 指挥点槽 —— OPS.loseKredits / OPS.loseKreditSlots
     *   ⚠ 这两个 op 默认 side 是**敌方**，必须显式 side:'self'。
     *   样例：调度707师「失去 2 个指挥点」、侦查4.2部队「失去 2 个指挥点槽」 */
    m = s.match(/^失去\s*([一二两三四五六七八九十\d]+)\s*个?\s*指挥点槽/);
    if (m) return { actions: [{ op: 'loseKreditSlots', amount: num(m[1]), side: 'self' }] };
    m = s.match(/^失去\s*([一二两三四五六七八九十\d]+)\s*个?\s*指挥点/);
    if (m) return { actions: [{ op: 'loseKredits', amount: num(m[1]), side: 'self' }] };

    /* 2) 完全修复 —— OPS.heal（amount 给足 = 修满）
     *   样例：山地第17医疗团「完全修复所有友方单位」、kuom-7 特「并完全修复」 */
    if (/^(?:并|将其|使其)?完全(?:修复|治疗)/.test(s)) {
      if (/所有|全部|全体/.test(s)) {
        const t = { sel: 'all', side: /敌方|对方/.test(s) ? 'enemy' : 'friendly' };
        if (/山地/.test(s)) t.filter = { keyword: 'alpine' };
        if (/太空/.test(s)) t.filter = { unitType: 'space' };
        return { actions: [{ op: 'heal', amount: 999, target: t }] };
      }
      return { actions: [{ op: 'heal', amount: 999, target: { sel: 'self' } }] };
    }

    /* 3) 使(所有)手牌(获得)减 N 花费 / 手牌具有 -N 花费 —— OPS.handOpCostMod
     *   样例：步兵114师「使所有手牌获得减1花费」、缄默行动「使手牌具有 -1 花费…」 */
    m = s.match(/^(?:使)?(?:所有)?手牌(?:获得)?(?:减|减少|-)\s*([一二两三四五六七八九十\d]+)\s*花费/);
    if (m) return { actions: [{ op: 'handOpCostMod', amount: -num(m[1]), side: 'self' }] };
    m = s.match(/^(?:使)?(?:所有)?手牌具有\s*-\s*([一二两三四五六七八九十\d]+)\s*花费/);
    if (m) return { actions: [{ op: 'handOpCostMod', amount: -num(m[1]), side: 'self' }] };

    /* 4) 本单位攻击后 ±N（防御力/攻击力）—— OPS.buff（「攻击后」触发由 compiler 剥）
     *   样例：kuom-551「本单位攻击后 +2 防御力」 */
    if (/^(?:本单位)?攻击后/.test(s)) {
      m = s.match(/^(?:本单位)?攻击后\s*[，,]?\s*(?:获得)?\s*\+\s*([一二两三四五六七八九十\d]+)\s*(?:\+\s*([一二两三四五六七八九十\d]+))?\s*(攻击力|防御力)?\s*$/);
      if (m) {
        const a = { op: 'buff', target: { sel: 'self' } };
        if (m[2] != null) { a.attack = num(m[1]); a.defense = num(m[2]); }
        else if (m[3] === '防御力') a.defense = num(m[1]);
        else if (m[3] === '攻击力') a.attack = num(m[1]);
        if (a.attack != null || a.defense != null) return { actions: [a] };
      }
      m = s.match(/^(?:本单位)?攻击后\s*[，,]?\s*失去\s*(?:一个|1\s*个)\s*指挥点\s*$/);
      if (m) return { actions: [{ op: 'loseKredits', amount: 1, side: 'self' }] };
    }

    /* 5) 无法被（敌方）指令指向 / 无法被指令影响 / 无法被部署与指令指向 —— OPS.grantMod ignoreOrders
     *   样例：后勤第312团「无法被指令影响」、布洛尔放良565队「本单位无法被敌方指令指向」 */
    if (/^(?:本单位)?无法被(?:敌方|对方)?(?:部署与|指令与)?指令(?:与部署)?(?:指向|指定|影响)/.test(s))
      return { actions: [{ op: 'grantMod', mod: 'ignoreOrders', target: { sel: 'self' } }] };

    /* 6) 本单位可以移动并攻击 / 可以部署并移动 —— OPS.canMoveAndAttack
     *   样例：撒塔罗斯28师「本单位可以部署并移动」、正奎4516团「本单位可以移动并攻击」 */
    if (/^本单位可以(?:移动并攻击|部署并移动|移动和攻击|部署和移动)/.test(s))
      return { actions: [{ op: 'canMoveAndAttack', target: { sel: 'self' } }] };

    /* 6b) 本单位受到指令重甲减伤 —— 卡面例外（制作者 2026-09-28）：
     *   仅**带这句**的单位，自己的重甲数值对指令伤害同样生效；
     *   普通重甲单位照旧只挡对战伤害。→ grantMod armorVsOrder */
    if (/^(?:本单位|此单位|该单位)?(?:受到)?指令(?:伤害)?(?:时)?(?:的)?重甲(?:减伤|减免)/.test(s))
      return { actions: [{ op: 'grantMod', mod: 'armorVsOrder', value: true, target: { sel: 'self' } }] };

    /* 7) （相邻）友方 X（具有）±N+N / ±N 攻击力|防御力 —— passive aura 身材光环
     *   样例：克莱空降-332旅「相邻友方单位具有+1+ 1」、塔塔梅洛-13坦克「友方步兵+1防御力」 */
    m = s.match(/^(相邻)?\s*友方\s*(相邻)?\s*([\u4e00-\u9fa5]{0,6}?)\s*(?:具有|获得)?\s*\+\s*([一二两三四五六七八九十\d]+)\s*(?:\+\s*([一二两三四五六七八九十\d]+))?\s*(攻击力|防御力)?\s*$/);
    if (m && (m[5] != null || m[6])) {
      const aura = { target: auraTargetOf(m[1] || m[2] || '', m[3]) };
      if (m[5] != null) { aura.attack = num(m[4]); aura.defense = num(m[5]); }
      else if (m[6] === '防御力') aura.defense = num(m[4]);
      else aura.attack = num(m[4]);
      return { actions: [{ _standaloneEffect: { trigger: 'passive', aura: aura } }] };
    }

    /* 8) 若有友方 X 单位则 ±N+N / 若前线有敌方单位，具有<词条> —— passive aura + condition（作用于自己）
     *   样例：克莱空降-13旅「若有友方空中单位则+1+1」、克莱空降-34旅「若前线有敌方单位，具有免疫」 */
    m = s.match(/^若(?:有|存在)\s*友方\s*([\u4e00-\u9fa5]{1,6}?)\s*单位\s*则?\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) {
      const tf = P.typeFilterFromText(m[1]);
      const spec = { side: 'friendly' };
      if (tf && tf.unitType) spec.filter = { unitType: tf.unitType };
      return { actions: [{ _standaloneEffect: { trigger: 'passive',
        condition: { op: 'unitCount', cmp: '>=', value: 1, spec: spec },
        aura: { target: { sel: 'self' }, attack: num(m[2]), defense: num(m[3]) } } }] };
    }
    m = s.match(/^若前线有敌方单位\s*[，,]?\s*(?:则)?\s*(?:具有|获得)\s*([\u4e00-\u9fa5]{2,4})\s*$/);
    //   ⚠ 「免疫」不在 KW_CN（它在 HQ_KW_CN 里），这里补一份小表，别漏。
    const KW_FALLBACK = { 免疫: 'immune' };
    if (m && (KW_CN[m[1]] || KW_FALLBACK[m[1]])) return { actions: [{ _standaloneEffect: { trigger: 'passive',
      condition: { op: 'frontlineEnemyOrFull' },
      aura: { target: { sel: 'self' }, keyword: KW_CN[m[1]] || KW_FALLBACK[m[1]] } } }] };

    /* 9) 将本单位复制进入同一阵线/支援线/前线 —— OPS.summon（self 复制）
     *   样例：克莱空降-692旅「移至前线时，将本单位复制进入同一阵线」 */
    if (/^(?:并且?)?将本单位复制(?:进入|置入|加入)/.test(s))
      return { actions: [{ op: 'summon', count: 1, self: true, side: 'self' }] };

    /* 10) 随机将 N 张<卡名>加入手中 / 加入支援线 —— OPS.addCardToHand / OPS.summon
     *   样例：克莱空降-34旅「随机将2张克莱空降加入手中」 */
    m = s.match(/^(?:随机)?将\s*([一二两三四五六七八九十\d]+)\s*张\s*[“"「]?([^”"「」，,。]{2,14}?)[”"」]?\s*(?:随机)?加入(手中|手牌|支援线|支援阵线|前线)/);
    if (m) {
      const name = m[2].trim();
      if (/手/.test(m[3])) return { actions: [{ op: 'addCardToHand', count: num(m[1]), name: name, side: 'self' }] };
      return { actions: [{ op: 'summon', count: num(m[1]), name: name, side: 'self' }] };
    }

    /* 11) 使支援线所有友方单位具有 -N 行动花费 / 敌方所有单位具有 +N 行动花费 —— OPS.opCostModAll
     *   样例：克莱空降-3585旅「使支援线所有友方单位具有-1行动花费」、特科5581团「则敌方所有单位具有 +1 行动花费」 */
    m = s.match(/^使支援线所有友方单位具有\s*[-·–—]?\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) return { actions: [{ op: 'opCostModAll', amount: -num(m[1]), target: { sel: 'all', side: 'friendly', zone: 'support' } }] };
    m = s.match(/^敌方所有单位具有\s*[+＋·]?\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) return { actions: [{ op: 'opCostModAll', amount: num(m[1]), target: ENEMY }] };
    m = s.match(/^使所有友方单位获得\s*-\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) return { actions: [{ op: 'opCostModAll', amount: -num(m[1]), target: ALLY }] };

    /* 12) 移除一个单位 —— OPS.destroy（项目既有口径：移除 = 消灭，见「移除所有友方单位」）
     *   样例：特科5581团「移除一个单位，若是友方，则具有 +4 攻击力」 */
    m = s.match(/^移除\s*(?:一个|1\s*个)?\s*单位\s*$/);
    if (m) {
      const id = paiqDeclare(declare, 'any', null, '单位');
      return { actions: [{ op: 'destroy', target: id }], targets: declare.slice() };
    }

    /* 13) 随机控制敌方 N 个单位 —— OPS.takeControl
     *   样例：绿皮书计划「随机控制敌方 2 个单位」 */
    m = s.match(/^随机控制敌方\s*([一二两三四五六七八九十\d]+)\s*个单位/);
    if (m) return { actions: [{ op: 'takeControl', target: { sel: 'random', side: 'enemy', count: num(m[1]) } }] };

    /* 14) 裸身材句：±N+N / ±N 攻击力|防御力（触发前缀已被 compiler 剥掉的剩余段）
     *   样例：kuom-551「本单位攻击后 +2 防御力」剥掉前缀后的「+2 防御力」 */
    m = s.match(/^\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)\s*$/);
    if (m) return { actions: [{ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: { sel: 'self' } }] };
    m = s.match(/^\+\s*([一二两三四五六七八九十\d]+)\s*(攻击力|防御力)\s*$/);
    if (m) {
      const a = { op: 'buff', target: { sel: 'self' } };
      if (m[2] === '攻击力') a.attack = num(m[1]); else a.defense = num(m[1]);
      return { actions: [a] };
    }

    /* 15) （若可能，）部署时将本单位移至前线 —— OPS.move（"若可能"由 OPS.move 自带：前线满就跳过）
     *   样例：克莱空降-34旅「若可能，部署时将本单位移至前线」 */
    if (/^(?:若可能[，,]?)?(?:部署时)?将本单位移(?:至|到)(?:前线|下一阵线)/.test(s))
      return { actions: [{ op: 'move', to: 'frontline', target: { sel: 'self' } }] };

    /* 16) 若有（场上）有友方同名单位，则获得等同于其数量的防御力 —— passive aura + 动态数值
     *   样例：BGIT-774「若有场上有友方同名单位则获得等同于其数量的防御力」
     *   ⚠ num() 支持 {count:…} 动态取值，所以这里直接把数量当数值挂进 aura。 */
    if (/^若有(?:场上)?有?友方同名单位则?(?:获得|具有)等同于其数量的防御力/.test(s))
      return { actions: [{ _standaloneEffect: { trigger: 'passive',
        aura: { target: { sel: 'self' }, defense: { count: { sel: 'all', side: 'friendly', filter: { sameName: true } } } } } }] };

    /* 17) 使所有攻击力不大于 N 的友方单位获得（加）M 攻击力 —— OPS.buffAll + filter maxAttack
     *   样例：撒塔罗斯第41指挥团「使所有攻击力不大于4的友方单位获得加1攻击力」 */
    m = s.match(/^使所有攻击力不大于\s*([一二两三四五六七八九十\d]+)\s*的友方单位获得(?:加|得到)?\s*([一二两三四五六七八九十\d]+)\s*攻击力/);
    if (m) return { actions: [{ op: 'buffAll', attack: num(m[2]), target: { sel: 'all', side: 'friendly', filter: { maxAttack: num(m[1]) } } }] };

    /* 18) （若可能）随机控制一个敌方花费不大于 N 的单位 —— OPS.takeControl
     *   样例：布洛尔放良1团「若可能随机控制一个敌方花费不大于 1 的单位」 */
    m = s.match(/^(?:若可能)?随机控制一个?敌方花费不大于\s*([一二两三四五六七八九十\d]+)\s*的单位/);
    if (m) return { actions: [{ op: 'takeControl', target: { sel: 'random', side: 'enemy', filter: { maxCost: num(m[1]) } } }] };

    /* 19) 若没有友方老兵单位则结束回合 —— OPS.conditional + OPS.endTurnNow
     *   样例：摩里尔栈道「若没有友方老兵单位则结束回合」 */
    if (/^若没有友方老兵单位则?结束回合/.test(s))
      return { actions: [{ op: 'conditional',
        condition: { op: 'unitCount', cmp: '==', value: 0, spec: { side: 'friendly', filter: { keyword: 'veteran' } } },
        then: [{ op: 'endTurnNow' }] }] };

    /* 20) （使其）+N 攻击力（并|与|和）-N 行动花费 —— OPS.buff + OPS.opCostMod（共用一个目标）
     *   样例：克莱空降-143旅「友方空降兵部署时，使其+2 攻击力与减 1 行动花费」 */
    m = s.match(/^(?:使)?其?\s*\+\s*([一二两三四五六七八九十\d]+)\s*攻击力\s*(?:并|与|和|,|，)\s*(?:减|减少|-)\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) {
      const tgt = /^使其|^其/.test(s) ? { sel: 'ref', ref: 'eventUnit' } : { sel: 'self' };
      return { actions: [{ op: 'buff', attack: num(m[1]), target: tgt }, { op: 'opCostMod', amount: -num(m[2]), target: tgt }] };
    }

    /* 21) 对敌方总部造成等同于（本单位）攻击力的伤害 —— OPS.damageHQ + num 动态值 {stat:'attack'}
     *   样例：放良装甲精锐1师「对敌方总部造成等同与本单位攻击力的伤害」 */
    if (/^对敌方总部造成等同(?:于|与)?(?:本单位)?攻击力的伤害/.test(s))
      return { actions: [{ op: 'damageHQ', amount: { stat: 'attack', of: 'self' }, side: 'enemy' }] };

    /* 22) 将所有友方单位行动花费设为零 —— OPS.setOpCost
     *   样例：奥利瓦腊！「将所有友方单位行动花费设为零」 */
    if (/^将所有友方单位行动花费设为(?:零|0)/.test(s))
      return { actions: [{ op: 'setOpCost', value: 0, target: ALLY }] };

    /* 23) 使敌方随机弃掉一张花费不大于 N 的牌 —— OPS.discard
     *   样例：U-769「使敌方随机弃掉一张花费不大于 5 的牌」 */
    m = s.match(/^使敌方随机弃掉一张花费不大于\s*([一二两三四五六七八九十\d]+)\s*的(?:牌|卡牌|卡)/);
    if (m) return { actions: [{ op: 'discard', side: 'enemy', mode: 'random', count: 1, filter: { maxCost: num(m[1]) } }] };

    /* 24) （并）使其随机与一个敌方单位战斗 —— OPS.fight
     *   样例：V-9 特「使其随机与一个敌方单位战斗」 */
    if (/^(?:并)?使其随机与一个敌方单位战斗/.test(s))
      return { actions: [{ op: 'fight', target: { sel: 'self' }, with: { sel: 'random', side: 'enemy' } }] };

    /* 25)（续）裸「-N-N」= 自己 -N-N —— OPS.debuff
     *   样例：撒塔罗斯654师「友方单位额外获得指挥点时 -1-1」 */
    m = s.match(/^-\s*([一二两三四五六七八九十\d]+)\s*-\s*([一二两三四五六七八九十\d]+)\s*$/);
    if (m) return { actions: [{ op: 'debuff', attack: num(m[1]), defense: num(m[2]), target: { sel: 'self' } }] };

    /* 26) 将一个单位转换为“X” —— OPS.transformUnit（把一张卡变成别的卡）
     *   样例：特鲁伊13团「部署：将一个单位转换为“近卫143营”」 */
    m = s.match(/^(?:并)?将一?个单位(?:随机)?转换为\s*[“"「]?([^”"「」，,。]{2,14}?)[”"」]?\s*$/);
    if (m) {
      const id = paiqDeclare(declare, 'any', null, '单位');
      return { actions: [{ op: 'transformUnit', target: id, name: m[1].trim() }], targets: declare.slice() };
    }

    /* 27) 使敌方所有指令造成的伤害 -N —— OPS.orderDamageMod
     *   样例：比尔格兹-5型「使敌方所有指令 造成的伤害-1」 */
    m = s.match(/^使敌方所有指令\s*造成的伤害\s*-\s*([一二两三四五六七八九十\d]+)/);
    if (m) return { actions: [{ op: 'orderDamageMod', side: 'enemy', amount: -num(m[1]) }] };

    /* 28) 敌方空军无法攻击 —— passive aura + mod:cannotAttack
     *   样例：比尔格兹-7型「敌方空军无法攻击」 */
    if (/^敌方空军无法攻击/.test(s))
      return { actions: [{ _standaloneEffect: { trigger: 'passive',
        aura: { target: { sel: 'all', side: 'enemy', filter: { unitType: ['fighter', 'spacefighter', 'bomber'] } }, mod: 'cannotAttack' } } }] };

    /* 29) 随机将卡组中 N 张X移至卡组顶 —— OPS.deckToTop
     *   样例：西海岸指挥所「随机将卡组中 2 张海军移至卡组顶」 */
    m = s.match(/^随机将卡组中\s*([一二两三四五六七八九十\d]+)\s*张\s*([\u4e00-\u9fa5A-Za-z]{0,6}?)\s*移至卡组顶/);
    if (m) {
      const w = (m[2] || '').trim();
      const f = {};
      if (w) {
        const t = P.typeFilterFromText(w) || P.traitFilterOf(w);
        if (t) Object.assign(f, t);
        else if (w === '海军') f.unitType = 'cruiser';
      }
      return { actions: [{ op: 'deckToTop', count: num(m[1]), filter: f }] };
    }

    /* 30) 指向一个单位 / 亡计：将其消灭 —— OPS.rememberTarget + {sel:'ref',ref:'remembered'}
     *   样例：交易田本连队「部署：指向一个单位 / 亡计：将其消灭」 */
    if (/^指向一个单位\s*$/.test(s)) {
      const id = paiqDeclare(declare, 'any', null, '单位');
      return { actions: [{ op: 'rememberTarget', holder: 'self', from: id }], targets: declare.slice() };
    }
    if (/^将其消灭\s*$/.test(s))
      return { actions: [{ op: 'destroy', target: { sel: 'ref', ref: 'remembered' } }] };

    /* 31) 若是唯一友方单位，（则）具有 +N+N —— passive aura + condition unitCount==1
     *   样例：撒塔罗斯654师「若是唯一友方单位，具有 +2+2」 */
    m = s.match(/^若是唯一友方单位\s*[，,]?\s*(?:则)?\s*(?:具有|获得)\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) return { actions: [{ _standaloneEffect: { trigger: 'passive',
      condition: { op: 'unitCount', cmp: '==', value: 1, spec: { side: 'friendly' } },
      aura: { target: { sel: 'self' }, attack: num(m[1]), defense: num(m[2]) } } }] };

    /* 32) 使一个单位获得 +N+N，若友方剩余指挥点不小于 M 则改为 +P+Q —— OPS.conditional（带 else）
     *   样例：倪理亲王号 */
    m = s.match(/^使一个单位获得\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)\s*[，,]?\s*若友方剩余指挥点不小于\s*([一二两三四五六七八九十\d]+)\s*则改为\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) {
      const id = paiqDeclare(declare, 'any', null, '单位');
      return { actions: [{ op: 'conditional',
        condition: { op: 'kreditsAtLeast', value: num(m[3]) },
        then: [{ op: 'buff', attack: num(m[4]), defense: num(m[5]), target: id }],
        else: [{ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: id }] }], targets: declare.slice() };
    }

    /* 33) 如果存在敌方空军或炮兵，则消灭本单位 —— OPS.conditional + OPS.destroy
     *   样例：TF465编队「回合结束时，如果存在敌方空军或炮兵，则消灭本单位」 */
    if (/^如果存在敌方(?:空军或炮兵|炮兵或空军)\s*[，,]?\s*则?消灭本单位/.test(s))
      return { actions: [{ op: 'conditional',
        condition: { op: 'or', items: [
          { op: 'controlsType', side: 'enemy', cmp: '>=', value: 1, unitType: ['fighter', 'spacefighter', 'bomber'] },
          { op: 'controlsType', side: 'enemy', cmp: '>=', value: 1, unitType: 'artillery' }] },
        then: [{ op: 'destroy', target: { sel: 'self' } }] }] };

    /* 34) 若本单位以及同名单位为本回合第一个部署的单位，则 +N+N —— OPS.conditional
     *   样例：九族三队侦查 */
    m = s.match(/^若本单位(?:以及|和)同名单位为本回合\s*第一个部署的单位\s*[，,]?\s*则?\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) return { actions: [{ op: 'conditional',
      condition: { op: 'and', items: [
        { op: 'firstUnitThisTurn' },
        { op: 'unitCount', cmp: '>=', value: 1, spec: { side: 'friendly', filter: { sameName: true } } }] },
      then: [{ op: 'buff', attack: num(m[1]), defense: num(m[2]), target: { sel: 'self' } }] }] };

    /* 36) 具有 -N 花费 / （本单位）具有 -N 行动花费 —— OPS.opCostMod（作用于自己）
     *   样例：物资补给内文「具有 -1 行动花费」、克莱空降-3995旅「友方抽牌时具有-1 花费」 */
    m = s.match(/^(?:本单位)?具有\s*[-·–—]\s*([一二两三四五六七八九十\d]+)\s*(?:行动)?花费/);
    if (m) return { actions: [{ op: 'opCostMod', amount: -num(m[1]), target: { sel: 'self' } }] };

    /* 37) 行动花费 +N —— OPS.opCostMod
     *   样例：正奎85部队「本单位冲击后，行动花费 +3」 */
    m = s.match(/^(?:本单位)?行动花费\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) return { actions: [{ op: 'opCostMod', amount: num(m[1]), target: { sel: 'self' } }] };

    /* 38) （随机）将手中一张单位复制进入友方支援阵线 —— OPS.handToField（mode:'random' 由引擎侧支持）
     *   样例：放良装甲精锐1师「随机将手中一张单位复制进入友方支援阵线」 */
    if (/^(?:随机)?将手中一?张单位复制进入(?:友方)?(?:支援阵线|支援线)/.test(s))
      return { actions: [{ op: 'handToField', side: 'self', mode: 'random', to: 'support' }] };

    /* 39) …指挥点不小于 N 则改为 +P+Q（前半句被拆走的容错）—— OPS.conditional
     *   样例：倪理亲王号「指挥点不小于 5 则改为+2+3」 */
    m = s.match(/^(?:若友方剩余)?指挥点不小于\s*([一二两三四五六七八九十\d]+)\s*则改为\s*\+\s*([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)/);
    if (m) {
      const id = paiqDeclare(declare, 'any', null, '单位');
      return { actions: [{ op: 'conditional', condition: { op: 'kreditsAtLeast', value: num(m[1]) },
        then: [{ op: 'buff', attack: num(m[2]), defense: num(m[3]), target: id }] }], targets: declare.slice() };
    }

    /* 40) 随机对一个敌方单位造成 N-M 点伤害 —— OPS.damage + num 的 {rand:[a,b]}
     *   样例：危袭「随机对一个敌方单位造成 3-4 点伤害」 */
    m = s.match(/^随机对一个敌方单位造成\s*([一二两三四五六七八九十\d]+)\s*[-—~]\s*([一二两三四五六七八九十\d]+)\s*点伤害/);
    if (m) return { actions: [{ op: 'damage', amount: { rand: [num(m[1]), num(m[2])] }, target: { sel: 'random', side: 'enemy' } }] };

    /* 47) 随机使一个友方单位 +N 攻击力（/+N+N）—— OPS.buff，目标=随机友方
     *   样例：BP-T-223「友方回合结束时，随机使一个友方单位 +3 攻击力」 */
    m = s.match(/^随机使一个友方单位\s*\+\s*([一二两三四五六七八九十\d]+)\s*(?:\+\s*([一二两三四五六七八九十\d]+))?\s*(攻击力|防御力)?/);
    if (m) {
      const a2 = { op: 'buff', target: { sel: 'random', side: 'friendly' } };
      if (m[2] != null) { a2.attack = num(m[1]); a2.defense = num(m[2]); }
      else if (m[3] === '防御力') a2.defense = num(m[1]);
      else a2.attack = num(m[1]);
      return { actions: [a2] };
    }

    /* 48) 招募令/征召：整句「将 N 张“X”加入支援线（手牌），（此前/此后）每使用一张，额外加入一张」
     *   —— 数量 = N + {playedCount}（本局打出过几张"X"）。OPS.summon / addCardToHand */
    m = s.match(/^将\s*([一二两三四五六七八九十\d]+)\s*张\s*[“"「]?([^”"「」，,。]{1,12}?)[”"」]?\s*加入(支援线|支援阵线|手牌|手中)[，,]?\s*(?:此前|此后)?每使用(?:过)?一张\s*[，,]?\s*额外加入一张/);
    if (m) {
      const nm = m[2].trim();
      const key = (opts && opts.cardName) || nm;
      if (/手/.test(m[3])) return { actions: [{ op: 'addCardToHand', name: nm, count: { plus: [num(m[1]), { playedCount: { name: key } }] }, side: 'self' }] };
      return { actions: [{ op: 'summon', name: nm, count: { plus: [num(m[1]), { playedCount: { name: key } }] }, side: 'self' }] };
    }
    m = s.match(/^将一张\s*[“"「]?([^”"「」，,。]{1,12}?)[”"」]?\s*加入手牌[，,]?\s*此后每使用过一张\s*[“"「]?([^”"「」，,。]{1,12}?)[”"」]?\s*额外加入一张/);
    if (m) {
      const nm = m[1].trim(), key = m[2].trim();
      return { actions: [{ op: 'addCardToHand', name: nm, count: { plus: [1, { playedCount: { name: key } }] }, side: 'self' }] };
    }

    /* 66) 使敌方支援线所有单位获得“本单位被指向或攻击时，消灭本单位” —— OPS.grantEffect × 2（两种事件）
     *   样例：指向式轰击 */
    m = s.match(/^使敌方支援线所有单位获得\s*[“"「]?本单位被指向或攻击时[，,]?\s*消灭本单位[。”"」\s]*$/);
    if (m) {
      const tgt = { sel: 'all', side: 'enemy', zone: 'support' };
      const eff = [{ op: 'destroy', target: { sel: 'self' } }];
      return { actions: [
        { op: 'grantEffect', target: tgt, effectTrigger: 'targeted', effects: eff, label: '被指向时消灭自己' },
        { op: 'grantEffect', target: tgt, effectTrigger: 'attacked', effects: eff, label: '被攻击时消灭自己' }] };
    }

    /* 67) 将随机一张（除本单位的）X 加入同一阵线 —— OPS.summon + filter（按名字包含，池里随机取）
     *   样例：克莱空降-332旅 */
    m = s.match(/^将?随机一张除本单位的\s*([\u4e00-\u9fa5A-Za-z0-9]{1,8}?)\s*(?:单位)?加入同一?\s*阵线/);
    if (m) return { actions: [{ op: 'summon', count: 1, side: 'self', filter: { nameIncludes: m[1].trim() } }] };

    /* 68) 将其反制 —— OPS.countered（真正生效靠打断消费） */
    if (/^将?其?反制\s*$/.test(s)) return { actions: [{ op: 'countered' }] };

    /* 64) 预报 / 天气 —— OPS.forecast / OPS.setWeather */
    if (/^(?:部署[:：]?)?预报\s*$/.test(s)) return { actions: [{ op: 'forecast', side: 'self' }] };
    m = s.match(/^(?:使战场|使天气|天气|战场)变为\s*(蓝天|薄雾|狂风|落雪|晴朗)/);
    if (m) return { actions: [{ op: 'setWeather', kind: m[1] === '晴朗' ? 'clear' : ({ 薄雾: 'mist', 狂风: 'gale', 落雪: 'snow' }[m[1]] || 'clear') }] };

    /* 65) 每使用一张，额外加入一张 —— 用**本卡自身**的打出次数（opts.cardName 由 compiler 传下来）
     *   样例：招募令「将 1 张“乔械师”加入支援线，此前每使用一张，额外加入一张」 */
    if (/^每使用(?:过)?一张\s*[，,]?\s*(?:额外)?加入一张\s*$/.test(s)) {
      const self = opts && opts.cardName;
      if (!self) return null;
      return { actions: [{ op: 'addCardToHand', name: self, count: { playedCount: { name: self } }, side: 'self' }] };
    }

    /* 62) 无法增加指挥点槽（压迫的后半句；制作者口径：接下来两回合） */
    if (/^无法增加指挥点槽\s*$/.test(s))
      return { actions: [{ op: 'noKreditSlotForTurns', side: 'enemy', turns: 2 }] };

    /* 63) 若造成伤害则 +N 攻击力（正奎4516团后半句） */
    m = s.match(/^若造成伤害则?\s*\+\s*([一二两三四五六七八九十\d]+)\s*攻击力/);
    if (m) return { actions: [{ op: 'conditional',
      condition: { op: 'compare', left: { stat: 'lastCombatDamage' }, cmp: '>', value: 0 },
      then: [{ op: 'buff', attack: num(m[1]), target: { sel: 'self' } }] }] };

    /* 53) 特殊攻击：使所有友方山地单位以随机顺序与敌方所有单位战斗，并使其受到的对战伤害 -2
     *   制作者口径：就是随机打一个敌方单位，自身没死就继续打。 */
    if (/^使所有友方山地单位以随机顺序与敌方所有单位进行战斗/.test(s))
      return { actions: [
        { _standaloneEffect: { trigger: 'passive', aura: { target: { sel: 'all', side: 'friendly', filter: { keyword: 'alpine' } }, takenDamage: -2 } } },
        { op: 'fightRepeatedly', side: 'self', target: { sel: 'all', side: 'friendly', filter: { keyword: 'alpine' } } }] };

    /* 54) UfG-3010-1（超长句）：花费为 32 → 弃掉敌方所有手牌 → 移除敌方所有单位 →
     *     移除敌方卡组中花费不大于4的卡 → 使敌方构筑中所有卡花费 +3 */
    if (/^友方总部防御力不大于\s*([一二两三四五六七八九十\d]+)\s*时\s*[，,]?\s*花费为\s*([一二两三四五六七八九十\d]+)/.test(s)) {
      const mm = s.match(/^友方总部防御力不大于\s*([一二两三四五六七八九十\d]+)\s*时\s*[，,]?\s*花费为\s*([一二两三四五六七八九十\d]+)/);
      const rest = s.slice(mm[0].length);
      const acts = [{ op: 'discardAll', side: 'enemy' }, { op: 'destroyAll', target: { sel: 'all', side: 'enemy' } },
        { op: 'removeFromEnemyDeck', side: 'enemy', filter: { maxCost: 4 } },
        { op: 'deckCostModAll', side: 'enemy', amount: 3 }];
      return { actions: acts, cardFields: { selfCostSet: mm[2] ? num(mm[2]) : null } };
    }

    /* 55) 压迫：压制敌方所有单位，使其接下来两回合内无法增加指挥点槽 */
    m = s.match(/^压制敌方所有单位\s*[，,]?\s*使其接下来\s*([一二两三四五六七八九十\d]+)\s*回合内无法增加指挥点槽/);
    if (m) return { actions: [
      { op: 'pin', target: { sel: 'all', side: 'enemy' } },
      { op: 'noKreditSlotForTurns', side: 'enemy', turns: num(m[1]) }] };

    /* 56) 本回合内，攻击敌方总部后，能再次行动并具有奋战与收缴 */
    m = s.match(/^攻击敌方总部后\s*[，,]?\s*能再次行动并具有\s*([\u4e00-\u9fa5和与、]{2,12})/);
    if (m) {
      const kws = String(m[1]).split(/[和与、]/).map(function (x) { return KW_CN[x.trim()]; }).filter(Boolean);
      return { actions: [{ op: 'refreshAction', target: { sel: 'self' } }].concat(kws.map(function (k) { return { op: 'grant', keyword: k, target: { sel: 'self' } }; })) };
    }

    /* 57) 抽 N 张牌，随机使其中 M 张获得“<内文>”（使用时生效） */
    m = s.match(/^抽\s*([一二两三四五六七八九十\d]+)\s*张牌\s*[，,]?\s*随机使其中\s*([一二两三四五六七八九十\d]+)\s*张获得\s*[“"「]([\s\S]+?)[”"」]?\s*$/);
    if (m) {
      const inner = P.parse(String(m[3]).trim(), { _noSplit: true });
      return { actions: [{ op: 'draw', count: num(m[1]), side: 'self' }].concat(
        (inner && inner.actions && inner.actions.length) ? [{ op: 'grantEffectToHand', side: 'self', count: num(m[2]), effects: inner.actions }] : []) };
    }

    /* 58) （把卡组里的牌）移至卡组顶…，若其花费为奇数则使其花费 -N */
    if (/^若其花费为奇数则使其花费\s*-?\s*([一二两三四五六七八九十\d]+)/.test(s)) {
      const mm2 = s.match(/^若其花费为奇数则使其花费\s*-?\s*([一二两三四五六七八九十\d]+)/);
      return { actions: [{ op: 'modMovedCost', side: 'self', oddOnly: true, amount: -num(mm2[1]) }] };
    }

    /* 59) 将所有空中单位移除，并使其所有者抽取等量卡牌 */
    if (/^将所有空中单位移除\s*[，,]?\s*并使其所有者抽取等量卡牌/.test(s))
      return { actions: [{ op: 'scrapAirDraw' }] };

    /* 60) 使友方手牌具有 -N 花费并使其获得“<内文>”（亚萍号） */
    m = s.match(/^使友方手牌(?:具有)?\s*-?\s*([一二两三四五六七八九十\d]+)\s*花费并使其获得\s*[“"「]([\s\S]+?)[”"」]?\s*$/);
    if (m) {
      const inner2 = P.parse(String(m[2]).trim(), { _noSplit: true });
      const acts2 = [{ op: 'handOpCostMod', amount: -num(m[1]), side: 'self' }];
      if (inner2 && inner2.actions && inner2.actions.length) acts2.push({ op: 'grantEffectToHand', side: 'self', effects: inner2.actions });
      return { actions: acts2 };
    }

    /* 61) 压制结束时将其 +N 行动花费 / 本单位攻击敌方总部时，抽一张牌，若造成伤害则 +N 攻击力 */
    m = s.match(/^将其\s*\+\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) return { actions: [{ op: 'opCostMod', amount: num(m[1]), target: { sel: 'ref', ref: 'victim' } }] };
    m = s.match(/^抽一张牌\s*[，,]?\s*若造成伤害则?\s*\+\s*([一二两三四五六七八九十\d]+)\s*攻击力/);
    if (m) return { actions: [
      { op: 'draw', count: 1, side: 'self' },
      { op: 'conditional', condition: { op: 'compare', left: { stat: 'lastCombatDamage' }, cmp: '>', value: 0 },
        then: [{ op: 'buff', attack: num(m[1]), target: { sel: 'self' } }] }] };

    /* 49) 卡面级费用修正（只产出 cardFields，无动作）
     *   · 「（若在手中）友方每被消灭一个单位具有 +N 花费（，最多为 M）」→ costModPerFriendlyDeath / costModMax
     *   · 「（它在手牌中时，友方获得额外指挥点时）花费设为 N」→ extraKreditCostSet
     *   ⚠ 必须**在动作之前**判：这些句子没有任何动作，交给通用解析会退化成"选一个单位"。 */
    m = s.match(/^(?:若在手中[，,]?)?友方(?:每|没)被消灭一?个单位具有\s*\+\s*([一二两三四五六七八九十\d]+)\s*(?:点)?\s*花费(?:[，,]?\s*最多为\s*([一二两三四五六七八九十\d]+))?/);
    if (m) return { actions: [], cardFields: { costModPerFriendlyDeath: num(m[1]), costModMax: m[2] != null ? num(m[2]) : undefined } };
    m = s.match(/^若在构筑中[，,]?\s*友方(?:每|没)被消灭一?个单位具有\s*\+\s*([一二两三四五六七八九十\d]+)\s*(?:点)?\s*花费(?:[，,]?\s*最多为\s*([一二两三四五六七八九十\d]+))?/);
    if (m) return { actions: [], cardFields: { costModPerFriendlyDeath: num(m[1]), costModMax: m[2] != null ? num(m[2]) : undefined } };
    m = s.match(/^若本回合额外获得指挥点不大于\s*([一二两三四五六七八九十\d]+)\s*[，,]?\s*无法部署/);
    if (m) return { actions: [], cardFields: { deployGateExtraKreditsMax: num(m[1]) } };
    m = s.match(/^(?:友方额外获得指挥点时[，,]?\s*)?(?:其)?花费设为\s*([一二两三四五六七八九十\d]+)\s*$/);
    if (m) return { actions: [], cardFields: { extraKreditCostSet: num(m[1]) } };

    /* 50) 友方卡组中所有花费不小于 N 的卡牌获得减 M 花费 —— OPS.buffCardsInPiles（piles:'deck'）
     *   样例：步兵213侦查营 */
    m = s.match(/^友方卡组中所有花费不小于\s*([一二两三四五六七八九十\d]+)\s*的卡牌获得减\s*([一二两三四五六七八九十\d]+)\s*花费/);
    if (m) return { actions: [{ op: 'buffCardsInPiles', side: 'self', piles: 'deck', cost: -num(m[2]), filter: { minCost: num(m[1]) } }] };

    /* 51) 攻击敌方总部时，对友方总部造成等量伤害 —— OPS.damageHQ + {stat:'lastCombatDamage'}
     *   样例：克莱空降-822旅（触发前缀「攻击敌方总部时」由 compiler 认） */
    if (/^对友方总部造成等量伤害/.test(s))
      return { actions: [{ op: 'damageHQ', side: 'self', amount: { stat: 'lastCombatDamage' } }] };

    /* 52) 将友方手牌洗入卡组，抽取等量卡牌，对敌方总部造成等同于洗入的海军牌数的伤害 —— 制胜之招 */
    if (/^将友方手牌洗入卡组[，,]?\s*抽取等量卡牌/.test(s))
      return { actions: [
        { op: 'shuffleHandIntoDeck', side: 'self' },
        { op: 'draw', count: { stat: 'shuffledCount' }, side: 'self' },
        { op: 'damageHQ', side: 'enemy', amount: { stat: 'shuffledNavy' } }] };

    /* 41) 触发山地效果 —— OPS.triggerMountain */
    if (/^(?:并)?触发山地效果/.test(s))
      return { actions: [{ op: 'triggerMountain', target: { sel: 'self' } }] };

    /* 42) 具有 <词条> 时（其他）友方单位无法部署 / 无法攻击 —— passive aura + condition + mod */
    m = s.match(/^具有\s*([\u4e00-\u9fa5]{2,4})\s*时\s*(?:其他)?友方(?:单位)?无法(部署单位|攻击)/);
    if (m && KW_CN[m[1]]) {
      const mod = m[2] === '攻击' ? 'cannotAttack' : 'cannotDeploy';
      return { actions: [{ _standaloneEffect: { trigger: 'passive',
        condition: { op: 'hasKeyword', keyword: KW_CN[m[1]], target: 'self' },
        aura: { target: { sel: 'all', side: 'friendly', filter: { excludeSelf: true } }, mod: mod } } }] };
    }

    /* 43) 攻击力等于场上友方 X 最高攻击力 —— OPS.setStats + {maxStat} */
    m = s.match(/^攻击力等于场上友方\s*([\u4e00-\u9fa5A-Za-z0-9\-]{1,8}?)\s*(?:的)?最高攻击力/);
    if (m) {
      const f = P.traitFilterOf(m[1]) || P.typeFilterFromText(m[1]) || { nameIncludes: m[1] };
      return { actions: [{ op: 'setStats', attack: { maxStat: { stat: 'attack', spec: { side: 'friendly', filter: f } } }, target: { sel: 'self' } }] };
    }

    /* 44) 对敌方总部造成等同于友方山地数量之和 +N 的伤害 —— OPS.damageHQ + {count, plus} */
    m = s.match(/^对敌方总部造成等同于友方山地数量之和\s*\+?\s*([一二两三四五六七八九十\d]+)?\s*的伤害/);
    if (m) return { actions: [{ op: 'damageHQ', side: 'enemy',
      amount: { count: { sel: 'all', side: 'friendly', filter: { keyword: 'alpine' } }, plus: num(m[1] || '0') } }] };

    /* 45) 若受到的伤害不大于 N，改为消灭该单位，并完全修复 —— OPS.conditional（kuom-7 特） */
    m = s.match(/^若受到的伤害不大于\s*([一二两三四五六七八九十\d]+)\s*[，,]?\s*改为消灭该单位\s*[，,]?\s*并?完全修复/);
    if (m) return { actions: [{ op: 'conditional',
      condition: { op: 'compare', left: { stat: 'lastDamageTaken' }, cmp: '<=', value: num(m[1]) },
      then: [{ op: 'destroy', target: { sel: 'ref', ref: 'defender' } }, { op: 'heal', amount: 999, target: { sel: 'self' } }] }] };

    /* 46) 每使用过一张“X”额外加入一张 —— OPS.addCardToHand + {playedCount} */
    m = s.match(/^(?:此后)?每使用过一张\s*[“"「]?([^”"「」，,。]{1,12}?)[”"」]?\s*(?:额外)?加入一张/);
    if (m) return { actions: [{ op: 'addCardToHand', name: m[1].trim(), count: { playedCount: { name: m[1].trim() } }, side: 'self' }] };

    /* 35) 从敌方手牌中选择一张，将其弃置并复制一张加入友方手牌 —— OPS.chooseHandCard+copyHandCard+discardChosenHand
     *   样例：九族三队侦查 */
    if (/^从敌方手牌中选择一张\s*[，,]?\s*将其弃置并\s*[，,]?\s*复制一张加入(?:友方)?手牌/.test(s))
      return { actions: [
        { op: 'chooseHandCard', side: 'enemy', count: 1, prompt: '从敌方手牌中选择一张' },
        { op: 'copyHandCard', from: 'chosen', side: 'self', sel: 'chosen' },
        { op: 'discardChosenHand', side: 'enemy' }] };

    /* 25) 使一个（友方）单位获得“<效果文本>” —— OPS.grantEffect
     *   把内文当**独立触发效果**挂到目标单位上；内文里的触发前缀在这里手工映射
     *   （primitives 不能回调 compiler）。
     *   样例：麦德奇斯号「使一个友方单位获得“本单位攻击时，是所有前线单位撤退”」、
     *         斯意奎奇号「…“友方回合结束时将其返回手牌，并复制一份进入支援线”」、
     *         物资补给「…“友方回合开始时，具有 -1 行动花费”」 */
    m = s.match(/^使一个(?:友方|我方)\s*([\u4e00-\u9fa5]{0,6}?)\s*单位获得\s*[“"「]([\s\S]+?)[”"」]?\s*$/);
    if (m) {
      const TRIG_MAP = [
        [/^本单位攻击时\s*[，,]?\s*/, 'attack'],
        [/^(?:本单位被消灭时|亡计)\s*[:：]?\s*[，,]?\s*/, 'death'],
        [/^友方回合结束时\s*[，,]?\s*/, 'turnEnd'],
        [/^友方回合开始时\s*[，,]?\s*/, 'turnStart'],
      ];
      let trig = null, body = String(m[2]).trim();
      for (const t of TRIG_MAP) { const mm = body.match(t[0]); if (mm) { trig = t[1]; body = body.slice(mm[0].length).trim(); break; } }
      if (trig && body) {
        const sub = P.parse(body, { _noSplit: true });
        if (sub && sub.actions && sub.actions.length) {
          const tf = m[1] ? (P.typeFilterFromText(m[1]) || P.traitFilterOf(m[1])) : null;
          const tgt = paiqDeclare(declare, 'friendly', tf, '友方单位');
          return { actions: [{ op: 'grantEffect', target: tgt, effectTrigger: trig, effects: sub.actions, label: String(m[2]).trim() }], targets: declare.slice() };
        }
      }
    }

    /* 26) 具有 -N 行动花费 —— OPS.opCostMod（作用于自己）
     *   样例：物资补给内文「友方回合开始时，具有 -1 行动花费」、动员「…与 -1 行动花费」 */
    m = s.match(/^具有\s*-\s*([一二两三四五六七八九十\d]+)\s*行动花费/);
    if (m) return { actions: [{ op: 'opCostMod', amount: -num(m[1]), target: { sel: 'self' } }] };
    m = s.match(/^(?:与|和|并)\s*-\s*([一二两三四五六七八九十\d]+)\s*行动花费\s*$/);
    if (m) return { actions: [{ op: 'opCostMod', amount: -num(m[1]), target: { sel: 'self' } }] };

    /* 27) （将）其复制（一份）进入支援线/同一阵线 —— OPS.summon（self 复制）
     *   样例：交易田143支队「亡计：将其复制进入支援线」 */
    if (/^(?:将)?其复制(?:一份)?(?:进入|置入|加入)(?:支援线|支援阵线|同一阵线|前线)/.test(s))
      return { actions: [{ op: 'summon', count: 1, self: true, side: 'self' }] };

    /* 28) 若有友方山地单位，使所有敌方单位 -N-M —— OPS.conditional + OPS.debuff
     *   样例：“幽灵”军「若有友方山地单位，使所有敌方单位 -1-2」 */
    m = s.match(/^若有友方山地单位\s*[，,]?\s*使所有敌方单位\s*-\s*([一二两三四五六七八九十\d]+)\s*-\s*([一二两三四五六七八九十\d]+)/);
    if (m) return { actions: [{ op: 'conditional',
      condition: { op: 'unitCount', cmp: '>=', value: 1, spec: { side: 'friendly', filter: { keyword: 'alpine' } } },
      then: [{ op: 'debuff', attack: num(m[1]), defense: num(m[2]), target: ENEMY }] }] };

    /* 29) （若可能）使随机一个友方（支援阵线的）X 单位移至前线 —— OPS.move
     *   样例：放良先锋458组「若可能使随机一个友方支援阵线的山地单位移至前线」 */
    m = s.match(/^(?:若可能)?使随机一个友方(?:支援阵线的?)?\s*([\u4e00-\u9fa5]{0,6}?)\s*单位移至(?:前线|下一阵线)/);
    if (m) {
      const t = { sel: 'random', side: 'friendly', zone: 'support' };
      const f = m[1] ? (P.traitFilterOf(m[1]) || P.typeFilterFromText(m[1])) : null;
      if (f) t.filter = f;
      return { actions: [{ op: 'move', to: 'frontline', target: t }] };
    }

    /* 30) （若可能）将一张花费不大于 N 的 <系列> 单位加入支援线 —— OPS.summon + filter{set,maxCost}
     *   样例：kuom-89 特「若可能将一张花费不大于2 的BSUC单位加入支援线」 */
    m = s.match(/^(?:若可能)?将一张花费不大于\s*([一二两三四五六七八九十\d]+)\s*的\s*([A-Za-z\u4e00-\u9fa5]{2,8})\s*单位加入(?:支援线|支援阵线|同一阵线)/);
    if (m) {
      const setw = SET_WORD_FILTER[m[2]] || m[2];
      const f = { maxCost: num(m[1]) };
      if (Array.isArray(setw)) f.setIn = setw; else f.set = setw;
      return { actions: [{ op: 'summon', count: 1, side: 'self', filter: f }] };
    }

    return null;
  }

  // 明确区分「完全不认识」和「认出了复合结构、但有片段没解析完」。
  // 后者绝不能继续落到宽松的旧规则里碰运气，否则一个子句会被前缀正则吃掉，
  // 其余语义静默消失。compiler 会把这个结果转成未实现标记和人工确认警告。
  P.unparsed = function (text, reason) {
    return { actions: [], targets: [], uncertain: true,
      unparsed: { text: String(text == null ? '' : text), reason: String(reason || '存在未解析片段') } };
  };

  /* 把 P.parse 埋下的哨兵数字回填成真正的变量引用（见「等同于v1的费用」那段）。
   * ⚠ 必须由调用方在拿到结果后调一次（compiler.js 的 parsePrimitives）——
   *   P.parse 内部有十几处 return，逐个埋点必漏。 */
  P.takeVarRefs = function (res) {
    const refs = P.__varRefs || [];
    P.__varRefs = [];
    if (!refs.length || !res) return res;
    const find = n => { for (let i = 0; i < refs.length; i++) if (refs[i].sentinel === n) return refs[i].ref; return null; };
    const fill = function (node) {
      if (Array.isArray(node)) {
        for (let i = 0; i < node.length; i++) {
          const r = (typeof node[i] === 'number') ? find(node[i]) : null;
          if (r) node[i] = r; else fill(node[i]);
        }
        return;
      }
      if (node && typeof node === 'object') {
        Object.keys(node).forEach(function (k) {
          const r = (typeof node[k] === 'number') ? find(node[k]) : null;
          if (r) node[k] = r; else fill(node[k]);
        });
      }
    };
    fill(res.actions || []);
    fill(res.targets || []);
    return res;
  };

  P.parse = function (sentenceRaw, opts) {
    let s = String(sentenceRaw || '').trim();
    if (!s) return null;
    opts = opts || {};
    const declare = [];
    // ★ 错别字容错：卡面/OCR 常把「使…」写成「是…」（「是所有友方单位获得闪击…」）。
    //   只在后面紧跟"群体/阵营/区域"这类明确词头时才纠正，别把真正的"是…"句子改坏。
    if (/^是(?=(?:所有|全部|全体|每个|每|友方|我方|敌方|对方|双方|场上|卡组|手牌|你的|你|本单位|这张))/.test(s)) s = '使' + s.slice(1);
    // ★ 统一剥掉**句尾标点**：所有句式规则都按"无尾标点"写，
    //   而卡面句子常带「。」「；」（「抽一张空军。」曾被当成"抽一张牌"= draw 1，筛选条件丢了）。
    s = s.replace(/[。；;，,、\s]+$/, '').trim();
    if (!s) return null;
    // ★★ 「若可能 / 如果可能 / 尽可能」这类**纯修饰词**单独成段（splitTop 会在它后面的逗号处断开）：
    //   它本身不产生动作，但**也不能算"这段解析失败"** ——
    //   否则复合句的 allOk 会因此变 false → 整句退回"碰运气"，**后面的动作整段丢掉**。
    //   实测：「将两张X加入支援阵线；**若可能**，将其中一张加入前线，并使其获得+1+1」
    //   因为"若可能"独立成段而丢掉最后的 buff。
    //   （"若可能"的语义由 `OPS.move` 自带 —— 前线被占/已满就静默跳过。）
    if (/^(?:若可能|如果可能|尽可能|有空位的话?|尚有空间)$/.test(s)) return { actions: [] };

    /* ★★ 卡面变量（Alan 09-28「变量系统也弄好」）—— 与 Inspector 同一套 v1/v2 命名。
     *   ①「等同于v1的费用 / 攻击力 / 防御力 / 行动花费」→ 数值 {cardVar:'v1', stat:'cost'}
     *      ⚠ 做法：先把这段换成**哨兵数字**，让原语组合器照常解析出动作（amount 先当普通数字），
     *        再由 P.takeVarRefs() 把哨兵回填成变量引用 —— 比给每个动作各写一条规则省得多，
     *        而且对**任意**接数值的字段（伤害 / 数量 / 治疗 / 费用…）都成立。
     *   ②「…并记为v1」→ 在动作后追加 asVar{name:'v1', from:'card'}（源 = 最近加入手牌的那张）
     *   ⚠ 只认**显式变量名**（v1），不做中文指代：「造成等同于**其**费用的伤害」里的"其"指谁
     *     是有歧义的（事件单位？上一个目标？刚抽的那张牌？）—— 宁可让人写清楚，也不猜错。 */
    {
      /* ⚠ 卡面是「造成等同于v1费用**的**伤害」—— "的" 在属性词**后面**，不是 v1 后面。
       *   两个"的"都要吃掉，并补一个「点」：替换后必须变成原语组合器认得的
       *   「造成 990001 点伤害」（实测「造成 990001 伤害」没有量词"点" → 解析失败）。 */
      const vm = /等同于\s*(v\d+|[a-zA-Z_]\w*)\s*(?:的)?\s*(费用|攻击力|防御力|行动花费|花费)\s*(?:的)?/.exec(s);
      if (vm) {
        const sm = { '费用': 'cost', '攻击力': 'attack', '防御力': 'defense', '行动花费': 'opCost', '花费': 'cost' };
        P.__varSeq = (P.__varSeq || 0) + 1;
        const sent = 990000 + (P.__varSeq % 9000);
        (P.__varRefs = P.__varRefs || []).push({ sentinel: sent, ref: { cardVar: vm[1], stat: sm[vm[2]] } });
        /* 量词跟着后面的名词走：「N **点**伤害」/「N **个**指挥点」—— 补错量词原语组合器就不认 */
        const tail = String(s.slice((vm.index || 0) + vm[0].length)).replace(/^\s*的\s*/, '');
        const q = /^\s*伤害/.test(tail) ? ' 点' : (/^\s*(?:指挥点|个|张|个指挥点)/.test(tail) ? ' 个' : ' ');
        s = s.replace(vm[0], sent + q);
      }
    }
    if (!opts._noVar) {
      /* 复合句会按「并」把「抽一张牌**并**记为v1」拆成两段 —— 后半段就是光秃秃的"记为v1"，
       * 它自己就是**一个**动作（记住最近加入手牌的那张），顺序上排在 draw 之后，正合先声明后使用。 */
      const am0 = /^记为\s*(v\d+)$/.exec(s);
      if (am0) return { actions: [{ op: 'asVar', name: am0[1], from: 'card' }] };
      const am = /^([\s\S]+?)(?:，|,)?\s*(?:并|然后)?\s*记为\s*(v\d+)$/.exec(s);
      if (am) {
        const inner = P.parse(am[1], Object.assign({}, opts, { _noVar: true }));
        if (inner && !inner.unparsed && inner.actions && inner.actions.length) {
          const out = { actions: inner.actions.concat([{ op: 'asVar', name: am[2], from: 'card' }]) };
          if (inner.targets && inner.targets.length) out.targets = inner.targets;
          return out;
        }
        return P.unparsed(s, '「记为vN」前面的动作没能解析');
      }
    }

    /* ★★「三次过后，X」/「每三次，X」/「第三次，X」= **动作门控前缀**（Alan 09-28
     *   「友方单位部署时，将其返回手牌，三次过后，升为老兵」）
     *   它本身不产生动作，必须和**后面的动作**绑成一个 nthTime{count, then}。
     *   ⚠ 必须放在**复合句拆分之前**（下面 0) 那段）：复合句按逗号拆成子句后，
     *     "三次过后" 会独立成段，它没有动作词 → 该段解析失败 → allOk=false
     *     → **整句**被判"未能完整解析"（改之前实测就是这个结果）。
     *   ⚠ 后半段递归回 P.parse，于是「N次过后」对**任意动作**都成立（消灭/抽牌/升为老兵…），
     *     不需要给每个动作逐条写规则。 */
    if (!opts._noNth) {
      const CNn = { '一': 1, '二': 2, '两': 2, '三': 3, '四': 4, '五': 5, '六': 6, '七': 7, '八': 8, '九': 9, '十': 10 };
      const toN = t => (/^\d+$/.test(t) ? parseInt(t, 10) : (CNn[t] || null));
      const nm = '(\\d+|[一二两三四五六七八九十]+)';
      const mn = new RegExp('^(?:第\\s*' + nm + '\\s*次(?:触发)?|每\\s*' + nm + '\\s*次|' + nm + '\\s*次(?:过后|之后|后))\\s*[，,、]\\s*').exec(s);
      if (mn) {
        const n = toN(mn[1] || mn[2] || mn[3]);
        const inner = P.parse(s.slice(mn[0].length), Object.assign({}, opts, { _noNth: true }));
        if (n && inner && !inner.unparsed && inner.actions && inner.actions.length) {
          const act = { op: 'nthTime', count: n, then: inner.actions };
          if (mn[2]) act.every = true;          // 「每三次」→ 达成后清零，循环触发
          const out = { actions: [act] };
          if (inner.targets && inner.targets.length) out.targets = inner.targets;
          if (inner.cardFields) out.cardFields = inner.cardFields;
          return out;
        }
        return P.unparsed(s, '「N次过后」后面的动作没能解析');
      }
    }

    /* ★★「每消灭一个(单位)?，X」= **销毁的伴随动作**（Alan 09-28
     *   「消灭所有受伤单位，每消灭一个，将一张随机进攻洗入卡组」）。
     *   产出带 _onKill 标记的哑动作，由**复合句合并循环**塞进前一个 destroy 的 forEach ——
     *   引擎 OPS.destroy 现在支持 forEach（每销毁一个单位结算一次）。
     *   ⚠ 若前面没有 destroy（比如单独写"每消灭一个，抽一张牌"）→ 合并失败 → allOk=false
     *     → 整句未实现，**绝不静默丢掉**"每消灭一个"的语义。 */
    if (!opts._noOnKill) {
      const km = /^每消灭\s*(?:一|\d+|[一二两三四五六七八九十]+)\s*个?(?:单位|敌方单位|友方单位|受伤单位)?\s*[，,、]\s*([\s\S]+)$/.exec(s);
      if (km) {
        /* ⚠ 只在**复合句拆分**的子句调用（_noSplit）里才返回 _onKill 哑动作 ——
         *   整句直接以"每消灭一个"开头时，它前面根本没有 destroy 可挂，
         *   返回哑动作会静默泄漏出去（实测：「每消灭一个，抽一张牌。」裸返回 {_onKill} 还零警告）。
         *   那种情况必须 unparsed。 */
        if (!opts._noSplit) return P.unparsed(s, '「每消灭一个」前面没有它跟随的消灭动作');
        const inner = P.parse(km[1], Object.assign({}, opts, { _noOnKill: true }));
        if (inner && !inner.unparsed && inner.actions && inner.actions.length)
          return { actions: [{ _onKill: inner.actions }] };
        return P.unparsed(s, '「每消灭一个」后面的动作没能解析');
      }
    }

    /* ★★「抽(数张/N张)牌直到手牌数为N」→ drawUntil（引擎现成，handSize 默认就是 7 ——
     *   以前只差句式：编译器根本不认"直到手牌数为N"，整句掉进未实现。 */
    {
      const CNd = { '一': 1, '二': 2, '两': 2, '三': 3, '四': 4, '五': 5, '六': 6, '七': 7, '八': 8, '九': 9, '十': 10 };
      const du = /^抽(?:数张|\d+张|[一二两三四五六七八九十]+张)?\s*牌?\s*直到(?:自己|友方|己方)?\s*手牌数为?\s*(?:等于|达到|是)?\s*(\d+|[一二两三四五六七八九十]+)$/.exec(s);
      if (du) {
        const n = /^\d+$/.test(du[1]) ? parseInt(du[1], 10) : CNd[du[1]];
        if (n) return { actions: [{ op: 'drawUntil', handSize: n }] };
      }
    }

    // 「将数张X加入支援阵线，直到阵线已满」是一个带动态数量限制的卡牌操作。
    //   splitTop 默认会在逗号处拆开，原先两半各自解析：前半变成一次 summon，
    //   后半又被 until-line-full 解析成 repeat + 目标弹窗。这里先合并语义：
    //   只有前半确实解析成召唤动作时才包装；不完整或非召唤句明确失败，不能留下半截动作。
    if (!opts._noSplit) {
      const fullLine = s.match(/^([\s\S]+?)[，,]\s*(?:直到|直至)\s*(?:支援阵线|支援线|阵线)\s*已满$/);
      if (fullLine) {
        const base = P.parse(fullLine[1].trim(), Object.assign({}, opts, { _noSplit: true }));
        if (base && !base.unparsed && base.actions && base.actions.length &&
            base.actions.every(function (a) { return a && a.op === 'summon'; })) {
          return {
            actions: [{ op: 'repeat', times: { supportFree: 'self' }, actions: base.actions }],
            targets: base.targets,
            cardFields: base.cardFields,
          };
        }
        return P.unparsed(s, '「直到阵线已满」前必须是完整的召唤动作');
      }
    }

    // 0.04) 「具有<词条>时，获得+N攻击力/防御力」→ **条件式常驻光环**（作用于自己）。
    //   必须整句处理：拆句会把"具有X时"和"获得+N"分开，条件被整段丢掉（13(a)坦克曾变成
    //   "选一个友方单位 +2攻击力"）。产出 trigger:'passive' 的独立效果块，
    //   由 recomputeAuras 每次重算时判条件 —— 词条消失时（冲击被消耗）加成立刻失效。
    if (!opts._noSplit) {
      const cm2 = s.match(/^具有\s*(闪击|固守|守护|烟幕|伏击|奋战|狂怒|游击|冲击|流亡|老兵)\s*时[，,]?\s*获得\s*\+([一二两三四五六七八九十\d]+)\s*(攻击力|防御力)\s*$/);
      if (cm2 && KW_CN[cm2[1]]) {
        const aur = { target: { sel: 'self' } };
        if (cm2[3] === '攻击力') aur.attack = num(cm2[2]); else aur.defense = num(cm2[2]);
        return { actions: [{ _standaloneEffect: { trigger: 'passive', condition: { op: 'hasKeyword', keyword: KW_CN[cm2[1]], target: 'self' }, aura: aur } }] };
      }
    }

    // 0.045) 「若场上存在友方<兵种>单位，（则）获得<词条>」→ **条件式常驻词条**（作用于自己）。
    //   拆句会把"若场上存在…"和"获得奋战"分开：条件整段丢失，且"获得奋战"会退化成
    //   "选一个友方轰炸机给它奋战"（K型战斗机曾因此把奋战给了别人）。
    if (!opts._noSplit) {
      const km = s.match(/^若场上存在友方\s*([\u4e00-\u9fa5]{1,6}?)\s*单位[，,]?\s*则?\s*获得\s*([\u4e00-\u9fa5]{2,4})\s*$/);
      if (km && KW_CN[km[2]]) {
        const tf = P.typeFilterFromText(km[1]);
        const spec = { side: 'friendly' };
        if (tf && tf.unitType) spec.filter = { unitType: tf.unitType };
        return { actions: [{ _standaloneEffect: { trigger: 'passive',
          condition: { op: 'unitCount', cmp: '>=', value: 1, spec: spec },
          aura: { target: { sel: 'self' }, keyword: KW_CN[km[2]] } } }] };
      }
    }

    // 0.048) 「如果/若 <buildCondition 认得的条件>，（则）（本单位）获得/具有 Y」→ **条件式常驻**（作用于自己）。
    //   Y = 词条（守护/闪击…）或身材（+N+N / +N攻击力 / +N防御力）。
    //   0.045 的泛化：钳级巡地舰（场上存在其它X→守护）、USG第5师（存在空投师→+2+2）、
    //   联合第三步兵师（有其他同名单位→闪击）、古斯师（是本回合第一个单位→冲击）。
    //   必须整句处理：拆句会把条件与奖励分开 → 条件整段丢失、奖励退化成"选一个友方单位给Y"。
    //   产出 passive aura：条件消失（同名单位都死了）时加成自动失效，这是"获得Y"一次性给不到的。
    if (!opts._noSplit) {
      const rm048 = s.match(/^(?:如果|若|假如)\s*([^，,。]{2,24}?)\s*[，,]?\s*(?:则)?\s*(?:本单位?)?\s*(?:具有|获得)\s*([\s\S]+)$/);
      if (rm048) {
        const cond048 = buildCondition(rm048[1]);
        const body048 = String(rm048[2] || '').trim();
        // ⚠ 自身**数值比较**条件（防御力/攻击力/行动花费 ≥N）不接：那类卡面（「若防御力不小于5，
        //   获得+1攻击力」）语义是**部署时判定一次**，旧路径产出 deploy+conditional 且有 fxcheck
        //   行为断言钉着。R1 只管**场面条件**（存在X/同名/第一个单位/控制X…）。
        const selfStat048 = cond048 && (cond048.op === 'opCost' || (cond048.op === 'compare' && cond048.left &&
          (cond048.left.stat === 'defense' || cond048.left.stat === 'attack' || cond048.left.stat === 'opCost')));
        if (cond048 && !selfStat048 && body048) {
          const aura048 = { target: { sel: 'self' } };
          let ok048 = false;
          const kw048 = body048.match(/^[“"「]?([\u4e00-\u9fa5]{2,4})[”"」]?$/);
          if (kw048 && KW_CN[kw048[1]]) { aura048.keyword = KW_CN[kw048[1]]; ok048 = true; }
          if (!ok048) {
            const st048 = body048.match(/^\+\s*([一二两三四五六七八九十\d]+)(?:\s*\+\s*([一二两三四五六七八九十\d]+))?(?:\s*(攻击力|防御力))?$/);
            if (st048) {
              if (st048[2] != null) { aura048.attack = num(st048[1]); aura048.defense = num(st048[2]); ok048 = true; }
              else if (st048[3] === '攻击力') { aura048.attack = num(st048[1]); ok048 = true; }
              else if (st048[3] === '防御力') { aura048.defense = num(st048[1]); ok048 = true; }
            }
          }
          if (ok048) return { actions: [{ _standaloneEffect: { trigger: 'passive', condition: cond048, aura: aura048 } }] };
        }
      }
    }

    // 0.049) 「消灭<范围>所有单位；每消灭一个，额外获得一个指挥点槽」→ **forEach 计数**。
    //   「每消灭一个」是**每单位一次**的计数语义，不是"再消灭一个单位"——
    //   落到通用解析会变成 destroy+选靶（凭空多消灭一个 + 弹选靶窗，制作者 2026-09-22 报
    //   「自动解析坏了」的就是它）。与手写层「最后一搏」同款：forEach(枚举) [destroy, 奖励]。
    //   ⚠ 不能用 destroyAll + gainKreditSlot{count} —— destroy 执行完 count 就成 0 了。
    if (!opts._noSplit) {
      const km049 = s.match(/^消灭\s*(敌方|友方|我方|双方)?\s*所有\s*单位\s*[；;，,]*\s*每消灭一个\s*[，,]?\s*(?:额外)?获得\s*(?:一个|1\s*个)?\s*指挥点槽\s*$/);
      if (km049) {
        const sd049 = /敌方/.test(km049[1] || '') ? 'enemy' : (/友方|我方/.test(km049[1] || '') ? 'friendly' : 'both');
        return { actions: [{ op: 'forEach', target: { sel: 'all', side: sd049 }, as: 't2',
          actions: [{ op: 'destroy', target: 't2' }, { op: 'gainKreditSlot', amount: 1, side: 'self' }] }] };
      }
    }

    // 0.049b) 「每有一个<阵营><兵种>单位，对一个单位造成N点伤害（，后续动作）」→ **动态数值伤害**。
    //   amount = {count:{side,filter}, times:N}（引擎 num() 支持 N×数量）。
    //   「每有一个」段自己解析不了 → 拆句必败 → 整句退回碰运气时数量被当目标筛选剥掉
    //   （重锤战术：伤害恒为 2，场景里 2 个太空单位应是 4）。
    if (!opts._noSplit) {
      const dm049 = s.match(/^每有一个\s*(敌方|对方|友方|我方)?\s*([\u4e00-\u9fa5]{1,4})\s*单位\s*[，,]?\s*对(?:一个|1\s*个)?\s*(?:单位|目标)\s*造成\s*([一二两三四五六七八九十\d]+)\s*点伤害\s*(?:[，,；;]\s*([\s\S]+))?$/);
      if (dm049) {
        const sdD = /敌|对方/.test(dm049[1] || '') ? 'enemy' : 'friendly';
        const tfD = P.typeFilterFromText(dm049[2]) || P.traitFilterOf(dm049[2]);
        if (tfD && tfD.unitType) {
          const actsD = [{ op: 'damage', amount: { count: { side: sdD, filter: { unitType: tfD.unitType } }, times: num(dm049[3]) }, target: 't1' }];
          const declD = [{ id: 't1', side: 'any', kind: 'unit', prompt: '选择一个单位' }];
          if (dm049[4]) {
            const rest = P.parse(dm049[4], { _noSplit: true, _slot: opts._slot, eventRef: opts.eventRef });
            if (rest && rest.actions && rest.actions.length) {
              actsD.push(...rest.actions);
              (rest.targets || []).forEach(function (t) { declD.push(t); });
            } else return null;   // 剩余段认不出 → 整句放弃（不许只吞一半）
          }
          return { actions: actsD, targets: declD };
        }
      }
    }

    // 0.046) 「若其不小于N，（额外）获得一个指挥点槽」——"其"指代**前一句的数值**
    //   （本项目里就是「上一回合被消灭的单位数量」）。
    //   ⚠ 必须在这里**整句**处理：交给下面通用的「若…」条件分支时，
    //     条件会因"其"指代不明而**被整段丢掉**（实测：产出 gainKreditSlot 但 condition 没了）。
    if (!opts._noSplit) {
      const km046 = s.match(/^若其\s*(不小于|大于|至少|不少于|超过)\s*([一二两三四五六七八九十\d]+)\s*[，,]?\s*(?:则)?\s*(?:额外)?获得\s*(?:一个|1\s*个)?\s*指挥[点额]槽/);
      if (km046) {
        const CMP046 = { '不小于': '>=', '大于': '>', '至少': '>=', '不少于': '>=', '超过': '>' };
        return { actions: [{ op: 'gainKreditSlot', amount: 1, side: 'self',
          condition: { op: 'lastTurnKilledCompare', cmp: CMP046[km046[1]] || '>=', value: num(km046[2]) } }] };
      }
    }

    // 0.047) 「从"A"、"B"和"C"中开发一张」——**指名几张卡的三选一**（2026-09-22 制作者）
    //   必须在这里整句处理：这句的"开发"在**句末**，走不到「开发一张X」（那条要求"开发"后带描述）。
    //   做法：抠出引号里的卡名 → **编译期转卡 id**（引擎的 develop 只认 id）→ filter.cardIds
    //   （OPS.develop 早已支持"指名道姓的卡池"，卡池正好 = 这几张 → 抽 3 选 1 就是"从这几张里选一张"）。
    //   ⚠ 只有**引号括起来的**才算名单 —— 不引号会跟「开发一张协约国空军」这类描述混淆。
    //   ⚠ 卡池里暂时没有这些卡（制作者正在编）时 filter 为空，名单留在 _names 便于排查。
    if (!opts._noSplit) {
      const q0 = s.match(/^从\s*((?:[“"「][^”"」]+[”"」]\s*[、,，和与及]?\s*)+)中?\s*(?:开发|发掘|发现)/);
      if (q0) {
        const names = (q0[1].match(/[“"「]([^”"」]+)[”"」]/g) || [])
          .map(function (x) { return x.replace(/[“”"「」]/g, '').trim(); }).filter(Boolean);
        if (names.length >= 2) {
          const pool = root.KG && root.KG.pool;
          const ids = [];
          if (pool) {
            const keys = Array.isArray(pool) ? null : Object.keys(pool);
            names.forEach(function (n) {
              let hit = null;
              if (keys) {
                hit = keys.find(function (k) { return pool[k] && String(pool[k].name) === n; })
                  || keys.find(function (k) { return pool[k] && String(pool[k].name || '').indexOf(n) >= 0; });
              }
              if (hit) ids.push(hit);
            });
          }
          const out = { op: 'develop', filter: ids.length ? { cardIds: ids } : {}, from: 'pool', _names: names };
          return { actions: [out] };
        }
      }
    }

    // 0.05) 「将N张X加入手牌，使其获得+Y+Z和词条」——"使其"指的是**刚加入手牌的那些牌**。
    //   必须整句一次处理：拆句后"使其获得…"会丢掉指代对象，退化成"选一个友方单位 +Y+Z"。
    //   落点：addCardToHand + buffCardsInPiles（手牌堆附魔，引擎 OPS.buffCardsInPiles）。
    if (!opts._noSplit) {
      const pm = s.match(/^([\s\S]*?加入\s*(?:双方)?\s*手牌(?:中|里)?)[，,]?\s*[使令]\s*(?:其|这批牌|这些牌)?\s*获得\s*\+([一二两三四五六七八九十\d]+)\s*\+([一二两三四五六七八九十\d]+)(?:\s*和\s*([\u4e00-\u9fa5]{2,4}))?\s*$/);
      if (pm) {
        const head = P.parse(pm[1], { _noSplit: true });
        const add = head && head.actions ? head.actions.filter(function (a) { return a.op === 'addCardToHand'; }) : [];
        if (add.length) {
          const kwName = pm[4] ? (KW_CN[pm[4]] || null) : null;
          const acts = head.actions.slice();
          add.forEach(function (a) {
            const f = {};
            if (a.filter) Object.assign(f, a.filter);
            if (a.cardId) f.cardId = a.cardId;
            if (a.name) f.name = a.name;
            acts.push({ op: 'buffCardsInPiles', side: 'self', piles: 'hand',
              filter: Object.keys(f).length ? f : undefined,
              attack: num(pm[2]), defense: num(pm[3]), keyword: kwName || undefined });
          });
          return { actions: acts, targets: head.targets };
        }
      }
    }

    // 「抽N张“X”，将其花费减为M」= 从自己的卡组抽指定卡，并只修改本次抽到的手牌实例。
    //   必须在 splitTop 前整句解析：分开后「将其花费减为M」没有目标，无法知道「其」指刚抽到的牌；
    //   ACTIONS 中虽已有「抽N张X」，但没有把这个后置费用修正绑到抽牌结果上。
    //   用户卡面常用 ASCII 句点结尾，故这里显式兼容中英文句号。
    if (!opts._noSplit) {
      const namedDrawCost = s.match(/^抽\s*([一二两三四五六七八九十\d]+)\s*张\s*[“"「]([^”"」]{1,20})[”"」]\s*[，,]\s*(?:并\s*)?(?:将|使)其花费\s*(?:减为|变为|设为|降为|改为)\s*([一二两三四五六七八九十\d]+)(?:。|\.)?$/);
      if (namedDrawCost) {
        return { actions: [{
          op: 'addCardToHand',
          name: namedDrawCost[2].trim(),
          _quotedName: true,
          count: num(namedDrawCost[1]),
          side: 'self',
          from: 'deck',
          costTo: num(namedDrawCost[3]),
        }] };
      }
    }

    // 0.02) 「获得 A，B 和 C」里的**逗号是并列片段分隔**，不是句子分隔。
    //   不归一的话，拆句会把「…获得减1花费」与「减1行动花费和+2+2」拆成两段 →
    //   后半段丢掉区域/阵营信息（跨区域修正就会只生效一半）。归一成「、」后 splitTop 不拆。
    s = s.replace(/((?:获得|具有|得到)[^，,。；;]*)[，,]\s*(?=[+＋]|[-−]\s*\d|减\s*[一二两三四五六七八九十\d]|行动花费|攻击力|防御力|花费)/g, '$1、');

    // 0.025) 「使友方场上、手中、卡组里的所有单位获得 <跨区域群体修正>」——整句一次处理。
    //   三个区域三种落点：场上=单位身材/行动花费，手牌=实例花费/行动花费/身材，卡组=cardMods。
    if (!opts._noCrossPiles) {
      const cp = s.match(/^[使是]?\s*(友方|我方)?\s*场上\s*[、,，和及]?\s*(?:手中|手牌)\s*[、,，和及]?\s*(?:卡组|牌库)\s*(?:里的|中的|的)?\s*所有\s*单位\s*获得\s*([\s\S]+)$/);
      if (cp) {
        const tail = String(cp[2] || '');
        const items = tail.split(/[、,，和及]+/).map(function (x) { return x.trim(); }).filter(Boolean);
        let atk = 0, def = 0, cost = 0, opCost = 0, ok = 0;
        items.forEach(function (it) {
          let mm;
          if ((mm = it.match(/^\+([一二两三四五六七八九十\d]+)\s*\+\s*([一二两三四五六七八九十\d]+)$/))) { atk += num(mm[1]); def += num(mm[2]); ok++; }
          else if ((mm = it.match(/^(?:减|[-−])\s*([一二两三四五六七八九十\d]+)\s*点?行动花费$/))) { opCost -= num(mm[1]); ok++; }
          else if ((mm = it.match(/^(?:减|[-−])\s*([一二两三四五六七八九十\d]+)\s*点?花费(?!槽)$/))) { cost -= num(mm[1]); ok++; }
        });
        if (ok === items.length && ok > 0) {
          const acts = [];
          if (atk || def) acts.push({ op: 'buff', attack: atk || undefined, defense: def || undefined, target: { sel: 'all', side: 'friendly' } });
          if (opCost) acts.push({ op: 'opCostModAll', amount: opCost, target: { sel: 'all', side: 'friendly' } });
          acts.push({ op: 'buffCardsInPiles', side: 'self', piles: 'all',
            attack: atk || undefined, defense: def || undefined, cost: cost || undefined, opCost: opCost || undefined });
          return { actions: acts };
        }
        return P.unparsed(s, '跨区域修正中有未识别的属性'); // 不许退回前缀规则吞掉部分修正
      }
    }

    // 0) 复合句：按「并 / 然后 / ，/ ；/ 且」拆开逐段解析，全部认得才合起来
    //    条件句（如果/若…）不拆，交给下面的条件分支处理 ——
    //    **包括中段条件**（"额外获得三个指挥点槽，如果你控制巡航舰，随机消灭一个敌方单位"）：
    //    拆开会让条件子句成孤立片段而被丢弃，动作片段退化成无条件执行。
    // ⚠ 守卫里的 `^(如果|若|假如)` 必须排除「若可能」——它只是修饰语，不是条件句；
    //   不排除的话整段被当成"句首条件"→ **跳过拆句** → 只落一条规则（同 hasMidCondition 那个坑）。
    if (!opts._noSplit && !/^(?:如果|若|假如)(?!可能|有空位|有空间)/.test(s) && !hasMidCondition(s)) {
      const parts = splitTop(s);
      if (parts.length > 1) {
        // 0.0a) 「A，或者B」= **抉择**（帝国安保初级研发 / 抉择：…，或者…）：两段都是
        //   **卡牌获取类**动作（加入手牌/开发/召唤/洗入卡组/上战场）时合成 chooseOne ——
        //   "或者"的语义是**二选一**，顺序执行会把两个选项都执行（帝国安保三兄弟曾把
        //   两张研发一起塞进手牌）。选项超出安全白名单或有一项未识别时，整句失败关闭。
        if (parts.length === 2 && /^\s*或/.test(parts[1])) {
          const c1 = P.parse(parts[0], { _noSplit: true, _slot: opts._slot, eventRef: opts.eventRef });
          const c2 = P.parse(parts[1].replace(/^\s*或(?:者)?\s*/, '').trim(), { _noSplit: true, _slot: opts._slot, eventRef: opts.eventRef });
          const CHOICE_OPS = ['addCardToHand', 'develop', 'summon', 'shuffleIn', 'deckToField'];
          const okChoice = (r) => !!(r && r.actions && r.actions.length && r.actions.every(a => CHOICE_OPS.indexOf(a.op) >= 0));
          if (okChoice(c1) && okChoice(c2)) {
            return { actions: [{ op: 'chooseOne', options: [
              { label: parts[0].trim(), actions: c1.actions, targets: c1.targets },
              { label: parts[1].trim(), actions: c2.actions, targets: c2.targets },
            ] }] };
          }
          // 明确出现「或/或者」时不能把两边当顺序动作执行，也不能只编译其中一边。
          return P.unparsed(s, '抉择选项未能全部解析为可执行效果');
        }
        /* ★ 2026-09-26：「每有一张"X"」这类**纯数量前缀**不能单独当子句解析（它没有动作词）——
         *   拆句后它必失败，而 allOk 要求"全部认得"→ **整句**被判「未能完整解析」（卫星联盟通用驱逐舰）。
         *   先把这类前缀并到下一个子句前面，交给 LONGTAIL4 的「每有一张X，对战Y时+N攻击力」规则。 */
        const parts2 = [];
        for (let pi = 0; pi < parts.length; pi++) {
          const curP = parts[pi];
          if (/^(?:场上)?每有[^，,。；:]{1,16}$/.test(String(curP).trim()) && pi + 1 < parts.length) {
            parts2.push(String(curP).trim() + '，' + parts[pi + 1]);
            pi++;
          } else if (/^(?:第\s*(?:\d+|[一二两三四五六七八九十]+)\s*次(?:触发)?|每\s*(?:\d+|[一二两三四五六七八九十]+)\s*次|(?:\d+|[一二两三四五六七八九十]+)\s*次(?:过后|之后|后))$/.test(String(curP).trim()) && pi + 1 < parts.length) {
            /* ★ 纯「三次过后」段（没有动作词）→ 并到**下一个子句**前面。
             *   跟上面「每有一张X」是同一类问题：单独解析必失败，而 allOk 要求全部认得，
             *   于是「将其返回手牌，**三次过后**，升为老兵」整句被判未实现。 */
            parts2.push(String(curP).trim() + '，' + parts[pi + 1]);
            pi++;
          } else if (/^每消灭\s*(?:一|\d+|[一二两三四五六七八九十]+)\s*个?(?:单位|敌方单位|友方单位|受伤单位)?$/.test(String(curP).trim()) && pi + 1 < parts.length) {
            /* ★ 纯「每消灭一个」段同理（Alan 09-28）：单独成段没有动作 → 并到下一子句，
             *   合成「每消灭一个，将一张随机进攻洗入卡组」再走 _onKill 规则。 */
            parts2.push(String(curP).trim() + '，' + parts[pi + 1]);
            pi++;
          } else parts2.push(curP);
        }
        const merged = [], targets = [], fields = {};
        let allOk = true;
        // ★ 「控制被攻击单位，并使该单位获得+2+2」——控制之后的「该单位」指**被夺取的单位**
        //   （refUnit 'defender'），不是事件主角（攻击者）。不传 override 会 buff 到攻击者头上。
        let ctrlTaken = false;
        for (const part of parts2) {
          const popts = { _noSplit: true, _slot: opts._slot, eventRef: opts.eventRef };
          if (ctrlTaken && /该单位|该目标|其|它/.test(part)) popts.pronounOverride = 'defender';
          const r = P.parse(part, popts);
          if (!r || r.unparsed) { allOk = false; break; }
          if (r.actions && r.actions.some(x => x.op === 'takeControl')) ctrlTaken = true;
          (r.actions || []).forEach(a => {
            /* ★ _onKill（"每消灭一个，X"）→ 塞进**前一个 destroy** 的 forEach。
             *   前面不是 destroy → 这段语义没地方放 → allOk=false 整句失败（不许静默丢）。 */
            if (a && a._onKill) {
              const last = merged[merged.length - 1];
              if (last && last.op === 'destroy' && !last.forEach) { last.forEach = a._onKill; return; }
              allOk = false;
              return;
            }
            merged.push(a);
          });
          (r.targets || []).forEach(t => targets.push(t));
          if (r.cardFields) Object.assign(fields, r.cardFields);
        }
        if (allOk && (merged.length || Object.keys(fields).length)) return {
          actions: merged, targets: targets.length ? targets : undefined,
          cardFields: Object.keys(fields).length ? fields : undefined,
        };
        if (!allOk) return P.unparsed(s, '复合句中至少有一个子句未能完整解析');
        // 拆分后没有可执行动作，继续走后面的完整句式规则。
      }
    }

    // 0.5) 卡面属性（不产生动作，而是写进卡牌定义：例如"若在手牌中，每用一张指令获得-2花费"）
    //   ★ 卡面级字段也**允许前置条件**（「若你控制轰炸机，降低等同于场上友方轰炸机最高攻击力的费用」）：
    //     条件认得出来就摘下来，只交给**能表达 condition 的字段**（costModByStat —— 引擎算费用时求值）；
    //     其余字段暂时没有闸门 → 不消费这个条件（宁可显式报"未实现"，也不静默把条件丢掉）。
    let m0;
    let attrCond = null, sAttr = s;
    const attrM = s.match(/^(?:如果|若|假如)([^，,。]{1,40})[，,]\s*([\s\S]+)$/);
    if (attrM) { const c0 = buildCondition(attrM[1]); if (c0) { attrCond = c0; sAttr = attrM[2]; } }
    // 「在手牌中时，友方每使用一张<词条/类型>牌，获得-N花费」→ 卡面属性（不产生动作）
    if (!attrCond && (m0 = s.match(/在手牌中[^。；;]*每使用一张\s*([\u4e00-\u9fa5]{1,4})\s*牌[^。；;]*?(-?\d+)\s*花费/))) {
      const KW2 = { 情报: 'intel', 指令: 'order', 单位: 'unit' };
      const k = KW2[m0[1]] || m0[1];
      const amt = num(m0[2]);
      // ⚠ 引擎是 `c -= costModPerOrder * n`（**做减法**）：卡面「获得-2花费」要存 **2**。
      //   存 -2 会变成每打一张指令**加 2 费**（符号反了）。
      if (k === 'order') return { actions: [], cardFields: { costModPerOrder: -amt } };
      return { actions: [], cardFields: { costModPerKeyword: { kw: k, amount: -amt } } };
    }
    if (!attrCond && (m0 = s.match(/在手牌中[^。；;]*每使用一张指令[^。；;]*?(-?\d+)\s*花费/))) {
      return { actions: [], cardFields: { costModPerOrder: -num(m0[1]) } };
    }
    if (!attrCond && (m0 = s.match(/在手牌中[^。；;]*每使用一张情报[^。；;]*?(-?\d+)\s*花费/))) {
      return { actions: [], cardFields: { costModPerIntel: -num(m0[1]) } };
    }
    // 「N次过后，升为老兵」——本卡**累计触发 N 次**后升级。
    //   触发事件 = 卡面上一句的触发前缀（编译器用 opts.listenerTrigger 传进来），默认 unitDeployed。
    //   ★ 必须写进卡面级字段 upgradeOn：引擎的 upgradeCheck 累计 upgradeProgress、到 count 才 tryVeteran；
    //     effect 的 condition 是纯布尔判定，**没有"累计 N 次"的能力**。
    //   所以这里**不产出动作**（升级由引擎的升级检查完成），只产出 cardFields。
    if (!attrCond && (m0 = s.match(/^([一二两三四五六七八九十\d]+)\s*次(?:过后|之后|以后|后)\s*[，,]?\s*(?:升为老兵|升级为老兵|成为老兵|变成老兵)/))) {
      const up = { trigger: (opts && opts.listenerTrigger) || 'unitDeployed', side: (opts && opts.listenerOwner === 'foe') ? 'enemy' : 'self', count: num(m0[1]) || 1, excludeSelf: true };
      return { actions: [], cardFields: { upgradeOn: up } };
    }
    // 「降低等同于场上<阵营><兵种>最高攻击力的费用」——卡面级**动态减费**。
    //   engine.instCost 读 def.costModByStat，用 KG.boardStatValue 按"场上单位筛选 + 统计量"现算 X。
    // 「降低等同于（位于支援阵线的）友方 USG 太空战机和轰炸机 的最高攻击力（的）花费/费用」
    //   词串支持：系列词（USG…）+ 多个兵种（…和…）+ 阵线限定 + 拉丁词（Mk）。
    if ((m0 = sAttr.match(/^降低等同于\s*(?:场上)?\s*(位于|在)?\s*(支援阵线|支援线|前线)?\s*(?:的)?\s*(友方|我方|敌方|对方|双方)?\s*([\u4e00-\u9fa5A-Za-z]{1,24}?)\s*(?:的)?最高攻击力(?:的)?(?:费用|花费)/))) {
      // ⚠ 捕获组编号：1=(位于|在) 2=(阵线) 3=(阵营) 4=(词串)——别再按 5 取词（取到 undefined 会静默只剩 zone）
      const side = /敌|对方/.test(m0[3] || '') ? 'enemy' : /双方/.test(m0[3] || '') ? 'both' : 'friendly';
      const f = wordsFilter(m0[4]) || { nameIncludes: m0[4] };
      if (m0[2]) f.zone = /前线/.test(m0[2]) ? 'frontline' : 'support';
      const spec = { side: side, stat: 'maxAttack', filter: f };
      if (attrCond) spec.condition = attrCond;   // 前置条件：由 engine.instCost 在算费用时求值
      return { actions: [], cardFields: { costModByStat: spec } };
    }

    // 0.6) 条件句：「如果/若 X，则 Y」 → conditional
    //   条件子句本身也走**原语表**（COND_RULES）：每条规则 = [匹配正则, 构造条件原语的函数]。
    //   于是「新增一种条件写法」= 往表里加一行，而不是改 if 链。
    let cond = null;
    let body = s;
    let condPrefix = '';        // 条件子句**之前**的动作（"额外获得三个指挥点槽，如果你控制…"）
    // 「然后如果前线没有单位，将其加入前线」——剥掉"然后"前缀，剩下就是普通条件句
    if (/^然后/.test(s)) s = s.replace(/^然后\s*/, '');
    // ★ 中段条件：「<前置动作>，如果<条件>，<then>」
    //   在 0) 里已按 hasMidCondition 跳过拆句，这里把句子切成三段分别处理，
    //   最后拼成 [前置动作..., conditional{condition, then}]。
    if (!/^(?:如果|若|假如)/.test(s)) {
      const mc = s.match(/^([\s\S]*?)[，,；;：:]\s*(?:如果|若|假如|只要|当)\s*([\s\S]*)$/);
      if (mc) {
        // 条件子句到**下一个分隔符**为止（条件子句内部一般不含逗号；含逗号的条件写法
        // 由 buildCondition 逐条规则自带正则兜底，这里取到第一个逗号即可）
        const rest = mc[2];
        const cm = rest.match(/^([^，,。]*?)[，,]\s*([\s\S]*)$/);
        const condText = cm ? cm[1] : rest;
        const c1 = buildCondition(condText);
        // 只有条件认得出、且后面确实还有 then 动作时才接管；否则退回原逻辑
        if (c1 && cm && cm[2].trim()) {
          condPrefix = mc[1].trim();
          cond = c1;
          body = cm[2].replace(/^则/, '').trim();
        }
      }
    }
    // 「若可能将所有友方单位移至前线，否则敌方弃牌」——"否则"是 else 分支。
    //   先单独处理这个带"否则"的形式：条件成立→then（可移前线），否则→else（敌方弃牌）。
    if (!cond) {
      const elseMatch = s.match(/^(?:若|如果|假如)([^，,。]*?)[，,]\s*否则[，,]?\s*(.*)$/);
      if (elseMatch) {
        const c0 = buildCondition(elseMatch[1]);
        const thenActs = [{ op: 'move', target: { sel: 'all', side: 'friendly' }, to: 'frontline' }];
        if (c0) {
          const elseBody = elseMatch[2];
          const elseRes = P.parse(elseBody, { _noSplit: false, eventRef: opts.eventRef });
          if (!elseRes || elseRes.unparsed || !(elseRes.actions && elseRes.actions.length)) {
            return P.unparsed(s, '否则分支未能完整解析');
          }
          const elseActs = elseRes.actions;
          // 条件 c0 是"能移前线"（hasRoom）；取反 = 前线满/被占（frontlineEnemyOrFull）
          return {
            actions: [{ op: 'conditional', condition: { op: 'frontlineEnemyOrFull', side: 'self' }, then: elseActs, else: thenActs }],
          };
        }
      }
    // ★ 条件子句里允许「，并且 B」这种并列：以前 [^，,。]* 会在第一个逗号处截断 →
    //   第二个条件被**静默丢掉**，只留下第一个（「如果友方占领了前线，并且你控制数量不小于2的Mk坦克，则…」）。
    const condMatch = s.match(/^(?:如果|若|假如)\s*([^，,。]*(?:[，,]\s*(?:并且|而且|同时|以及|且)[^，,。]*)*)[，,]?\s*(?:则|就)?\s*([\s\S]*)$/);
    if (condMatch) {
      cond = buildCondition(condMatch[1]);
      if (cond && condMatch[2]) body = condMatch[2].replace(/^则/, '').trim();
      else if (!cond && condMatch[2] && /^(?:可能|可以|可行|办得到)$/.test(String(condMatch[1] || '').trim()) && !/否则/.test(s)) {
        // ★ 「若可能，X」= **hedge**（"能做到就做"，不是可求值的条件）：
        //   剥掉它、照常解析 X —— 否则「若可能，揭示一张隐蔽单位，抽一张牌」会整句变"未实现"。
        //   ⚠ 不吃带「否则」的句子（那是"若可能…否则…"的抉择/条件结构，由上面的分支专门处理）。
        body = condMatch[2].replace(/^则/, '').trim();
      }
      else if (!cond) {
        // ★★ 条件**认不出来**时不许"把条件子句当动作解析"。
        //   以前 cond=null 会把整句（含"若…，"）交给下面的通用动作解析：
        //   动作词从整串里扫阵营/配额/限定词 → 条件里的词被当成**目标属性**
        //   （「若敌方手牌不少于3张，抽一张牌」曾编成"给对手抽一张"）——静默错。
        //   这里直接放弃（上层记一条"未能自动解析"），宁可显式失败，也不产出错效果。
        return P.unparsed(s, '条件子句无法识别');
      } else cond = null;
    }
    }
    s = body;
    // 条件句的动作部分可能含多个动作（「获得两个指挥点，抽一张牌」）。
    //   splitTop 只在函数开头对**原始句**做过（那时还是"如果…"整句，被跳过），
    //   所以这里要对 body 再做一次拆分：拆得开就逐段递归解析、包进同一个 then。
    if (cond) {
      const parts = splitTop(s);
      if (parts.length > 1) {
        const merged = [];
        let allOk = true;
        let ctrlTaken = false;   // 同上：夺取控制权之后的「该单位」指被夺取的单位
        for (const part of parts) {
          const popts = { _noSplit: true, _slot: opts._slot, eventRef: opts.eventRef };
          if (ctrlTaken && /该单位|该目标|其|它/.test(part)) popts.pronounOverride = 'defender';
          const r = P.parse(part, popts);
          if (!r || r.unparsed || !(r.actions && r.actions.length)) { allOk = false; break; }
          if (r.actions.some(x => x.op === 'takeControl')) ctrlTaken = true;
          r.actions.forEach(a => merged.push(a));
        }
        if (allOk && merged.length) return wrapCond(merged, cond);
        if (!allOk) return P.unparsed(s, '条件句的动作部分未能完整解析');
      }
    }

    // 条件包装：把前面 0.6) 抠出来的 `cond` 包到动作外面。
    //   ⚠ 本函数下面有**多个提前 return** 的分支（LONGTAIL4 / LONGTAIL3 / LONGTAIL / ACTIONS），
    //     它们若不自己调用 wrapCond，就会把「若…，」这个限定**静默丢掉** →
    //     效果从"条件触发"退化成"无条件触发"（比解析失败更危险，因为不会报错）。
    function wrapCond(acts, condition) {
      if (!condition) return actText ? { actions: acts, actText: actText } : { actions: acts };
      const then = (Array.isArray(acts) ? acts : [acts]).filter(Boolean);
      if (!then.length) return { actions: acts };
      const wrapped = { actions: [{ op: 'conditional', condition: condition, then: then }] };
      // 中段条件：前置动作拼在 conditional 之前（"额外获得三个指挥点槽，如果你控制…"）
      const out = condPrefix ? withCondPrefix(wrapped.actions, declare) : wrapped;
      if (out && actText) out.actText = actText;
      return out;
    }
    /* 中段条件的前置动作：把 `condPrefix` 里的动作解析出来，拼在 conditional 之前。
     *   「额外获得三个指挥点槽，如果你控制巡航舰，随机消灭一个敌方单位」
     *   → [ gainKreditSlot(3), conditional{controlsType cruiser → destroy random enemy} ]
     * 前置动作解析失败就**宁可不返回**（让调用方回退），也不能悄悄丢掉条件。 */
    function withCondPrefix(acts, declare) {
      const out = [];
      const targets = [];
      if (condPrefix) {
        const pre = P.parse(condPrefix, { _noSplit: false, eventRef: opts.eventRef });
        if (!pre || pre.unparsed || !(pre.actions && pre.actions.length)) {
          return P.unparsed(condPrefix, '条件前的动作未能完整解析');
        }
        pre.actions.forEach(a => out.push(a));
        (pre.targets || []).forEach(t => targets.push(t));
      }
      for (const t of declare || []) {
        if (targets.some(x => x.id === t.id)) return P.unparsed(s, '条件前后使用了同名目标，需要区分目标引用');
        targets.push(t);
      }
      acts.forEach(a => out.push(a));
      return { actions: out, targets: targets.length ? targets : undefined };
    }
    var actText = '';   // ★ var 提前挂到函数作用域：wrapCond 在 2966/2972 会先于本行读它，let 会 TDZ
    let act = null;

    /* ★ 0.44) 「本回合内，X」→ X，并把其中的**常驻修正**标成 `duration:'turn'`。
     *   以前这个前缀被忽略 → `grantMod` 无条件写成永久修正
     *   （「本回合内，本单位无法攻击总部」会**永远**打不了总部）。
     *   只给 grantMod 打标记（其他 op 有自己的时长机制）。 */
    if (!(opts && opts._noTurnDur)) {
      const tp = s.match(/^(?:仅)?本回合内\s*[，,]?\s*(.+)$/);
      if (tp && tp[1].trim()) {
        const inner = P.parse(tp[1].trim(), Object.assign({}, opts, { _noTurnDur: true }));
        if (inner && !inner.unparsed && inner.actions && inner.actions.length) {
            inner.actions.forEach(function (a) { if (a && a.op === 'grantMod' && !a.duration) a.duration = 'turn'; });
            return inner;
        }
        return P.unparsed(s, '本回合时限后的效果未能完整解析');
      }
    }

    /* ★ 0.45) 「（如果）场上有X，Y」→ conditional(场上有X, Y)
     *   X 可以是兵种/词条词，**也可以是卡名**（「如果场上有"空投师"，获得+2+2」）。
     *   以前这条只在"兵种词"时认，卡名就直接不认 → 整句退化成"选择一个单位"，
     *   条件丢失、buff 还会打到那张卡身上。
     *   ⚠ 这里**显式组装 conditional**（不走 COND_RULES），避免又踩"入口处 cond 未就绪"的坑。 */
    if (!(opts && opts._noFieldCond)) {
      const FC = s.match(/^(?:如果|若)?\s*场上有\s*([^，,。]{2,12}?)\s*[，,]\s*(.+)$/);
      if (FC) {
        const raw = FC[1].replace(/[“”"「」]/g, '').trim();
        const side = /敌|对方|对手/.test(raw) ? 'enemy' : 'self';
        const w2 = raw.replace(/^(?:友方|我方|敌方|对方|对手)/, '').replace(/(?:的)?单位$/, '').trim();
        const filt = (w2 && (P.traitFilterOf(w2) || P.typeFilterFromText(w2))) ||
          (w2 && /^[\u4e00-\u9fa5A-Za-z0-9·\-]{2,12}$/.test(w2) ? { nameIncludes: w2 } : null);
        if (filt) {
          const inner = P.parse(FC[2], Object.assign({}, opts, { _noFieldCond: true }));
          if (inner && !inner.unparsed && inner.actions && inner.actions.length) {
            return {
              actions: [{ op: 'conditional', condition: { op: 'controlsType', side: side, cmp: '>=', value: 1, filter: filt }, then: inner.actions }],
              targets: inner.targets,
            };
          }
          return P.unparsed(s, '场面条件后的效果未能完整解析');
        }
      }
    }

    /* ★ 0.5) 分号段由 **compiler 拆行**处理（见 compiler.js 的 lines 拆分）。
     *   之前在这里做多段拆分是**错的位置**：入口处 `cond` 还没就绪，
     *   拆开第一段会让它的 conditional 包装整个丢掉（比不拆更糟）。保留这段注释以免重犯。 */

    /* ★ 0.52) 句首的「将其移至下一阵线/前线」= **自己**移过去。
     *   单位自身效果里句首的「其」指的就是本单位，但通用"其"兜底是"随机友方单位"
     *   → 空投师会变成"随机把一个友方单位挪到前线"（错）。
     *   只在**句首**拦（"消灭一个单位，将其移至前线"那种由前面的目标承接，不在这里）。 */
    //   ⚠⚠ 只有在**没有监听上下文**（opts.eventRef）时才把"其"当自己。
    //   「友方单位部署时，将其移至前线」里的"其"指的是**刚部署的那个单位**（eventUnit），
    //   有 eventRef 时交给内置规则（它能产出 {sel:'ref', ref:'eventUnit'}）。
    //   我第一版没判这个，把远征旗舰那张测试卡改坏了（move 的对象从 eventUnit 变成 self）。
    if (!(opts && opts._noSelfMove) && !(opts && opts.eventRef)) {
      //   正则要容忍**没被剥掉的触发前缀**（「该单位部署时将其移至下一阵线」整句进来时），
      //   并且用 `$` 锚到行尾 —— 否则内置的「将其移至」通用规则会抢先，
      //   把"其"兜底成"随机友方单位"（空投师就是被这么吃掉的）。
      const sm = s.match(/^(?:(?:该|本)单位\s*(?:部署|登场|出场)时?[，,]?\s*)?(?:并将其|将其|使其|将该单位|将该)\s*移(?:至|到|上)\s*(?:下一阵线|前线)$/);
      if (sm) return wrapCond([{ op: 'move', to: 'frontline', target: 'self' }], cond);
    }

    /* ★ 0.55) 「若可能，X」/「如果可能，X」= **能做就做**（做不了静默跳过，不算失败）。
     *   它不是独立动作，只是给后面的 X 加一层"尽力而为"：所以剥掉前缀，
     *   把 X 交给原语层正常解析（move / summon 这类在无合法目标时本身就会跳过）。
     *   ⚠ 不处理的话，分号后的「若可能，加入前线」会整段解析失败被丢
     *     —— 这是"分号后面的句子全被忽略"的一个直接原因。 */
    if (!(opts && opts._noMaybe)) {
      const maybe = s.match(/^(?:若|如果)\s*(?:有)?可能\s*[，,]?\s*(.+)$/);
      if (maybe && maybe[1].trim()) {
        const inner = P.parse(maybe[1].trim(), Object.assign({}, opts, { _noMaybe: true }));
        if (inner && !inner.unparsed && inner.actions && inner.actions.length) return wrapCond(inner.actions, cond);
        return P.unparsed(s, '尽可能执行的效果未能完整解析');
      }
    }

    /* ★★ 0.6) 「每有…」数量前缀（制作者口径：这是**条件原语**，以前整条被静默忽略）
     *   「每有一个敌方单位使该单位获得+1+1」「场上每有一个友方太空单位，造成2点伤害」
     *   做法：抠出"每有"后面的数量描述 → 转成 selectUnits 的 spec →
     *         解析后面的主体，把主体里的**数值参数**包成 `{count: spec, times: 原值}`。
     *   例：`每有一个友方太空单位，造成2点伤害` → damage{amount:{count:{side:'friendly',
     *       filter:{unitType:'space'}}, times:2}}（运行时由 effects.js 的 num() 求值）。 */
    if (!(opts && opts._noPer) && /^(?:场上)?每有/.test(s)) {
      // 前缀边界用**零宽前瞻**定：who 到「使/则/时/，/获得/造成/抽/对」之前为止。
      //   ⚠ 非贪婪会把「黑盾第二团」截成「黑盾」，主体也跟着切错
      //     （曾产出 addCardToHand{name:"第二团"} 这种垃圾）。
      const PER = s.match(/^(?:场上)?每有\s*(?:一?张|一个|一名|一)?\s*([^，,。]{2,14}?)\s*(?=使|则|时|，|,|获得|造成|抽|对|移|弃|消|压制)(?:时[，,]?|[，,])?\s*(.+)$/);
      // 「每有一张牌」这类数量取自**手牌**，selectUnits 选不出来 → 不接（免得算成 0 个单位）
      if (PER && !/牌|卡牌/.test(PER[1])) {
        const who = PER[1].replace(/[“”"「」]/g, '').trim();
        const spec = { sel: 'all', side: /敌|对方/.test(who) ? 'enemy' : 'friendly' };
        const cnt = who.replace(/^(?:我方|友方|敌方|对方|你的)\s*/, '');
        const t = vsTypeFromText ? vsTypeFromText(cnt.replace(/(?:单位|卡牌|牌)$/, '')) : null;
        if (t) spec.filter = { unitType: t };
        else if (cnt && !/^(?:单位|目标|卡牌|牌)?$/.test(cnt)) spec.filter = { nameIncludes: cnt };
        const inner = P.parse(PER[2], Object.assign({}, opts, { _noPer: true }));
        if (inner && !inner.unparsed && inner.actions && inner.actions.length) {
          // 把主体里的固定数值包成"数量 × 原值"
          const NUM_FIELDS = ['amount', 'attack', 'defense', 'count', 'value', 'times'];
          inner.actions.forEach(a => {
            if (!a || typeof a !== 'object') return;
            NUM_FIELDS.forEach(f => {
              if (typeof a[f] === 'number') a[f] = { count: spec, times: a[f] };
            });
          });
          /* ★★ 制作者口径（2026-09-26）：「每有一张X，对战Y时+N攻击力」是**光环/常驻**，不是一次性动作。
           *   写成动作时 grantMod 会在挂载那一刻用 num() 把 count 求值成数字（之后场面变化不再反映）；
           *   而 passiveRules.vsType 里的对象会被 engine.js attackPowerAgainst 的 addUp() **每次攻击现算**。
           *   所以「每有 + 对战修正」整句改发 passive + passiveRules（真正的常驻光环语义）。 */
          const modA = (inner.actions.length === 1) ? inner.actions[0] : null;
          if (modA && modA.op === 'grantMod' && modA.target === 'self' && (modA.mod === 'vsType' || modA.mod === 'vsHq')) {
            const pr = {};
            if (modA.mod === 'vsType' && modA.unitType) {
              pr.vsType = {}; pr.vsType[modA.unitType] = {};
              if (modA.attack != null) pr.vsType[modA.unitType].attack = modA.attack;
              if (modA.damage != null) pr.vsType[modA.unitType].damage = modA.damage;
              if (modA.double) pr.vsType[modA.unitType].double = true;
            } else {
              pr.vsHq = (modA.attack != null) ? modA.attack : ((modA.damage != null) ? modA.damage : 1);
            }
            const eff = { trigger: 'passive', passiveRules: pr };
            if (cond) eff.condition = cond;
            return { actions: [{ _standaloneEffect: eff }], targets: [] };   // 必须作为 action 返回（compiler.js:1181）
          }
          return wrapCond(inner.actions, cond);
        }
        return P.unparsed(s, '「每有」条件后的效果未能完整解析');
      }
    }

    // 0.65) 带卡名的卡牌操作（加入手牌 / 加入阵线 / 洗入卡组）——必须抢在通用动作之前，
    //       否则会被 /加入手牌/ 这类规则吃掉卡名，导致"卡池中找不到「undefined」"
    const cardOps = parseCardOp(s);
    // ★ 必须带条件包装：否则「如果行动花费不小于8，将一张复制加入手牌」里的
    //   "如果…" 会被**静默丢掉**，效果退化成无条件（比解析失败更危险，因为它不报错）。
    if (cardOps) return cond ? wrapCond(cardOps, cond) : { actions: cardOps };
    // 0.7) 代词「使其 / 令其 / 使其获得…」→ 指向事件主角（监听类效果里就是刚触发的那个单位）
    //      只在编译器告知"这是监听触发"时才这么做，避免影响"抑制一个友方单位，然后使其…"这类语句
    //      ★ pronounOverride：同一句里**前一段刚夺取了某个单位**（takeControl）时，
    //        后续「该单位」指被夺取的那个（defender），不是事件主角——
    //        「控制被攻击单位，并使该单位获得+2+2」的 +2+2 是给被抢来的单位，不是给攻击者。
    let pronounRef = opts.pronounOverride || opts.eventRef || null;
    if (pronounRef) {
      const pm = s.match(/^[使令]\s*(?:其|该单位|该卡牌|该卡|此单位|它)(?![们])/);
      if (pm) s = s.slice(pm[0].length).trim();
      // ★ 「将其返回手牌」这种**以"将其"开头**的监听句：「其」同样是事件主角。
      //   以前这里判成"不是代词"→ pronounRef 被清空 → 规则退化成"随机一个友方单位"
      //   （「友方单位部署时，将其返回手牌」会随机弹回一个友方单位，包括本卡自己）。
      //   这里**不剥前缀**：让具体规则自己产出 target（如 LONGTAIL4 的 _pronoun 规则）。
      else if (/^[将把]\s*(?:其|它)(?![们])/.test(s)) { /* 「将其X」= 事件主角 */ }
      else if (/该单位|该目标|这个单位|该敌军|该敌/.test(s) && !/一[个名辆架艘张]/.test(s)) {
        s = s.replace(/该单位|该目标|这个单位|该敌军|该敌/g, '').trim();   // 「消灭该单位」→ 消灭（目标=事件主角）
      } else pronounRef = null;
    }
    // 第四批（对战修正/费用取值/能力类，最具体）→ 第三批 → 第二批 → 常规动作表
    for (const a of (P.LONGTAIL4 || [])) {
      const m = s.match(a.re);
      if (m) {
        actText = m[0];
        const act0 = a.build(m, opts);
        if (act0) {
          // ★ 复合规则：一次产出「多个动作 + 自己的选靶声明」（不走后面的通用选靶流程）。
          //   用于「抑制一个友方单位，然后使其获得X」这类**代词跨动作指代**的句子 ——
          //   两个动作必须指向同一个目标 id，通用选靶会各生成一个 t1/t2。
          if (act0._compound) {
            const actsC = (act0.actions || []).filter(Boolean);
            if (cond) return wrapCond(actsC, cond);
            return { actions: actsC, targets: act0.declare, actText: actText };
          }
          const acts0 = Array.isArray(act0) ? act0 : [act0];
          acts0.forEach(function (x) {
            if (!x) return;
            if (pronounRef && (x._pronoun || !x.target)) x.target = { sel: 'ref', ref: pronounRef };
            if (x._pronoun && !pronounRef) x.target = { sel: 'random', side: 'friendly' };
            delete x._pronoun;
          });
          return wrapCond(acts0, cond);
        }
      }
    }
    // 第三批（最具体）→ 第二批 → 常规动作表
    for (const a of (P.LONGTAIL3 || [])) {
      const m = s.match(a.re);
      if (m) {
        actText = m[0];
        const act0 = a.build(m, opts);
        if (!act0) continue;                                  // 规则认不出这句 → 继续试后面的规则
        if (act0._needPronoun && !pronounRef) continue;        // 只在事件主角存在时才可用
        // ★ 复合规则：一次产出「多个动作 + 自己的选靶声明」（与 LONGTAIL4 同一套 _compound 约定）。
        //   用于「使一个友方坦克单位获得+1+1,奋战和守护」这类**一个目标吃多段效果**的句子。
        if (act0._compound) {
          const actsC = (act0.actions || []).filter(Boolean);
          actsC.forEach(function (x) {
            if (pronounRef && x._pronoun) x.target = { sel: 'ref', ref: pronounRef };
            if (x._pronoun && !pronounRef) x.target = { sel: 'random', side: 'friendly' };
            delete x._pronoun;
          });
          if (cond) return wrapCond(actsC, cond);
          return { actions: actsC, targets: act0.declare, actText: actText };
        }
        if (act0.op === 'discoverCard') {
          const d = act0.desc || '';
          // ★ 默认**不设** cardType：只有原文明确是"单位"或含兵种词时才限定为 unit。
          //   「开发一张卡组中花费不大于3的牌」里的"牌"是任意类型（手写 filter 只有 maxCost）。
          const f = {};
          const UNIT_WORD = /单位|坦克|步兵|炮兵|火炮|战斗机|轰炸机|巡洋舰|巡地舰|驱逐舰|太空|空军|陆军|舰|工事|建筑/;
          if (UNIT_WORD.test(d)) f.cardType = 'unit';
          if (/空军/.test(d)) { f.unitType = ['fighter', 'spacefighter', 'bomber']; f.cardType = 'unit'; }
          if (/指令/.test(d)) { f.cardType = 'order'; delete f.unitType; }
          if (/协约国/.test(d)) f.setIn = ['USG', 'av76'];
          const mx = d.match(/不大于\s*([一二两三四五六七八九十\d]+)/);
          const mn = d.match(/不小于\s*([一二两三四五六七八九十\d]+)/);
          if (mx) f.maxCost = num(mx[1]);
          if (mn) f.minCost = num(mn[1]);
          // ★ 描述串里剩下的若还含"阵营/国家/系列"词，说明它**不是卡名**，不能塞进 filter.name。
          //   引擎的 discover 把 name 当**卡名字串**做 indexOf 匹配（见 effects.js 的 match()）——
          //   而「协约国」这种阵营名，没有任何一张卡的名字里含它 → 卡池被过滤成空
          //   → 走"该卡池里没有可供开发的牌"分支，**整张卡静默失效**。
          //   （「开发一张协约国空军」曾因此：setIn 对了、name 却错，两条并列 ⇒ 交集为空。）
          // ★★ 数量词必须先剥掉：「开发**两张**坦克」里的"两张"若不剥，
          //   会连着"坦克"一起被当成**卡名** → 卡池里没有这张卡 → discover 静默失效。
          //   以前只剥「一张|一个」，"两张/三张/N张"全漏（工业化的 bug）。
          const cntM = d.match(/([一二两三四五六七八九十\d]+)\s*张/);
          const devCnt = cntM ? (num(cntM[1]) || 1) : 1;
          // 兵种词（坦克/步兵/炮兵/战斗机…）同样要从"名字"里剥出来，改成 filter.unitType。
          //   ⚠ typeFilterFromText 返回的是 **{unitType:…} 对象**（不是字符串），
          //     要 Object.assign 进 f，不能直接 f.unitType = 返回值。
          const devT = (P.typeFilterFromText ? P.typeFilterFromText(d.replace(/[一二两三四五六七八九十\d]+\s*张/g, '')) : null);
          if (devT && !f.unitType) Object.assign(f, devT);
          let nm = d.replace(/[一二两三四五六七八九十\d]+\s*张|一张|一个|单位|指令|空军|花费[^，,。]*/g, '').trim();
          // 兵种词本身不该留在名字里（「坦克」已经被吃进 unitType 了）
          if (devT && nm && P.typeFilterFromText && P.typeFilterFromText(nm)) nm = '';
          //   排除阵营/国家/系列词（复用 SET_WORD_FILTER 这份唯一权威名单，不另抄）
          for (const w in SET_WORD_FILTER) if (nm.indexOf(w) >= 0) { nm = ''; break; }
          if (nm && nm.length >= 2 && !/主力|主国|盟国|卡组|轻型|帝国|进攻/.test(nm)) f.name = nm;
          const out = { op: 'develop', filter: f };
          // 「开发**两张**坦克」→ 数量也带出去（引擎按 count 开发多张）
          if (devCnt > 1) out.count = devCnt;
          if (/卡组中|卡组里/.test(s)) out.from = 'deck';
          /* ★★ 「从"A"、"B"和"C"中开发一张」——**指名几张卡的三选一**
           *   （2026-09-22 制作者例：「从"V-9特","反坦克手雷"和"15号通用载具"中开发一张」）
           *   旧行为：整段只产出 `{op:'develop', filter:{}}`（**空卡池**）→ 引擎随机不到任何卡，静默失效。
           *   做法：把引号里的卡名全部抠出来 → **编译期转成卡 id**（引擎的 develop 只认 id，
           *   `KG.pool` 里有 id↔name）→ `filter.cardIds`（引擎早已支持这种"指名道姓的卡池"，
           *   见 OPS.develop 的 cardIds 分支），卡池正好 = 这几张 → 抽 3 选 1 就是"从这几张里选一张"。
           *   ⚠ 只有**引号括起来的**才算名单：不引号会跟「开发一张协约国空军」这类描述混淆。 */
          {
            // ⚠ 名单要从**整句 s** 里抠，不能用 `d`：这句的"开发"后面紧跟"一张"，
            //   `d`（开发之后的描述）是空的，引号全在"开发"**前面**。
            const quoted = String(s).match(/[“"「]([^”"」]+)[”"」]/g);
            if (quoted && quoted.length >= 2) {
              const names = quoted.map(function (x) { return x.replace(/[“”"「」]/g, '').trim(); }).filter(Boolean);
              const pool = root.KG && root.KG.pool;
              const ids = names.map(function (n) {
                if (!pool) return null;
                const keys = Array.isArray(pool) ? null : Object.keys(pool);
                if (keys) {
                  // 先精确同名，再退化到子串（卡面常把后缀写省）
                  let hit = keys.find(function (k) { return pool[k] && String(pool[k].name) === n; });
                  if (!hit) hit = keys.find(function (k) { return pool[k] && String(pool[k].name || '').indexOf(n) >= 0; });
                  return hit || null;
                }
                const arr = pool || [];
                const c = arr.find(function (x) { return x && String(x.name) === n; })
                  || arr.find(function (x) { return x && String(x.name || '').indexOf(n) >= 0; });
                return c ? c.id : null;
              }).filter(Boolean);
              if (ids.length >= 2) { out.filter = { cardIds: ids }; out.from = 'pool'; }
              else if (ids.length === 1) { out.filter = { cardIds: ids }; out.from = 'pool'; }
            }
          }
          if (/进攻/.test(d)) { out.filter = { cardIds: ['av76/command/-17'], name: '进攻' }; out.from = 'pool'; }
          if (/轻型巡洋舰/.test(d)) out.filter = { cardType: 'unit', unitType: 'cruiser', maxCost: 5 };
          if (/帝国坦克/.test(d)) out.filter = { cardType: 'unit', cardIds: ['deran/units/_29', 'deran/units/_21', 'deran/units/_10'] };
          if (/新星联盟/.test(d)) out.filter = { cardType: 'unit', cardIds: ['USG/units/_39', 'USG/units/_38'] };
          // 「并使其加入战场」→ 开发后直接进场（引擎的 discover 支持 thenSummon）
          if (/并使其加入战场|使其加入战场/.test(s)) out.thenSummon = true;
          // 「回合结束时，将其消灭」→ 进场的那张当回合末消灭（引擎支持 killAtTurnEnd）
          if (/回合结束时[^。；;]*将其消灭/.test(s)) out.killAtTurnEnd = true;
          return wrapCond([out], cond);
        }
        const acts0 = Array.isArray(act0) ? act0 : [act0];
        acts0.forEach(function (x) {
          if (!x) return;
          if (pronounRef && (x._pronoun || !x.target)) x.target = { sel: 'ref', ref: pronounRef };
          if (x._pronoun && !pronounRef) x.target = { sel: 'random', side: 'friendly' };
          delete x._pronoun;
        });
        // ★ 这一层是"提前返回"，必须自己带上前面 0.6) 抠出来的条件包装 ——
        //   否则「若敌方手中具有明牌，具有减2花费」会丢掉"若…"限定，变成无条件触发。
        //   （末尾的 conditional 包装在 833 行，但这些 LONGTAIL 规则根本走不到那里。）
        return wrapCond(acts0, cond);
      }
    }
    // 长尾句式先试（更具体）
    for (const a of (P.LONGTAIL || [])) {
      const m = s.match(a.re);
      if (m) { act = a.build(m, opts); actText = m[0]; break; }
    }
    if (act) {
      // ★ discard 也在内：弃牌**不需要玩家选一个单位**（"弃一张牌"/"敌方弃N张牌"都是随机弃），
      //   漏在这里会被强制挂上"选择一个单位"的目标声明，产出 target:"t1" 这种垃圾。
      // ★ transformHandCard 也在内：它操作的是**手牌**，不是让玩家去选一个单位。
      const noTargetOps = ['endTurnNow', 'auraBuff', 'condOpCost', 'debuffTemp', 'nextTurnKredits', 'unitDeployedOpCost', 'counterDeploy', 'destroyAll', 'hqMaxUp', 'addCardToHand', 'discard', 'discardAll', 'mill', 'transformHandCard'];
      if (noTargetOps.indexOf(act.op) < 0) { /* 需要目标，走下面常规流程 */ }
      else return wrapCond([act], cond);
    }
    // 常驻规则类（无法被X / 不会受到X / 无视X / 免疫）优先，避免"无法被压制"被当成"压制"
    for (const a of ACTIONS) {
      if (!/无法|不会|无视|免疫/.test(String(a.re))) continue;
      const m = s.match(a.re);
      if (m) { act = a.build(m, opts); actText = m[0]; break; }
    }
    for (const a of ACTIONS) {
      if (act) break;
      const m = s.match(a.re);
      if (m) { act = a.build(m, opts); actText = m[0]; break; }
    }
    if (!act) return null;
    if (act.op === 'grant') {
      const cn = act.keyword;
      const kw = KW_CN[cn] || (cn && cn.toLowerCase().replace(/[\s-]/g, ''));
      if (!kw || !/^[a-zA-Z]{3,}$/.test(kw)) return null;      // 认不出的词条名不硬猜
      act.keyword = kw;
    }

    // 2) 选靶方式 / 阵营 / 区域 / 条件（可以在句子的任何位置）
    let sel = null, side = null, zone = null, count = null;
    const filter = {};
    for (const q of QUANT) {
      const m = s.match(q.re);
      if (!m) continue;
      sel = q.sel;
      if (q.sel === 'many' || q.sel === 'upto') count = num(m[1]);
      if (q.sel === 'upto') sel = 'all';
      break;
    }
    // ★ 「每有一个<阵营>X单位」是**数量限定**（配合动态数值），不是目标筛选条件。
    //   不剥掉的话「每有一个友方太空单位，对一个单位造成2点伤害」会把"友方太空"
    //   当成目标 filter/阵营（重锤战术：目标被错限成"友方太空单位"）。
    const quantClause = /每有\s*[一二两三四五六七八九十\d]*\s*(?:个)?\s*(?:敌方|对方|友方|我方)\s*[\u4e00-\u9fa5]{0,6}单位/;
    const sForPick = s.replace(quantClause, '');
    let sideWeak = false;
    for (const sd of SIDE) { const m = sForPick.match(sd.re); if (m) { side = sd.side; sideWeak = !!sd.weak; break; } }
    for (const z of ZONE) { const m = s.match(z.re); if (m) { zone = z.zone; break; } }
    for (const f of FILTERS) {
      const m = sForPick.match(f.re);
      if (!m) continue;
      Object.assign(filter, typeof f.filter === 'function' ? f.filter(m) : f.filter);
    }
    // 「其他单位」这类：不分敌我（双方都算），由 filter 里的标记把它从"负面默认敌方"扭转过来
    if (filter.bothSides) { delete filter.bothSides; side = 'any'; }

    // 3) 组装
    // 「目标」与「单位」的区别（制作者确认）：
    //   写「目标」  = **单位 + 总部**都能选（总部属于目标）
    //   写「单位」  = 只能选单位 → 「对一个单位造成X点伤害」就是"不能指向总部"
    //   两者都没写（光是「造成X点伤害」）= 同「目标」
    const saysTarget = /目标/.test(s);
    const saysUnit = /单位/.test(s);
    const hqPickable = act.op === 'damage' && !saysUnit;      // 只有"打伤害"对总部有意义

    // 「所有目标」= 所有单位 + 对应阵营的总部（拆成 damageAll + damageHQ，与「空中打击」的既有编码一致）
    if (act.op === 'damage' && sel === 'all' && saysTarget) {
      const unitSide = side || 'both';
      const hqSides = (!side || side === 'any') ? ['self', 'enemy'] : [side === 'enemy' ? 'enemy' : 'self'];
      const acts = [Object.assign({ op: 'damage', amount: act.amount, target: Object.assign({ sel: 'all', side: unitSide }, zone ? { zone: zone } : {}) },
        Object.keys(filter).length ? { filter: filter } : {})];
      hqSides.forEach(function (sd) { acts.push({ op: 'damageHQ', amount: act.amount, side: sd }); });
      return wrapCond(acts, cond);
    }

    // 这些动作自带作用对象（或作用于玩家/全场），不需要玩家再选目标
    const NO_TARGET_OPS = ['draw', 'drawUntil', 'gainKredits', 'gainKreditSlot', 'loseKredits', 'loseKreditSlots',
      'nextTurnKredits', 'nextTurnSlots', 'hqMaxUp', 'hqArmor', 'damageHQ', 'upgradeSelf', 'summon', 'addCardToHand',
      'shuffleIn', 'shuffleRandomSet', 'damageAll', 'destroyAll', 'healAll', 'discardAll', 'mill',
      'endTurnNow', 'hqKeyword', 'suppressHQ', 'auraBuff', 'condOpCost', 'debuffTemp', 'gainRandomKw',
      'copyHandCard', 'opCostModAll', 'handOpCostMod', 'setHandCost', 'discover', 'develop',
      'noDrawNextTurn', 'noKreditSlotNextTurn', 'move', 'takeControl',
      'hqEnchant'];   // 总部附魔自包含（effects 自带监听+目标），不需要选靶（2026-09-24）
    const needsTarget = !!(act.op && NO_TARGET_OPS.indexOf(act.op) < 0);
    if (act.op === 'upgradeSelf') act.target = 'self';
    // 规则构建里已经硬定了 target:'self'（"无法被抑制""不会受到反击伤害"这类常驻自修正）
    //   → 不要再当成"需要玩家选目标"，否则会被改写成 t1 这种选靶 id。
    const explicitSelf = act.target === 'self';
    // ★★ 规则里已经**显式写好内联目标**（`{sel:'ref'|'all'|'random', ...}`）→ **保持不动**。
    //   否则会落到下面第三个分支被改写成 `t1`（= 弹出"选择一个单位"），把卡面语义整个改掉：
    //   实测「…并**使其**获得+1+1」的 buff 被改成让玩家选人（卡面明明是给刚召唤的那张加）。
    //   （`move` 这类已进 NO_TARGET_OPS 的走不到这里，但 buff 等在 needsTarget 里，必须在这挡一道。）
    const inlineTarget = act.target && typeof act.target === 'object' && !!act.target.sel;
    // ★ 「本单位X时，获得…」这类**本单位触发**语境 + 正面动作 + 无选靶词
    //   → 默认作用于**自己**（"获得"的主语是本单位），不要再生成"选一个友方单位"。
    //   （「本单位移至前线时，获得+1攻击力」曾因此被解析成"选一个友方单位 +1攻击力"。）
    const evSelf = !!opts.eventRef && !!POSITIVE[act.op] && !sel && !/一个|随机|所有|每个|全体/.test(s);
    if (needsTarget && inlineTarget) {
      // 内联目标：原样保留
    } else if (needsTarget && (explicitSelf || evSelf) && !pronounRef) {
      act.target = 'self';
    } else if (needsTarget && pronounRef) {
      // 「使其…」= 事件主角，不需要玩家再选一次
      act.target = { sel: 'ref', ref: pronounRef };
    } else if (needsTarget) {
      // ★ 制作者规则：**非指向性群体增益默认只作用于友方**（即使卡面写"所有"）。
      //   「使场上所有USG和星盟单位获得+2+3」曾因此编成 side:'any'（连敌人一起加）。
      if (sideWeak && POSITIVE[act.op]) side = 'friendly';
      if (!side) side = POSITIVE[act.op] ? 'friendly' : (sel === 'one' || !sel ? 'any' : 'enemy');
      if (sel === 'one' || !sel) {
        // 指向性：交给玩家选。「目标」把总部也算进去，「单位」就只有单位
        const id = 't' + (declare.length + 1);
        const kind = hqPickable ? 'any' : 'unit';
        const label = (side === 'enemy' ? '敌方' : side === 'friendly' ? '友方' : '') + (kind === 'any' ? '目标' : '单位');
        declare.push({
          id, side, kind, zone: zone || undefined,
          filter: Object.keys(filter).length ? filter : undefined,
          prompt: '选择一个' + label + (zone === 'frontline' ? '（前线）' : '') +
            (kind === 'any' ? '（可选总部）' : ''),
        });
        act.target = id;
      } else {
        act.target = Object.assign({ sel: sel === 'random' ? 'random' : 'all', side, zone: zone || undefined },
          Object.keys(filter).length ? { filter } : {}, count ? { count } : {});
      }
    } else if (Object.keys(filter).length && ['draw', 'drawUntil', 'gainKredits', 'gainKreditSlot', 'loseKredits',
      'loseKreditSlots', 'nextTurnKredits', 'nextTurnSlots', 'hqMaxUp', 'hqArmor', 'damageHQ', 'endTurnNow',
      'hqKeyword', 'suppressHQ', 'noDrawNextTurn', 'noKreditSlotNextTurn', 'mill', 'discardAll', 'healAll',
      'auraBuff', 'condOpCost', 'debuffTemp', 'gainRandomKw', 'upgradeSelf'].indexOf(act.op) < 0) {
      // ★ 只有**真的能用 filter 的动作**才挂 filter：抽牌/拿指挥点这类动作不吃 filter，
      //   挂上去纯属噪声（「揭示一张隐蔽单位，抽一张牌」曾因此给 draw 挂上 keyword:stealth）。
      act.filter = filter;
    }
    if (side && ['draw', 'gainKredits', 'gainKreditSlot'].includes(act.op)) act.side = side === 'enemy' ? 'enemy' : 'self';

    // 「获得<词条>和+N+N」展开成两个动作（先词条、再身材），共用同一个目标
    if (act.op === 'grantAndBuff') {
      const kw = (KW_CN[act.keyword] || act.keyword || '').toLowerCase();
      const tgt = act.target;
      const acts = [
        { op: 'grant', keyword: /^[a-z]{3,}$/.test(kw) ? kw : act.keyword, target: tgt },
        { op: 'buff', attack: act.attack, defense: act.defense, target: tgt },
      ];
      return { actions: acts, targets: declare.length ? declare : undefined };
    }
    const result = { actions: [act], targets: declare.length ? declare : undefined, actText: actText, sel, side, zone };
    if (cond) {
      return withCondPrefix([{ op: 'conditional', condition: cond, then: [act] }], declare);
    }
    return result;
  };

  /* ★ 牌Q 卡包句式兜底（2026-09-25 导入）：**在最外层包一层**。
   *   为什么不在 parse() 末尾加：parse() 内部有多处"整句放弃"的**提前 return**
   *   （分号段拆不开 / 条件段认不出），那些路径根本走不到末尾 —— 只有包一层才覆盖全部失败路径。
   *   语义：原解析成功就原样返回；失败才交给 paiqRules（因此不可能抢掉任何既有句式）。
   *   递归调用 P.parse 时同样生效（子段失败也能被兜住）。 */
  (function () {
    const inner = P.parse;
    P.parse = function (sentenceRaw, opts) {
      const r = inner(sentenceRaw, opts);
      if (r && r.unparsed) return r;
      if (r && ((r.actions && r.actions.length) || r.cardFields)) return r;
      const txt = String(sentenceRaw == null ? '' : sentenceRaw).trim().replace(/[。；;，,、\s]+$/, '');
      if (txt) {
        try {
          const pr = paiqRules(txt, opts || {}, []);
          if (pr && ((pr.actions && pr.actions.length) || pr.cardFields)) return pr;
        } catch (e) { /* 兜底规则抛错不能影响主解析 */ }
      }
      return r;
    };
  })();

  root.KG_PRIMITIVES = P;
  if (typeof module !== 'undefined' && module.exports) module.exports = P;
})(typeof window !== 'undefined' ? window : globalThis);
