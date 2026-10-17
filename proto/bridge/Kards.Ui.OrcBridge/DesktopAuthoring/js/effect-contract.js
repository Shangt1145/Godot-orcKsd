/* Inspector contract: implementation defines capabilities; card usage never
 * defines legality. Parameter types are editing hints, not guessed rules. */
(function (g) {
  'use strict';
  const types = {
    target: 'selector', spec: 'selector', with: 'selector', filter: 'filter',
    condition: 'condition', actions: 'actionList', then: 'actionList', else: 'actionList',
    effects: 'actionList', forEach: 'actionList', options: 'choiceList', items: 'json', item: 'condition',
    amount: 'numberExpr', count: 'numberExpr', times: 'numberExpr', attack: 'numberExpr',
    defense: 'numberExpr', left: 'numberExpr', right: 'numberExpr',
    chance: 'numberExpr', once: 'boolean', oncePerTurn: 'boolean', optional: 'boolean',
  };
  function parameters(fn, name) {
    const source = Function.prototype.toString.call(fn).replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
    const out = {};
    for (const m of source.matchAll(new RegExp('\\b' + name + '\\.([A-Za-z_$][\\w$]*)', 'g'))) {
      if (m[1] !== 'op') out[m[1]] = types[m[1]] ? { type: types[m[1]] } : {};
    }
    for (const m of source.matchAll(new RegExp('\\b' + name + '\\[\\s*[\'\"]([A-Za-z_$][\\w$]*)[\'\"]\\s*\\]', 'g'))) out[m[1]] = {};
    return out;
  }
  function build(observed, engine, docs) {
    observed = observed || {}; engine = engine || {}; docs = docs || {};
    const out = { ...observed, ops: {}, conditions: {}, source: 'engine', complete: !!(engine.OPS && engine.CONDS) };
    for (const [table, runtime, kind, arg] of [['ops', engine.OPS, 'op', 'a'], ['conditions', engine.CONDS, 'cond', 'c']]) {
      const old = observed[table] || {}, descriptions = docs[kind] || {};
      const keys = runtime ? Object.keys(runtime) : Object.keys(old);
      for (const id of keys) {
        const legacy = old[id] || {}, doc = descriptions[id] || {};
        const extracted = runtime ? parameters(runtime[id], arg) : {};
        const sources=[legacy.params||{},doc.params||{},extracted,table==='ops'&&(engine.BASIC_PARAMETERS||{})[id]||{}],hints={};
        for(const source of sources)for(const [key,hint] of Object.entries(source))hints[key]={...hints[key],...hint};
        out[table][id] = { ...legacy, label: doc.name || legacy.label, params: hints,
          // Runtime identity can confirm aliases even without a generated schema.
          aliasOf: runtime && legacy.aliasOf && runtime[id] !== runtime[legacy.aliasOf] ? null : legacy.aliasOf || null };
      }
      // Delegating handlers inherit the called primitive's parameter hints.
      if (runtime) for (let round = 0; round < 3; round++) for (const id of keys) {
        const source = runtime[id].toString();
        for (const m of source.matchAll(/\b(?:OPS|CONDS)\.([\w$]+)\s*\(/g)) {
          if (out[table][m[1]]) out[table][id].params = { ...out[table][m[1]].params, ...out[table][id].params };
        }
      }
    }
    return out;
  }
  // The editor follows the executable vocabulary, including legacy aliases.
  // Inferred fields are optional: opening an old card must never add defaults.
  function editorCatalog(schema, engine, labels) {
    labels=labels||{};const catalog=engine.composition?.catalog||{};
    const infer=(name,hint={})=>{
      let type=hint.type||types[name];
      type=({selector:'selector',numberExpr:'value',number:'value',actionList:'actions',choiceList:'options',object:'data',json:'data',string:'text',enum:'data'})[type]||type;
      if(name==='code'||name==='script')type='code';
      if(!type||type==='data'){
        if(['value','amount','cost','opCost','opCostMod','total','turns','every','handSize','deckSize','maxCost','minCost','max','min','requireKredits','costMod','costTo'].includes(name))type='value';
        else if(['random','adjacent','byCost','reset','self','thenSummon','allowToken','killAtTurnEnd','choose','oddOnly','loseBlitz','trueCopy','halve','double','negate','untilTurnEnd','excludeSelf','optional','_quotedName'].includes(name))type='boolean';
        else if(name==='side')type='legacySide';
        else if(name==='cardId')type='card';
        else if(name==='keyword'||name==='kw')type='keyword';
        else if(name==='cmp')type='comparison';
        else if(name==='effectTrigger'||name==='trigger')type='trigger';
        else if(name==='effect')type='effect';
        else if(['name','as','ref','prompt','text','code','script','from','to','zone','unitType','type','cardType','set','rarity','mode','duration','piles','mod','cond','stat','of','holder','pick','sel','range'].includes(name))type='text';
        else if(name==='test')type='predicate';
        else type='data';
      }
      const initial=type==='boolean'?false:type==='value'?1:type==='actions'||type==='options'?[]:type==='condition'?{op:'and',items:[]}:type==='predicate'?{test:'all',items:[]}:type==='effect'?{trigger:'deploy',actions:[]}:type==='selector'?'self':type==='filter'||type==='data'?{}:type==='legacySide'?'self':type==='comparison'?'>=':type==='keyword'?'blitz':type==='trigger'?'deploy':'';
      return {name,type,initial,optional:true,desc:hint.desc};
    };
    const make=(table,kind)=>Object.fromEntries(Object.entries(schema[table]||{}).map(([id,d])=>{
      const base=table==='ops'?catalog[id]:null,fields=(base?.fields||[]).map(f=>({...f}));
      for(const [name,hint] of Object.entries(d.params||{}))if(!fields.some(f=>f.name===name))fields.push(infer(name,hint));
      if(table==='ops'&&id==='activate'){const f=fields.find(f=>f.name==='effects');if(f){f.type='effectList';f.initial=[];}}
      return [id,{...base,name:base?.name||d.label||labels[kind]?.[id]||id,group:base?.group||'全部 DSL 动作',fields}];
    }));
    return {ops:make('ops','op'),conditions:make('conditions','cond'),infer};
  }
  const contextRefs = new Set(['self', 'source', 'eventUnit', 'event', 'dead', 'eventVictim', 'victim',
    'eventOrSelf', 'defender', 'attackTarget', 'attacker', 'summoned', 'lastSummoned', 'target', 'eventCard', 'remembered', 'candidate', 'current', 'friendlyPlayer', 'enemyPlayer', 'state']);
  function validate(dsl, schema, opts) {
    opts = opts || {}; schema = schema || {};
    const engine = opts.engine || (g.KG && g.KG.effects) || {};
    const out = { errors: [], warnings: [], infos: [] };
    const push = (level, path, msg) => out[level].push({ path: path.join(' > '), msg });
    const object = v => !!v && typeof v === 'object' && !Array.isArray(v);
    const known = (kind, id) => {
      const runtime = kind === 'ops' ? engine.OPS : engine.CONDS;
      if (runtime) return Object.prototype.hasOwnProperty.call(runtime, id);
      const table = schema[kind] || {};
      return schema.source === 'engine' && schema.complete
        ? Object.prototype.hasOwnProperty.call(table, id) : null;
    };
    const reference = (v, path, scope) => {
      const ref = typeof v === 'string' ? v : object(v) && (v.sel === 'ref' || v.sel === 'refAll') ? v.ref : null;
      if (ref && !scope.has(ref) && !contextRefs.has(ref)) push('warnings', path, '引用“' + ref + '”未在本效果前面声明目标或变量。');
    };
    const badName=x=>typeof x!=='string'||!x||['__proto__','prototype','constructor'].includes(x);
    function expression(x,p,scope) {
      if(!object(x)||!x.expr)return;
      const kinds=['literal','read','variable','memory','query','count','size','math','random','union','intersection'];
      if(!kinds.includes(x.expr)){push('errors',p,'未知取值方式：'+x.expr);return;}
      if(x.expr==='read'){
        reference(x.of,p.concat('of'),scope);
        if(typeof x.path!=='string'||!x.path||x.path.split('.').some(badName))push('errors',p,'需要有效的属性路径。');
      }
      if(x.expr==='variable'){if(badName(x.name))push('errors',p,'变量名称无效。');else reference(x.name,p,scope);}
      if(x.expr==='memory'&&(badName(x.key)||!['unit','player','match'].includes(x.scope||'unit')))push('errors',p,'记忆位置或名称无效。');
      if(x.expr==='query'||x.expr==='count')query(x.query,p.concat('query'),scope);
      if(x.expr==='size')expression(x.value,p.concat('value'),scope);
      if(x.expr==='union'||x.expr==='intersection'){if(!Array.isArray(x.items))push('errors',p,'集合运算需要列表。');else x.items.forEach((v,i)=>expression(v,p.concat('items',i),scope));}
      if(x.expr==='math'){
        if(!['add','subtract','multiply','divide','min','max','floor','ceil'].includes(x.operator))push('errors',p,'请选择算式的运算。');
        if(!Array.isArray(x.items))push('errors',p,'算式需要一组运算项。');else x.items.forEach((v,i)=>expression(v,p.concat('items',i),scope));
      }
      if(x.expr==='random'){expression(x.min,p.concat('min'),scope);expression(x.max,p.concat('max'),scope);}
    }
    function predicate(t,p,scope){
      if(!object(t)){push('errors',p,'判断应是一组设置。');return;}
      if(!t.test){condition(t,p,scope);return;}
      if(t.test==='all'||t.test==='any'){
        if(!Array.isArray(t.items))push('errors',p,'且 / 或需要条件列表。');else t.items.forEach((x,i)=>predicate(x,p.concat(i),scope));
      }else if(t.test==='not')predicate(t.item,p.concat('not'),scope);
      else if(t.test==='compare'||t.test==='exists'){
        if(t.left===undefined)push('errors',p,'判断缺少左值。');expression(t.left,p.concat('left'),scope);
        if(t.test==='compare'){
          if(!['==','!=','>','>=','<','<=','contains','in'].includes(t.cmp))push('errors',p,'请选择有效的比较方式。');
          if(t.right===undefined)push('errors',p,'判断缺少右值。');expression(t.right,p.concat('right'),scope);
        }
      }else push('errors',p,'未知判断方式：'+t.test);
    }
    function query(q,p,scope){
      if(typeof q==='string'){reference(q,p,scope);return;}
      if(!object(q)){push('errors',p,'请选择作用对象。');return;}
      if(!['all','random','first','last','self','ref','refAll','adjacent','union'].includes(q.sel))push('errors',p,'未知选择方式：'+q.sel);
      if(q.sel==='adjacent')reference(q.ref,p.concat('ref'),scope);
      if(q.sel==='union'){if(!Array.isArray(q.queries))push('errors',p,'合并对象需要查询列表。');else q.queries.forEach((v,i)=>query(v,p.concat('queries',i),scope));}
      reference(q,p,scope);
      if(q.zone&&!['field','support','frontline','hand','deck','discard','hq','player','pool'].includes(q.zone))push('errors',p,'未知区域：'+q.zone);
      if(q.where)predicate(q.where,p.concat('where'),scope);
      if(q.filter?.where)predicate(q.filter.where,p.concat('filter','where'),scope);
      if(q.count!=null)expression(q.count,p.concat('count'),scope);
      if(q.cardIds&&(!Array.isArray(q.cardIds)||q.cardIds.some(id=>typeof id!=='string'||!id)))push('errors',p,'指定卡池需要卡牌 ID 列表。');
      if(q.distinctBy&&(typeof q.distinctBy!=='string'||q.distinctBy.split('.').some(badName)))push('errors',p,'去重属性路径无效。');
    }
    function cardPool(pool,p,scope){
      if(!object(pool)||!['pool','cards','deck','hand','discard'].includes(pool.source)){push('errors',p,'请选择候选卡池来源。');return;}
      if(pool.source==='cards'&&(!Array.isArray(pool.cardIds)||!pool.cardIds.length||pool.cardIds.some(id=>typeof id!=='string'||!id)))push('errors',p,'请添加候选卡牌。');
      if(pool.source==='hand'&&pool.side&&pool.side!=='friendly')push('errors',p,'手牌卡池只能使用我方手牌，不能公开敌方隐藏手牌。');
      if(pool.where)predicate(pool.where,p.concat('where'),scope);
      for(const key of ['includeTokens','includeReserved'])if(pool[key]!=null&&typeof pool[key]!=='boolean')push('errors',p.concat(key),'这里需要是 / 否。');
      if(pool.side&&!['friendly','enemy','both'].includes(pool.side))push('errors',p,'候选卡池所属方无效。');
    }
    function condition(c, path, scope) {
      if (!object(c)) { push('errors', path, '条件应是一组设置。'); return; }
      if (typeof c.op !== 'string' || !c.op) push('errors', path, '请选择判断条件。');
      else if (known('conditions', c.op) === false) push('errors', path, '引擎不支持条件：' + c.op);
      if(c.op==='predicate')predicate(c.test,path.concat('test'),scope);
      if (c.op === 'and' || c.op === 'or') {
        if (!Array.isArray(c.items)) push('errors', path.concat('items'), '组合条件应为列表。');
        else c.items.forEach((x, i) => condition(x, path.concat('items', i), scope));
      } else if (c.op === 'not') condition(c.item || (Array.isArray(c.items) && c.items[0]), path.concat('item'), scope);
      if (c.target) reference(c.target, path.concat('target'), scope);
    }
    function actions(list, path, incoming) {
      if (!Array.isArray(list)) { push('errors', path, '动作应为列表。'); return; }
      const scope = new Set(incoming);
      list.forEach((a, i) => {
        const p = path.concat(i);
        if (!object(a)) { push('errors', p, '动作应是一组设置。'); return; }
        if (typeof a.op !== 'string' || !a.op) { if (!a.script) push('errors', p, '请选择要执行的动作。'); }
        else if (known('ops', a.op) === false) push('errors', p, '引擎不支持动作：' + a.op);
        if (a.script || a.op === 'script') push('infos', p, '这段脚本保留原样，检查器无法证明其行为；可逐步改为原语组合。');
        for (const k of ['target', 'spec', 'with']) if (a[k] != null) reference(a[k], p.concat(k), scope);
        if (a.condition != null) condition(a.condition, p.concat('condition'), scope);
        const basic=engine.composition?.catalog[a.op];
        if(basic){
          for(const f of basic.fields){
            if(!Object.prototype.hasOwnProperty.call(a,f.name))continue;
            const fp=p.concat(f.name),x=a[f.name];
            if(f.type==='query')query(x,fp,scope);
            if(f.type==='value')expression(x,fp,scope);
            if(f.type==='card'&&object(x))expression(x,fp,scope);
            if(f.type==='cardPool')cardPool(x,fp,scope);
            if(f.type==='eventData'){
              if(!object(x))push('errors',fp,'事件数据应是一组设置。');else for(const [key,v] of Object.entries(x)){if(badName(key)||['owner','trigger','source','chooser'].includes(key))push('errors',fp,'事件数据不能覆盖所属方或来源。');expression(v,fp.concat(key),scope);}
            }
            if(f.type==='predicate')predicate(x,fp,scope);
            if(f.type==='boolean'&&typeof x!=='boolean')push('errors',fp,'这里需要是 / 否。');
          }
          if(['let','store'].includes(a.op)&&badName(a.name||a.key))push('errors',p,'请填写有效的变量或记忆名称。');
          if(['dealDamage','restoreHealth','destroyUnit','changeAttribute','setKeyword','iterate','setPinned','suppressUnit','moveUnit','returnUnit','transferControl','leaveUnit','transformBody','bindEvent','setModifier'].includes(a.op)&&a.target==null)push('errors',p,'动作缺少作用对象。');
          if(['destroyUnit','setKeyword','setPinned','suppressUnit','moveUnit','returnUnit','leaveUnit','transformBody','setModifier','transferControl'].includes(a.op)&&object(a.target)&&['hand','deck','discard','hq','player'].includes(a.target.zone))push('errors',p.concat('target'),'这个动作需要战场、支援阵线或前线的单位。');
          if(a.op==='bindEvent'&&['deck','discard','pool'].includes(a.target?.zone))push('errors',p,'绑定效果需要场上单位、手牌实例或总部。');
          if(a.op==='bindHQEvent'&&!a.target)push('errors',p,'请选择要附加效果的总部。');
          if(a.op==='bindHQEvent'&&a.target?.zone&&!['hq','player'].includes(a.target.zone))push('errors',p,'总部附加效果需要选择总部。');
          if(a.op==='changeAttribute'&&object(a.target)&&['deck','discard'].includes(a.target.zone))push('errors',p.concat('target'),'属性修改需要场上单位、手牌实例或总部防御力。');
          if(a.op==='changeAttribute'&&['hq','player'].includes(a.target?.zone)&&(a.property!=='defense'||a.mode!=='add'||a.duration==='turn'))push('errors',p,'总部支持永久增减防御力。');
          if(a.op==='changeAttribute'&&a.target?.zone==='hand'&&a.duration==='turn')push('errors',p,'尚不支持手牌属性的临时修改。');
          if(a.op==='drawOne'&&a.as&&badName(a.as))push('errors',p,'抽牌记录名称无效。');
          if(a.op==='createCard'&&a.as&&badName(a.as))push('errors',p,'创建记录名称无效。');
          if(['threeChoice','developCard'].includes(a.op)){if(!a.pool)push('errors',p,'请选择候选卡池。');if(a.as&&badName(a.as))push('errors',p,'结果记录名称无效。');}
          if(a.op==='threeChoice'&&(!['fixed','random'].includes(a.mode)||a.mode==='fixed'&&(a.pool?.source!=='cards'||!Array.isArray(a.pool.cardIds)||new Set(a.pool.cardIds).size>3)))push('errors',p,'固定三选一需要指定最多三张不同的候选卡；随机模式可使用更大的卡池。');
          if(a.op==='emitEvent'&&(!a.trigger||badName(a.trigger)))push('errors',p,'事件名称无效。');
          if(a.op==='moveCard'&&(!a.target||!['deck','hand'].includes(a.to)||!['shuffle','top','bottom','left','right'].includes(a.position)||(a.to==='hand'?!['left','right'].includes(a.position):!['shuffle','top','bottom'].includes(a.position))))push('errors',p,'请选择手牌对象以及对应的卡组或手牌位置。');
          if(a.op==='battleUnits'&&(!a.target||!a.with))push('errors',p,'交战需要双方单位。');
          if(a.op==='changeAttribute'&&!['attack','defense','opCost','cost'].includes(a.property))push('errors',p,'请选择可以修改的属性。');
          if(a.op==='selectObjects'&&(badName(a.as)||!a.as||!a.target))push('errors',p,'请选择候选对象并填写结果名称。');
          if(a.op==='changeAttribute'&&a.property==='cost'&&a.target?.zone==='field')push('errors',p,'花费修改需要手牌实例。');
          if(a.op==='changeResource'&&!['kredits','maxKredits'].includes(a.property))push('errors',p,'请选择资源属性。');
          if(['changeAttribute','changeResource'].includes(a.op)&&!['add','set'].includes(a.mode))push('errors',p,'请选择修改方式。');
          if(['createCard','createUnit','transformBody'].includes(a.op)&&!a.cardId)push('errors',p,'请选择卡牌。');
          if(a.op==='iterate'&&badName(a.as||'current'))push('errors',p,'当前对象名称无效。');
          if(a.op==='setModifier'&&badName(a.property))push('errors',p,'修正名称无效。');
          if(a.op==='bindEvent'&&!a.effect&&(!a.trigger||typeof a.trigger!=='string'))push('errors',p,'请选择绑定效果的时机。');
          if(a.op==='bindHQEvent'&&!a.effect)push('errors',p,'请填写要给总部附加的完整效果。');
          if(basic.fields.some(f=>f.type==='effect')&&a.effect)effect(a.effect,p.concat('effect'),new Set());
          if(a.op==='setKeyword'&&!a.keyword)push('errors',p,'请选择词条。');
          const numerical={dealDamage:'amount',restoreHealth:'amount',loop:'times',setPinned:'turns',changeAttribute:'value',changeResource:'value',setKeyword:'value'}[a.op];
          if(numerical&&a[numerical]!=null&&!object(a[numerical])){
            const n=a[numerical];if(Array.isArray(n)||typeof n==='boolean'||(typeof n==='string'&&!n.trim())||!Number.isFinite(Number(n)))push('errors',p.concat(numerical),'这里需要数值或数值表达式。');
          }
        }
        const childScope = new Set(scope);
        if (a.op === 'forEach') childScope.add(a.as || 'each');
        if(a.op==='iterate')childScope.add(a.as||'current');
        for (const k of ['actions', 'then', 'else', 'forEach']) if (a[k] != null) actions(a[k], p.concat(k), childScope);
        if (a.options != null) {
          if (!Array.isArray(a.options)) push('errors', p.concat('options'), '抉择选项应为列表。');
          else a.options.forEach((o, j) => {
            if (a.op === 'randomPick' && Array.isArray(o)) actions(o, p.concat('options', j), scope);
            else if (!object(o)) push('errors', p.concat('options', j), '选项应是一组设置。');
            else { if (o.condition != null) condition(o.condition, p.concat('options', j, 'condition'), scope);
              if(o.test)predicate(o.test,p.concat('options',j,'test'),scope);
              if(o.targets)effect({trigger:'order',targets:o.targets,actions:o.actions||[]},p.concat('options',j),scope);
              else actions(o.actions == null ? [] : o.actions, p.concat('options', j, 'actions'), scope); }
          });
        }
        if (a.effects != null) {
          if (!Array.isArray(a.effects)) push('errors', p.concat('effects'), '嵌套效果应为列表。');
          else a.effects.forEach((e, j) => e && !e.op && (e.trigger != null || e.actions != null)
            ? effect(e, p.concat('effects', j), scope) : actions([e], p.concat('effects', j), scope));
        }
        if ((a.op === 'asVar' || a.op === 'setVar'||a.op==='let') && typeof a.name === 'string') scope.add(a.name);
        if(['drawOne','selectObjects','createCard','threeChoice','developCard'].includes(a.op)&&typeof a.as==='string')scope.add(a.as);
      });
    }
    function effect(e, p, outer) {
      if (!object(e)) { push('errors', p, '效果应是一组设置。'); return; }
      const scope = new Set(outer), seen = new Set();
      if (object(e.vars)) Object.keys(e.vars).forEach(k => scope.add(k));
      if (e.targets != null) {
        if (!Array.isArray(e.targets)) push('errors', p.concat('targets'), '目标应为列表。');
        else e.targets.forEach((t, j) => {
          const at = p.concat('targets', j);
          if (!object(t) || typeof t.id !== 'string' || !t.id) push('errors', at, '每个目标需要一个名称。');
          else { if (seen.has(t.id)) push('errors', at, '目标名称重复：' + t.id); seen.add(t.id); scope.add(t.id); }
          if(t?.filter?.where)predicate(t.filter.where,at.concat('filter','where'),scope);
        });
      }
      if (e.trigger == null) push('warnings', p, '未选择触发时机，请确认这是供其它效果调用的子效果。');
      else if (typeof e.trigger !== 'string') push('errors', p.concat('trigger'), '触发时机应为名称。');
      if(typeof e.trigger==='string'&&e.trigger.startsWith('behavior:')) {
        const timing=/^behavior:(op|game)\/([^:]+):(before|during|after)$/.exec(e.trigger);
        if(!timing)push('errors',p.concat('trigger'),'行为时机需要有效的行为和发生阶段。');
        else if(timing[1]==='op'&&known('ops',timing[2])===false||timing?.[1]==='game'&&!g.KG?.BEHAVIOR_ACTIONS?.[timing[2]])push('errors',p.concat('trigger'),'这个行为尚未在引擎中注册。');
      }
      if(e.eventSide!=null&&!['friendly','enemy','any'].includes(e.eventSide))push('errors',p.concat('eventSide'),'请选择友方、敌方或双方的行为。');
      if(e.listenZone!=null&&!['auto','hand','field','any'].includes(e.listenZone))push('errors',p.concat('listenZone'),'请选择携带者在手牌、战场或两者中的生效区域。');
      if (e.condition != null) condition(e.condition, p.concat('condition'), scope);
      for (const k of ['actions', 'then', 'else']) if (e[k] != null) actions(e[k], p.concat(k), scope);
      for (const k of ['aura', 'passiveRules', 'vars', 'cardFields']) if (e[k] != null && !object(e[k])) push('errors', p.concat(k), '这里应是一组设置。');
      if (e.then) push('warnings', p.concat('then'), '效果根级的 then 不作为普通动作执行；请将动作放在 actions 中，或使用条件流程。');
      if (e.aura && e.trigger !== 'passive') push('warnings', p.concat('aura'), '光环需要“常驻 / 光环”触发时机，当前不会生效。');
      if (e.trigger === 'passive' && Array.isArray(e.actions) && engine.PASSIVE_CONT_OPS) {
        const continuous=(list,path)=>{(list||[]).forEach((a,j)=>{if(!a)return;const at=path.concat(j);if(!engine.PASSIVE_CONT_OPS[a.op]&&!engine.composition?.continuousOps[a.op])push('warnings',at,'这个动作不会持续执行，请选择事件触发。');for(const k of ['actions','then','else'])if(Array.isArray(a[k]))continuous(a[k],at.concat(k));});};
        continuous(e.actions,p.concat('actions'));
      }
      if (e.unimplemented) push('infos', p, '旧解析记录提示未完整实现，请结合实际组合检查。');
    }
    if (!Array.isArray(dsl)) push('errors', [], '效果应为列表。');
    else dsl.forEach((e, i) => effect(e, [i], new Set()));
    if (!engine.OPS && !(schema.source === 'engine' && schema.complete)) push('infos', [], '未加载引擎能力表，仅检查结构，不判断动作是否受支持。');
    return out;
  }
  const api = { build, validate, parameters, types, editorCatalog };
  g.KG_EFFECT_CONTRACT = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
