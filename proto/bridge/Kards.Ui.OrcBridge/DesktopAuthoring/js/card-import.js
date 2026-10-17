/* Shared, deterministic batch import planning. No storage or pool mutations. */
(function(g){
  'use strict';
  const K=g.KG,copy=x=>JSON.parse(JSON.stringify(x));
  function csv(text){
    const rows=[];let row=[],cell='',quoted=false,closed=false;
    text=String(text).replace(/^\uFEFF/,'').replace(/\r\n?/g,'\n');
    for(let i=0;i<text.length;i++){const c=text[i];
      if(quoted){if(c==='"'){if(text[i+1]==='"'){cell+='"';i++;}else{quoted=false;closed=true;}}else cell+=c;continue;}
      if(c==='"'){if(cell.length||closed)throw Error('CSV 引号位置错误');quoted=true;}
      else if(c===','||c==='\n'){row.push(cell);cell='';closed=false;if(c==='\n'){if(row.some(Boolean))rows.push(row);row=[];}}
      else {if(closed&&!/\s/.test(c))throw Error('CSV 引号后存在多余字符');if(!closed)cell+=c;}
    }
    if(quoted)throw Error('CSV 引号未闭合');row.push(cell);if(row.some(Boolean))rows.push(row);
    if(!rows.length)return [];
    const map={'卡名':'name','名称':'name','费用':'cost','攻击力':'attack','防御力':'defense','操作费':'opCost','行动花费':'opCost','类型':'cardType','兵种':'unitType','词条':'keywords','效果':'text','卡面文本':'text','国家':'nation','稀有度':'rarity','卡图':'file'};
    const known=['id','name','cost','attack','defense','opCost','cardType','unitType','keywords','text','nation','set','rarity','effects','token','file','art','system','kwMap','kwValues'];
    let headers=rows[0].map(s=>map[s.trim()]||known.find(k=>k.toLowerCase()===s.trim().toLowerCase())||s.trim());
    if(headers.includes('name'))rows.shift();else headers=['name','cost','attack','defense','cardType','unitType','keywords','text'];
    if(new Set(headers).size!==headers.length)throw Error('CSV 表头重复');
    return rows.map((r,i)=>{if(r.length!==headers.length)throw Error('CSV 第 '+(i+2)+' 行列数不一致（包含逗号或换行的内容应加双引号）');return Object.fromEntries(headers.map((k,j)=>[k,r[j]]));});
  }
  function parse(text,filename='cards.json'){
    text=String(text).replace(/^\uFEFF/,'').trim();
    if(/\.json$/i.test(filename)||/^[\[{]/.test(text)){
      const data=JSON.parse(text),cards=Array.isArray(data)?data:Array.isArray(data.cards)?data.cards:data.name?[data]:null;
      if(!cards)throw Error('JSON 需要卡牌数组、{cards:[...]} 或单张卡牌对象');
      return {cards,images:!Array.isArray(data)&&data.images||{}};
    }
    return {cards:csv(text),images:{}};
  }
  const hash=s=>{let n=2166136261;for(const c of s){n^=c.charCodeAt(0);n=Math.imul(n,16777619);}return (n>>>0).toString(16).padStart(8,'0');};
  function fromOfficial(node){
    const raw=node.json;
    if(!raw||typeof raw!=='object'||!raw.title||!raw.type)throw Error('官方卡牌记录缺少 json/title/type');
    const cn={'Britain':'英国','Germany':'德国','Soviet':'苏联','SovietUnion':'苏联','USA':'美国','Japan':'日本','France':'法国','Italy':'意大利','Poland':'波兰','Finland':'芬兰','Anzac':'澳新军团','Neutral':'中立'};
    const unit=['infantry','tank','artillery','fighter','bomber'].includes(raw.type),prefix='kards/';
    const c={id:prefix+raw.id,name:raw.title['zh-Hans'],text:raw.text?.['zh-Hans']||'',cardType:unit?'unit':raw.type==='countermeasure'?'counter':raw.type,
      unitType:unit?raw.type:null,cost:raw.kredits,attack:raw.attack??null,defense:raw.defense??null,opCost:raw.operationCost??null,
      nation:cn[raw.faction]||raw.faction,set:cn[raw.faction]||raw.faction,expansion:raw.set,
      rarity:({Standard:'iron',Limited:'bronze',Special:'silver',Elite:'gold'})[raw.rarity]||raw.rarity,
      token:(raw.attributes||[]).includes('OnlySpawnable'),reserved:!!node.reserved,
      kwMap:{},kwValues:{},keywords:[],importDiagnostics:[],
      official:{cardId:raw.id,recordId:node.id,importId:node.importId,attributes:raw.attributes||[],canCreate:raw.can_create||[],exile:raw.exile||null,raw}};
    if(raw.text&&Object.keys(raw.text).length&&!Object.hasOwn(raw.text,'zh-Hans'))c.importDiagnostics.push('官方记录缺少简体中文效果文本，未使用空效果替代。');
    if(raw.exile){c.kwMap.exile=true;c.exileNation=cn[raw.exile]||raw.exile;}
    for(const a of raw.attributes||[]){let id=({shock:'impact',covert:'conceal',bond:'synergy'})[a]||a,value=1;
      if(a==='OnlySpawnable')continue;
      if(a.startsWith('BecomesVeteran:')){c.upgradeTo=prefix+a.slice(15);continue;}
      if(a.startsWith('VeteranOf:')){c.kwMap.veteran=true;c.baseForm=prefix+a.slice(10);c.token=true;continue;}
      const m=a.match(/^(heavyArmor|intel)(\d+)$/);if(m){id=m[1]==='heavyArmor'?'armor':'intel';value=Number(m[2]);}
      if(!K.KEYWORDS[id]){c.importDiagnostics.push('官方词条尚未实现：'+a);continue;}
      c.kwMap[id]=true;c.kwValues[id]=value;
      if(a==='bond'&&!unit)c.importDiagnostics.push('协力的非单位打出结算尚未实现，需核对。');
    }
    c.keywords=Object.keys(c.kwMap);return c;
  }
  function normalize(raw){
    if(!raw||typeof raw!=='object'||Array.isArray(raw))throw Error('卡牌必须是对象');
    if(raw.json&&raw.cardId)raw=fromOfficial(raw);
    const c=copy(raw);c.name=String(c.name||'').trim();if(!c.name)throw Error('缺少卡名');
    if(c.importDiagnostics!=null&&(!Array.isArray(c.importDiagnostics)||c.importDiagnostics.some(x=>typeof x!=='string')))throw Error('importDiagnostics 需要文字列表');
    c.id=String(c.id||'custom/orc/'+encodeURIComponent(String(c.nation||c.set||'custom'))+'/'+encodeURIComponent(c.name));
    if(['__proto__','prototype','constructor'].includes(c.id))throw Error('卡牌 ID 无效');
    c.cardType=({'单位':'unit','指令':'order','反制':'counter'})[c.cardType||c.type]||c.cardType||c.type||'unit';
    if(!['unit','order','counter'].includes(c.cardType))throw Error('未知卡牌类型：'+c.cardType);
    c.unitType=({'步兵':'infantry','坦克':'tank','炮兵':'artillery','战斗机':'fighter','轰炸机':'bomber','巡航舰':'cruiser','巡地舰':'landcruiser','太空战机':'spacefighter','建筑':'structure'})[c.unitType]||c.unitType||null;
    for(const field of ['cost','attack','defense','opCost']){const v=c[field];if(v===''||v==null){c[field]=null;continue;}const n=Number(v);if(!Number.isInteger(n)||n<0||typeof v==='boolean')throw Error(field+' 需要非负整数');c[field]=n;}
    if(c.cost==null)c.cost=1;
    if(c.cardType==='unit'){if(c.attack==null)c.attack=1;if(c.defense==null)c.defense=1;}
    if(typeof c.effects==='string')c.effects=c.effects.trim()?JSON.parse(c.effects):[];
    if(c.effects!=null&&!Array.isArray(c.effects))throw Error('effects 需要数组');
    for(const key of ['kwMap','kwValues'])if(typeof c[key]==='string')c[key]=c[key].trim()?JSON.parse(c[key]):{};
    if(c.kwMap!=null&&(typeof c.kwMap!=='object'||Array.isArray(c.kwMap)))throw Error('kwMap 需要对象');
    if(c.kwValues!=null&&(typeof c.kwValues!=='object'||Array.isArray(c.kwValues)))throw Error('kwValues 需要对象');
    if(typeof c.token==='string'){if(!/^(true|false|0|1)?$/i.test(c.token))throw Error('token 需要 true 或 false');c.token=/^(true|1)$/i.test(c.token);}
    const kws=Array.isArray(c.keywords)?c.keywords:String(c.keywords||'').split(/[|;；、,，]/).map(x=>x.trim()).filter(Boolean);
    c.kwMap={...c.kwMap};c.kwValues={...c.kwValues};c.keywords=[];
    for(const word of kws){const kw=K.OrC.keyword(String(word));if(!kw)throw Error('未知词条：'+word);c.kwMap[kw.id]=true;if(kw.quantified)c.kwValues[kw.id]=kw.value;if(!c.keywords.includes(kw.id))c.keywords.push(kw.id);}
    c.text=String(c.text||'');c.set=c.set||c.nation||'自定义';c.custom=true;c.art=c.art||'';return c;
  }
  function plan(records,options={}){
    const ready=[],pending=[],rejected=[],skipped=[],entries=[],normalized=[],existing=options.existing||[],pool=options.pool||Object.values(K.pool||{});
    for(let i=0;i<records.length;i++){try{normalized.push({card:normalize(records[i]),row:i+1});}catch(e){rejected.push({row:i+1,name:records[i]?.name||'',messages:[e.message],raw:records[i]});}}
    const counts=new Map();for(const {card} of normalized)counts.set(card.id,(counts.get(card.id)||0)+1);
    const registry=[...pool.filter(c=>!existing.some(x=>x.id===c.id)),...existing,...normalized.filter(x=>counts.get(x.card.id)===1&&!pool.some(c=>c.id===x.card.id)&&!existing.some(c=>c.id===x.card.id)).map(x=>x.card)];
    const validIds=new Set(registry.map(c=>c.id));
    for(const {card:c,row} of normalized){
      if(counts.get(c.id)>1){rejected.push({row,name:c.name,id:c.id,messages:['本批次卡牌 ID 重复，未任意覆盖。'],raw:c});continue;}
      if(existing.some(x=>x.id===c.id)||pool.some(x=>x.id===c.id)){skipped.push({row,name:c.name,id:c.id,messages:['已存在同 ID 卡牌，保留已有数据与手工效果。']});continue;}
      const manual=c.effects?.length>0,report=manual?{effects:c.effects,complete:true,warnings:[],cardFields:{},kwMap:{},kwValues:{},keywords:[],legacyOps:[]}:K.OrC.compile(c.text,c,{pool:registry,atomicOnly:options.atomicOnly});
      if(manual&&g.KG_EFFECT_CONTRACT){
        const validation=g.KG_EFFECT_CONTRACT.validate(c.effects,g.KG_EFFECT_CONTRACT.build({},K.effects,{}));report.warnings=[...validation.errors,...validation.warnings].map(d=>d.path+'：'+d.msg);
        if(c.effects.some(e=>e?.unimplemented))report.warnings.push('提供的效果标记为未完整实现。');
        K.OrC.walk(c.effects.flatMap(e=>e?.actions||[]),a=>{
          if(typeof a.cardId==='string'&&!validIds.has(a.cardId))report.warnings.push('缺少卡牌 ID：'+a.cardId);
          for(const ids of [a.pool?.cardIds,a.target?.cardIds])if(Array.isArray(ids))for(const id of ids)if(!validIds.has(id))report.warnings.push('缺少卡牌 ID：'+id);
          if(a.unimplemented||(a.op==='log'&&/^未实现[：:]/.test(a.text||'')))report.warnings.push(a.text||'子效果未完整实现。');
          if(a.name&&!a.cardId&&!a.filter&&['summon','addCardToHand','shuffleIn','deckToField'].includes(a.op)){
            const matches=registry.filter(c=>c.name===a.name);if(matches.length===1)a.cardId=matches[0].id;else report.warnings.push('无法唯一确定引用的卡牌：'+a.name);
          }
        });report.complete=!report.warnings.length;
      }
      report.warnings.push(...(c.importDiagnostics||[]));
      for(const id of [c.upgradeTo,c.baseForm].filter(Boolean))if(!validIds.has(id))report.warnings.push('缺少关联形态：'+id);
      report.complete=report.complete&&!report.warnings.length;
      report.warnings=[...new Set(report.warnings)];
      Object.assign(c,report.cardFields);Object.assign(c.kwMap,report.kwMap);Object.assign(c.kwValues,report.kwValues);c.keywords=[...new Set([...c.keywords,...report.keywords])];
      c.effectStatus=report.complete?(manual?'manual-confirmed':'auto-confirmed'):'needs-review';c.compileWarnings=report.warnings;c.effects=report.complete?report.effects:[];
      c.orc={version:K.OrC.version,sourceHash:hash(c.text),legacyOps:report.legacyOps||[],origin:manual?'provided-effects':'text'};
      const entry={row,id:c.id,name:c.name,messages:report.warnings,legacyOps:report.legacyOps||[],card:c};
      if(report.complete)ready.push(c);else{entry.draftEffects=report.effects;pending.push(entry);}entries.push(entry);
    }
    // A card that relies on a rejected or pending card cannot become executable alone.
    let changed=true;while(changed){changed=false;const unavailable=new Set([...rejected,...pending].map(x=>x.id).filter(Boolean));
      for(let i=ready.length-1;i>=0;i--){const c=ready[i],deps=[c.upgradeTo,c.baseForm].filter(id=>id&&unavailable.has(id));K.OrC.walk(c.effects.flatMap(e=>e.actions||[]),a=>{if(a.cardId&&unavailable.has(a.cardId))deps.push(a.cardId);for(const ids of [a.pool?.cardIds,a.target?.cardIds])if(Array.isArray(ids))for(const id of ids)if(unavailable.has(id))deps.push(id);});if(deps.length){const e=entries.find(x=>x.id===c.id);e.messages=c.compileWarnings=['依赖的卡牌尚未通过解析：'+[...new Set(deps)].join('、')];e.draftEffects=c.effects;c.effects=[];c.effectStatus='needs-review';ready.splice(i,1);pending.push(e);changed=true;}}
    }
    return {version:K.OrC.version,ready,pending,rejected,skipped,entries,summary:{ready:ready.length,pending:pending.length,rejected:rejected.length,skipped:skipped.length}};
  }
  K.cardImport={parse,csv,normalize,plan,fromOfficial};
})(typeof window!=='undefined'?window:globalThis);
