/* Primitive editor. Text input never rebuilds its control; confirmations stay in the page. */
(function(g){
  'use strict';
  const clone=x=>JSON.parse(JSON.stringify(x));
  const labels={target:'作用对象',amount:'数值',value:'取值',property:'属性',mode:'修改方式',name:'变量名',as:'当前对象名称',times:'重复次数',scope:'记忆位置',key:'记忆名称',keyword:'词条',enabled:'启用',turns:'持续回合',trigger:'触发时机',duration:'持续时间',cardId:'卡牌',side:'归属',to:'放入位置',kind:'天气',prompt:'选择提示',actions:'执行动作',then:'成立时',else:'不成立时',test:'判断条件',options:'可选分支',effect:'授予的完整效果'};
  const enums={side:[['self','我方'],['enemy','敌方']],scope:[['unit','来源单位或总部'],['player','我方玩家'],['match','本局']],attribute:[['attack','攻击力'],['defense','防御力'],['opCost','行动费用']],resource:[['kredits','当前指挥点'],['maxKredits','指挥点槽']],mode:[['add','增加或减少'],['set','设为']],duration:[['permanent','永久'],['turn','本回合']],position:[['support','支援阵线'],['frontline','前线']],weather:[['clear','晴朗'],['mist','薄雾'],['gale','狂风'],['snow','落雪']]};
  const properties={attack:'当前攻击力',defense:'当前防御力',maxDefense:'防御力上限',missingDefense:'损失的防御力',cost:'费用',opCost:'行动费用',unitType:'兵种',cardType:'卡牌种类',set:'国家 / 系列',system:'体系',rarity:'稀有度',keywords:'当前词条列表',owner:'所有者',zone:'所在区域',alive:'存活',damaged:'已受伤',pinned:'被压制',canAct:'可以行动',actionsLeft:'剩余行动次数',revealed:'已公开',silenced:'已被抑制',chargeIn:'充能剩余回合','printed.attack':'卡面攻击力','printed.defense':'卡面防御力','printed.keywords':'卡面词条','mods.immuneOrders':'无视指令',hq:'总部生命',hqMax:'总部生命上限',kredits:'当前指挥点',maxKredits:'指挥点槽'};
  enums.attribute.push(['cost','手牌花费']);enums.cardZone=[['deck','卡组'],['hand','手牌']];enums.cardPosition=[['shuffle','洗入卡组'],['top','卡组顶'],['bottom','卡组底'],['left','手牌最左侧'],['right','手牌最右侧']];
  labels.effects='授予的子效果';labels.once='触发后移除';labels.untilTurnEnd='临时附加';labels.pool='候选卡池';labels.asCopies='生成副本';labels.data='事件数据';
  enums.offerMode=[['fixed','指定候选牌'],['random','从卡池随机取三张']];
  const examples=[
    {id:'draw',name:'部署时抽两张牌',hint:'可以把「2」改成想抽的张数。',effect:{trigger:'deploy',actions:[{op:'loop',times:2,actions:[{op:'drawOne',side:'self'}]}]}},
    {id:'buff',name:'部署时让自己 +1/+1',hint:'分别修改攻击力和防御力，数值可自行调整。',effect:{trigger:'deploy',actions:[{op:'changeAttribute',target:{sel:'self'},property:'attack',mode:'add',value:1,duration:'permanent'},{op:'changeAttribute',target:{sel:'self'},property:'defense',mode:'add',value:1,duration:'permanent'}]}},
    {id:'damage',name:'选一个敌方单位，造成 2 点伤害',hint:'打出卡牌前选目标。单位在部署时生效，指令在使用时生效。',effect:{trigger:'order',targets:[{id:'chosenEnemy',side:'enemy',kind:'unit',prompt:'选择一个敌方单位'}],actions:[{op:'dealDamage',target:'chosenEnemy',amount:2}]}},
    {id:'heal',name:'部署时让己方总部恢复 3 点生命',hint:'可以修改恢复的生命值。',effect:{trigger:'deploy',actions:[{op:'restoreHealth',target:{sel:'all',side:'friendly',zone:'hq'},amount:3}]}}
  ];
  const actionNames={let:'记住一个数或对象',store:'跨回合记住数据',sequence:'按顺序执行',branch:'如果……就……否则……',loop:'重复做几次',iterate:'对每个对象分别做',choice:'让玩家选择一项',selectObjects:'选出卡牌并记住',emitEvent:'通知其他效果',changeAttribute:'修改攻击力、防御力或费用',setKeyword:'获得或失去一个词条',setPinned:'压制或解除压制',suppressUnit:'抑制一个单位',createUnit:'让一个单位加入战场',createCard:'把一张牌加入手牌',bindEvent:'给单位、手牌或总部增加能力',bindHQEvent:'给总部附加效果',setModifier:'修改特殊规则',changeResource:'增加、减少或设置指挥点'};
  function mount(host,api,Core){
    const K=g.KG,P=K.effects.composition;
    const contract=g.KG_EFFECT_CONTRACT.build(api.schema(),K.effects,g.KG_OP_DOCS);
    const vocabulary=g.KG_EFFECT_CONTRACT.editorCatalog(contract,K.effects,g.KG_EFFECT_LABELS),C=vocabulary.ops;
    const seed=op=>Object.assign({op},Object.fromEntries(C[op].fields.filter(f=>!f.optional).map(f=>[f.name,clone(f.initial)])));
    const root=document.createElement('div');root.className='ins-root';host.appendChild(root);
    root.innerHTML='<div class="ins-toolbar"><button id="insBtnCards">卡牌</button><input id="insSearch" placeholder="搜索卡名、国家、兵种"><button id="insBtnNew">新建卡</button><span id="insHint" role="status"></span></div><div class="ins-body"><aside class="ins-list" id="insList"></aside><main class="ins-main" id="insTree"></main></div><footer><span id="insVal" role="status"></span><div class="ins-tools"><button id="insBtnUndo" title="撤销">↶</button><button id="insBtnRedo" title="重做">↷</button><button id="insBtnRaw">原始数据</button><button id="insBtnConfirm">标记已验证</button><button id="insBtnSave" class="primary">保存修改</button></div></footer><section id="insRawPanel" hidden><button id="insRawClose">关闭原始数据</button><textarea id="insRaw" spellcheck="false"></textarea></section>';
    const $=id=>root.querySelector('#'+id);
    $('insBtnRaw').textContent='查看原始数据';$('insBtnConfirm').textContent='我已实测，标记通过';
    const more=document.createElement('details');more.className='ins-more-tools';
    const moreTitle=document.createElement('summary');moreTitle.textContent='更多工具';more.append(moreTitle,$('insBtnRaw'),$('insBtnConfirm'));root.querySelector('.ins-tools').insertBefore(more,$('insBtnSave'));
    const modeButton=document.createElement('button');modeButton.id='insBtnMode';modeButton.type='button';modeButton.textContent='显示进阶设置';modeButton.setAttribute('aria-pressed','false');root.querySelector('.ins-toolbar').insertBefore(modeButton,$('insHint'));
    let card=null,fx=[],patch={},dirty=false,rawInvalid=false,historyTimer,checkTimer,pendingDialog=null,simple=true;
    const invalidJSON=new Set();
    const history=new Core.History(80),snapshot=()=>JSON.stringify({fx,patch});
    const message=t=>{$('insHint').textContent=t;};
    const schema=()=>g.KG_EFFECT_CONTRACT.build(api.schema(),K.effects,g.KG_OP_DOCS);
    const node=(tag,cls,text)=>{const el=document.createElement(tag);if(cls)el.className=cls;if(text!=null)el.textContent=text;return el;};
    const button=(text,fn,cls)=>{const b=node('button',cls,text);b.type='button';b.onclick=()=>{if(invalidJSON.size&&!/保存|原始数据|继续编辑/.test(text)){message('请先修正未完成的数据。');return;}return fn();};return b;};
    const modeLabel=()=>{modeButton.textContent=simple?'显示进阶设置':'收起进阶设置';modeButton.setAttribute('aria-pressed',String(!simple));};
    modeButton.onclick=()=>{if(invalidJSON.size){message('请先填写完整当前数据，再切换显示。');return;}simple=!simple;modeLabel();render();};
    const row=(title,control)=>{const r=node('div','ins-field');control.setAttribute('aria-label',title);r.append(node('span','ins-label',title),control);return r;};
    function timingControls(effect,key,title='什么时候生效') {
      const box=node('div','ins-timing'),timing=K.parseBehaviorTrigger(effect.trigger);
      const choices=Object.entries(Core.TRIGGERS).concat([['behavior','任意行为的发生前 / 时 / 后']]);
      box.append(row(title,select(timing?'behavior':effect.trigger,choices,x=>{
        effect.trigger=x==='behavior'?'behavior:op/drawOne:during':x;
      },key+'.trigger')));
      if(!timing)return box;
      const make=(kind,action,phase)=>'behavior:'+kind+'/'+action+':'+phase;
      const actions=timing.kind==='game'?Object.entries(K.BEHAVIOR_ACTIONS):Object.keys(K.effects.OPS).map(id=>[
        id,id==='draw'?'抽牌（所有抽牌）':P.catalog[id]?.name||g.KG_OP_DOCS?.op?.[id]?.name||Core.SEED_OP[id]||id]);
      box.append(row('行为类别',select(timing.kind,[['game','对局操作'],['op','效果动作']],x=>{
        effect.trigger=make(x,x==='game'?'playCard':'drawOne',timing.phase);
      },key+'.behaviorKind')),
      row('具体行为',select(timing.action,actions,(x)=>effect.trigger=make(timing.kind,x,timing.phase),key+'.behaviorAction')),
      row('发生阶段',select(timing.phase,Object.entries(K.BEHAVIOR_PHASES),x=>effect.trigger=make(timing.kind,timing.action,x),key+'.behaviorPhase')),
      row('谁的行为',select(effect.eventSide||(effect.watchOpponent?'enemy':effect.anyOwner?'any':'friendly'),[['friendly','友方'],['enemy','敌方'],['any','双方']],x=>{
        effect.eventSide=x;delete effect.watchOpponent;delete effect.anyOwner;delete effect.forOpponent;
      },key+'.eventSide')),
      row('行为来源',select(effect.selfOnly?'self':'any',[['any','任意来源'],['self','仅携带者']],x=>effect.selfOnly=x==='self',key+'.selfOnly')),
      row('携带者所在区域',select(effect.listenZone||'auto',[['auto','自动（自身减费在手牌，其它在战场）'],['hand','手牌中'],['field','战场上'],['any','手牌和战场']],x=>{if(x==='auto')delete effect.listenZone;else effect.listenZone=x;},key+'.listenZone')));
      box.append(node('p','muted','发生前：准备执行；发生时：确认开始、结果结算前；发生后：该行为结算完成。每个阶段的效果结算完才继续。'));
      return box;
    }
    function persistDraft(){
      if(!card||!dirty)return;
      try {localStorage.setItem('kg.inspectorDraft',JSON.stringify({cardId:card.id,fx,patch,raw:rawInvalid?$('insRaw').value:null}));}
      catch(e){message('草稿备份失败：'+e.message+'，请及时保存。');}
    }
    function check(){
      const report=rawInvalid?{errors:[{msg:'原始数据还不是有效 JSON'}],warnings:[]}:Core.validate(fx,schema());
      const knownIds=new Set(api.list().map(c=>c.id));const inspect=x=>{if(!x||typeof x!=='object')return;for(const ids of [x.pool?.cardIds,x.target?.cardIds])if(Array.isArray(ids))for(const id of ids)if(!knownIds.has(id))report.errors.push({msg:'候选卡池缺少卡牌：'+id});Object.values(x).forEach(v=>{if(Array.isArray(v))v.forEach(inspect);else if(v&&typeof v==='object')inspect(v);});};inspect(fx);
      const all=[...report.errors,...report.warnings];
      $('insVal').textContent=all.length?all.length+' 项需要检查：'+all.slice(0,3).map(x=>x.msg).join('；'):'填写检查通过。保存后去对战试试效果。';
      $('insVal').classList.toggle('error',report.errors.length>0);
      return report;
    }
    function changed(structural=false){
      dirty=true;rawInvalid=false;
      if(document.activeElement!==$('insRaw'))$('insRaw').value=JSON.stringify(fx,null,2);
      clearTimeout(historyTimer);clearTimeout(checkTimer);
      if(structural){if(invalidJSON.size){message('先修正未完成的数据，再改变结构。');return;}history.push(snapshot());render();check();persistDraft();}
      else {historyTimer=setTimeout(()=>{history.push(snapshot());syncHistory();persistDraft();},350);checkTimer=setTimeout(check,180);}
      syncHistory();persistDraft();message('尚未保存');
    }
    function input(v,set,key,type='text',list){
      const el=node('input','ins-input');el.type=type;el.value=v==null?'':String(v);el.dataset.focusKey=key;
      if(list)el.setAttribute('list',list);
      let composing=false;
      el.addEventListener('compositionstart',()=>{composing=true;});
      el.addEventListener('compositionend',()=>{composing=false;set(el.value);changed();});
      el.oninput=e=>{if(composing||e.isComposing)return;set(el.value);changed();};
      return el;
    }
    function select(v,options,set,key){
      const el=node('select','ins-input');el.dataset.focusKey=key;
      const opts=options.slice();if(v!=null&&!opts.some(o=>String(o[0])===String(v)))opts.unshift([v,String(v)]);
      for(const [value,text] of opts){const o=node('option','',text);o.value=value;el.append(o);}
      el.value=v==null?'':String(v);el.onchange=()=>{set(el.value);changed(true);};return el;
    }
    function disclosure(title,content,open=false){const d=node('details','ins-detail'),s=node('summary','',title);d.open=open;d.append(s,content);return d;}
    function json(value,set,key){const t=node('textarea','ins-json');t.value=JSON.stringify(value,null,2);t.dataset.focusKey=key;t.spellcheck=false;
      t.oninput=()=>{try{set(JSON.parse(t.value));t.classList.remove('invalid');invalidJSON.delete(key);changed();}catch(e){t.classList.add('invalid');invalidJSON.add(key);dirty=true;message('这段数据尚未填写完整，先不要保存。');}};return t;}
    const fieldLabel=name=>labels[name]||Core.SEED_FIELD[name]||g.KG_EFFECT_LABELS?.field?.[name]||name;
    const safeName=name=>!!name&&!['__proto__','prototype','constructor'].includes(name);
    function dataEditor(v,set,key,suggestions=[],fixedObject=false){
      const box=node('div','ins-data'),type=v===null?'null':Array.isArray(v)?'list':typeof v==='object'?'object':typeof v;
      if(!fixedObject)box.append(select(type,[['string','文字'],['number','数字'],['boolean','是 / 否'],['object','字段组'],['list','列表'],['null','空值']],s=>set(s==='object'?{}:s==='list'?[]:s==='number'?0:s==='boolean'?false:s==='null'?null:''),key+'.type'));
      if(type==='object'){
        for(const name of Object.keys(v)){
          const line=node('div','ins-data-field');line.append(row(fieldLabel(name),parameterControl(v[name],x=>{v[name]=x;set(v);},key+'.'+name,vocabulary.infer(name))),button('移除字段 '+name,()=>{delete v[name];set(v);changed(true);},'danger'));box.append(line);
        }
        const line=node('div','ins-line'),name=input('',()=>{},key+'.newName');name.placeholder='字段名';
        if(suggestions.length){const hints=node('datalist');hints.id='fields-'+key.replace(/[^\w-]/g,'-');for(const s of suggestions)hints.append(node('option','',s));name.setAttribute('list',hints.id);line.append(hints);}
        line.append(name,button('加入字段',()=>{const id=name.value.trim();if(!safeName(id)||Object.hasOwn(v,id)){message('请填写未使用的有效字段名。');return;}v[id]=clone(vocabulary.infer(id).initial);set(v);changed(true);}));box.append(line);
      }else if(type==='list'){
        v.forEach((x,i)=>{const line=node('div','ins-data-field');line.append(row('第 '+(i+1)+' 项',dataEditor(x,y=>{v[i]=y;set(v);},key+'.'+i)),button('移除列表项',()=>{v.splice(i,1);set(v);changed(true);},'danger'));box.append(line);});
        box.append(button('＋ 列表项',()=>{v.push('');set(v);changed(true);}));
      }else if(type==='boolean')box.append(select(String(v),[['false','否'],['true','是']],s=>set(s==='true'),key+'.value'));
      else if(type!=='null')box.append(input(v,s=>set(type==='number'&&s.trim()&&Number.isFinite(Number(s))?Number(s):s),key+'.value'));
      return box;
    }
    function selectorEditor(v,set,key){
      const box=node('div','ins-selector');box.append(select(typeof v==='string'?'ref':'query',[['ref','引用名称'],['query','选择一组对象']],s=>set(s==='ref'?'self':{sel:'all',side:'friendly',zone:'field'}),key+'.form'),queryEditor(v,set,key));return box;
    }
    function conditionList(list,key){
      const box=node('div','ins-condition-list');if(!Array.isArray(list))return dataEditor(list,()=>{},key);
      list.forEach((c,i)=>{box.append(conditionEditor(c,x=>list[i]=x,key+'.'+i),button('删除判断条件',()=>{list.splice(i,1);changed(true);},'danger'));});
      box.append(button('＋ 判断条件',()=>{list.push({op:'compare',left:0,cmp:'>=',value:1});changed(true);}));return box;
    }
    function conditionEditor(c,set,key,subject='self'){
      if(!c||typeof c!=='object'||Array.isArray(c))return dataEditor(c,set,key);
      const box=node('div','ins-condition');box.append(row('条件原语',select(c.op,Object.entries(vocabulary.conditions).map(([id,d])=>[id,d.name+' · '+id]),op=>set(op==='predicate'?{op,test:{test:'all',items:[]}}:op==='and'||op==='or'?{op,items:[]}:op==='not'?{op,item:{op:'and',items:[]}}:{op}),key+'.op')));
      if(c.op==='predicate')box.append(predicateEditor(c.test,x=>c.test=x,key,subject));
      else if(c.op==='and'||c.op==='or'){if(Array.isArray(c.items))box.append(conditionList(c.items,key+'.items'));else box.append(button('＋ 判断条件列表',()=>{c.items=[];changed(true);}));}
      else if(c.op==='not'){
        const legacy=Array.isArray(c.items)&&!c.item;box.append(conditionEditor(c.item||c.items?.[0]||{op:'and',items:[]},x=>{if(legacy)c.items[0]=x;else c.item=x;},key+'.item',subject));
      }else box.append(parameterFields(c,key,vocabulary.conditions[c.op]||{fields:[]}));
      box.append(disclosure('其它条件字段',dataEditor(Object.fromEntries(Object.entries(c).filter(([k])=>k!=='op')),x=>{for(const k of Object.keys(c))if(k!=='op')delete c[k];Object.assign(c,x);},key+'.extra',[],true))); 
      return box;
    }
    function parameterControl(v,set,key,f){
      if(f.type==='selector')return selectorEditor(v,set,key);
      if(f.type==='condition')return conditionEditor(v,set,key);
      if(f.type==='filter')return dataEditor(v,set,key,['unitType','cardType','set','setIn','minCost','maxCost','keyword','damaged','where']);
      if(f.type==='actions')return Array.isArray(v)?actionList(v,key):dataEditor(v,set,key);
      if(f.type==='effect')return v&&typeof v==='object'&&!Array.isArray(v)?effectFields(v,key,'子效果什么时候执行'):dataEditor(v,set,key);
      if(f.type==='effectList')return Array.isArray(v)?effectList(v,key):dataEditor(v,set,key);
      if(f.type==='value')return valueEditor(v,set,key);
      if(f.type==='predicate')return predicateEditor(v,set,key);
      if(f.type==='boolean')return typeof v==='boolean'?select(String(v),[['false','否'],['true','是']],s=>set(s==='true'),key):dataEditor(v,set,key);
      if(f.type==='legacySide')return select(v,[['self','我方'],['enemy','敌方'],['friendly','友方'],['opponent','对方'],['both','双方'],['any','任意方'],['0','玩家 1'],['1','玩家 2']],s=>set(s==='0'||s==='1'?Number(s):s),key);
      if(f.type==='comparison')return select(v,[['>=','不小于'],['>','大于'],['<=','不大于'],['<','小于'],['==','等于'],['!=','不等于']],set,key);
      if(f.type==='code'){const text=node('textarea','ins-json');text.value=v??'';text.dataset.focusKey=key;text.spellcheck=false;text.oninput=()=>{set(text.value);changed();};return text;}
      if(f.type==='query')return queryEditor(v,set,key);
      if(enums[f.type]||f.type==='keyword'||f.type==='trigger')return enumEditor(f.type,v,set,key);
      if(f.type==='text'||f.type==='card')return typeof v==='string'?input(v,set,key,'text',f.type==='card'?'insCards':null):dataEditor(v,set,key);
      return dataEditor(v,set,key);
    }
    function parameterFields(a,key,def){
      const box=node('div','ins-generic-parameters');
      for(const f of def.fields){
        if(!Object.hasOwn(a,f.name)){const b=button('＋ '+fieldLabel(f.name),()=>{a[f.name]=clone(f.initial);changed(true);});b.dataset.parameter=f.name;b.title=f.desc||f.name;box.append(b);continue;}
        const line=node('div','ins-parameter');line.append(row(fieldLabel(f.name),parameterControl(a[f.name],x=>a[f.name]=x,key+'.'+f.name,f)),button('移除参数 '+f.name,()=>{delete a[f.name];changed(true);},'danger'));box.append(line);
      }return box;
    }
    function valueEditor(v,set,key){
      const wrap=node('div','ins-value');const kind=v&&typeof v==='object'&&!Array.isArray(v)?(v.expr==='literal'?'object':v.expr||'legacy'):'literal';
      const seeds={literal:0,object:{expr:'literal',value:{}},read:{expr:'read',of:'candidate',path:'attack'},variable:{expr:'variable',name:'v1'},count:{expr:'count',query:{sel:'all',side:'friendly',zone:'field'}},query:{expr:'query',query:{sel:'all',side:'friendly',zone:'field'}},size:{expr:'size',value:{expr:'variable',name:'v1'}},math:{expr:'math',operator:'add',items:[0,1]},random:{expr:'random',min:1,max:3},memory:{expr:'memory',scope:'unit',key:'counter'}};
      const modes=[['literal','常量'],['object','一组属性值'],['read','读取属性'],['variable','变量'],['count','计数'],['query','一组对象'],['size','列表长度'],['math','算式'],['union','合并列表并去重'],['intersection','列表共有项'],['random','随机数'],['memory','记忆']];
      seeds.union={expr:'union',items:[[],[]]};seeds.intersection={expr:'intersection',items:[[],[]]};
      seeds.legacy={hand:'self'};modes.push(['legacy','原有 DSL 动态取值']);
      const mode=select(kind,modes,k=>set(clone(seeds[k]??v)),key+'.kind');
      const valueSettings=node('div','ins-value-settings');
      if(simple&&kind==='literal')valueSettings.append(row('如何取值',mode));else wrap.append(mode);
      if(kind==='literal'){
        const type=typeof v==='boolean'?'boolean':typeof v==='number'?'number':Array.isArray(v)?'list':'text';
        const typePicker=select(type,[['number','数字'],['text','文字'],['boolean','是 / 否'],['list','列表']],t=>set(t==='number'?0:t==='boolean'?true:t==='list'?[]:''),key+'.type');
        if(simple)valueSettings.append(row('填写类型',typePicker));else wrap.append(typePicker);
        if(type==='boolean')wrap.append(select(String(v),[['true','是'],['false','否']],s=>set(s==='true'),key+'.literal'));
        else if(type==='list')wrap.append(dataEditor(v,set,key+'.literal'));
        else {const t=input(v,s=>set(type==='number'?(s.trim()!==''&&Number.isFinite(Number(s))?Number(s):s):s),key+'.literal');if(type==='number')t.inputMode='decimal';wrap.append(t);}
        if(simple){const detail=disclosure('更多取值方式',valueSettings);detail.classList.add('ins-value-more');wrap.append(detail);}
      } else if(kind==='object')wrap.append(dataEditor(v.value,x=>v.value=x,key+'.object'));
      else if(kind==='read'){
        wrap.append(input(v.of,s=>v.of=s,key+'.of','text','insRefs'),input(v.path,s=>v.path=s,key+'.path','text','insProperties'));
      } else if(kind==='variable')wrap.append(input(v.name,s=>v.name=s,key+'.name','text','insRefs'));
      else if(kind==='count'||kind==='query')wrap.append(queryEditor(v.query,q=>v.query=q,key+'.query'));
      else if(kind==='size')wrap.append(valueEditor(v.value,x=>v.value=x,key+'.value'));
      else if(kind==='memory')wrap.append(select(v.scope,enums.scope,s=>v.scope=s,key+'.scope'),input(v.key,s=>v.key=s,key+'.key'));
      else if(kind==='math'||kind==='union'||kind==='intersection'){
        if(kind==='math')wrap.append(select(v.operator,[['add','加'],['subtract','减'],['multiply','乘'],['divide','除'],['min','取最小'],['max','取最大'],['floor','向下取整'],['ceil','向上取整']],s=>v.operator=s,key+'.operator'));
        (v.items||[]).forEach((item,i)=>{const r=node('div','ins-line');r.append(valueEditor(item,x=>v.items[i]=x,key+'.items.'+i),button('删除此项',()=>{v.items.splice(i,1);changed(true);}));wrap.append(r);});
        wrap.append(button('＋ 运算项',()=>{v.items=v.items||[];v.items.push(kind==='math'?0:[]);changed(true);}));
      } else if(kind==='random')wrap.append(row('最小',valueEditor(v.min,x=>v.min=x,key+'.min')),row('最大',valueEditor(v.max,x=>v.max=x,key+'.max')));
      else {
        const legacy={var:{var:'v1'},cardVar:{cardVar:'v1',stat:'cost'},stat:{stat:'attack',of:'self'},count:{count:{sel:'all',side:'friendly'},times:1},hand:{hand:'self'},deck:{deck:'self'},hq:{hq:'self'},kredits:{kredits:'self'},turn:{turn:true},revealedCount:{revealedCount:'enemy'},supportFree:{supportFree:'self'},frontlineFree:{frontlineFree:'self'},lastAdded:{lastAdded:true},rand:{rand:[1,3]},maxStat:{maxStat:{stat:'attack',spec:{sel:'all',side:'friendly'}}},playedCount:{playedCount:{name:''},of:'self'},sum:{sum:{items:[0,1]}},mul:{mul:{items:[1,2]}},min:{min:{items:[0,1]}},max:{max:{items:[0,1]}}};
        const pick=select('',[['','选择原有 DSL 取值方式…'],...Object.keys(legacy).map(id=>[id,fieldLabel(id)+' · '+id])],id=>{if(legacy[id])set(clone(legacy[id]));},key+'.legacyKind');wrap.append(pick,dataEditor(v,set,key+'.legacy'));
      }
      return wrap;
    }
    function predicateEditor(t,set,key,subject='candidate'){
      t=t||{test:'all',items:[]};const box=node('div','ins-predicate');
      const kind=t.test||'legacy';const modes=[['all','全部满足（且）'],['any','至少一个满足（或）'],['not','取反（非）'],['compare','比较两个值'],['exists','属性存在']];
      modes.push(['legacy','DSL 条件原语']);
      const seed=k=>k==='legacy'?{op:'and',items:[]}:k==='all'||k==='any'?{test:k,items:[]}:k==='not'?{test:'not',item:{test:'all',items:[]}}:{test:k,left:{expr:'read',of:subject,path:'attack'},...(k==='compare'?{cmp:'>=',right:1}:{})};
      box.append(select(kind,modes,k=>set(seed(k)),key+'.kind'));
      if(kind==='all'||kind==='any'){
        (t.items||[]).forEach((item,i)=>{const r=node('div','ins-test-item');r.append(predicateEditor(item,x=>t.items[i]=x,key+'.'+i,subject),button('移除条件',()=>{t.items.splice(i,1);changed(true);},'danger'));box.append(r);});
        box.append(button('＋ 条件',()=>{t.items=t.items||[];t.items.push(seed('compare'));set(t);changed(true);}));
        if(!(t.items||[]).length)box.append(node('p','muted',kind==='all'?'尚无条件：全部对象都通过。':'尚无条件：没有对象通过。'));
      } else if(kind==='not')box.append(predicateEditor(t.item,x=>t.item=x,key+'.not',subject));
      else if(kind==='compare'||kind==='exists'){
        const simple=t.left&&t.left.expr==='read'&&t.left.of==='candidate';
        if(simple)box.append(row('对象属性',input(t.left.path,s=>t.left.path=s,key+'.property','text','insProperties')));
        else box.append(row('左值',valueEditor(t.left,x=>t.left=x,key+'.left')));
        if(kind==='compare'){
          box.append(select(t.cmp,[['==','等于'],['!=','不等于'],['>','大于'],['>=','不小于'],['<','小于'],['<=','不大于'],['contains','包含'],['in','属于列表']],s=>t.cmp=s,key+'.cmp'));
          box.append(row('右值',valueEditor(t.right,x=>t.right=x,key+'.right')));
        }
        if(simple)box.append(disclosure('用表达式比较属性、变量或其它对象',valueEditor(t.left,x=>t.left=x,key+'.left')));
      } else box.append(conditionEditor(t,set,key+'.condition',subject));
      return box;
    }
    function queryEditor(q,set,key){
      if(typeof q==='string'){
        const targets=fx.flatMap(e=>Array.isArray(e?.targets)?e.targets:[]).map(t=>[t.id,'玩家选的目标：'+(t.prompt||t.id)]);
        return row('对谁生效',targets.some(t=>t[0]===q)?select(q,targets,set,key+'.ref'):input(q,set,key+'.ref','text','insRefs'));
      }
      q=q||{sel:'all',side:'friendly',zone:'field'};const box=node('div','ins-query');
      box.append(row('选择方式',select(q.sel||'all',[['all','全部符合者'],['random','随机选取'],['first','从最左侧 / 牌堆顶取'],['last','从最右侧 / 牌堆底取'],['self','效果来源'],['refAll','命名对象 / 列表'],['ref','命名对象中的第一个'],['adjacent','相邻单位'],['union','合并多组对象']],s=>{
        const next={...q,sel:s};if(s==='ref'||s==='refAll'||s==='adjacent')next.ref=next.ref||'current';if(s==='random')next.count=next.count??1;if(s==='union')next.queries=next.queries||[{sel:'self'}];set(next);
      },key+'.sel')));
      if(q.sel==='union'){
        (q.queries||[]).forEach((item,i)=>box.append(queryEditor(item,x=>{q.queries[i]=x;set(q);},key+'.queries.'+i),button('删除此组',()=>{q.queries.splice(i,1);set(q);changed(true);})));box.append(button('＋ 一组对象',()=>{q.queries=q.queries||[];q.queries.push({sel:'self'});set(q);changed(true);}));
      }else if(q.sel==='ref'||q.sel==='refAll'||q.sel==='adjacent'){
        box.append(row('名称',input(q.ref,s=>{q.ref=s;set(q);},key+'.ref','text','insRefs')));
        if(q.sel==='adjacent')box.append(row('包括指定单位',select(String(!!q.includeAnchor),[['false','仅相邻单位'],['true','指定单位及相邻单位']],s=>{q.includeAnchor=s==='true';set(q);},key+'.includeAnchor')));
      }
      else if(q.sel!=='self'){
        box.append(row('归属',select(q.side||'friendly',[['friendly','友方'],['enemy','敌方'],['both','双方']],s=>{q.side=s;set(q);},key+'.side')));
        box.append(row('区域',select(q.zone||'field',[['field','整个战场'],['support','支援阵线'],['frontline','前线'],['hand','手牌'],['deck','卡组'],['discard','弃牌区'],['hq','总部'],['pool','完整卡池']],s=>{q.zone=s;set(q);},key+'.zone')));
      }
      if(['random','first','last'].includes(q.sel))box.append(row('数量',valueEditor(q.count??1,x=>{q.count=x;set(q);},key+'.count')));
      const where=q.where||(q.filter&&q.filter.where);
      if(where)box.append(row('只保留满足条件的对象',predicateEditor(where,x=>{q.where=x;if(q.filter)delete q.filter.where;set(q);},key+'.where')),button('取消筛选',()=>{delete q.where;if(q.filter)delete q.filter.where;set(q);changed(true);}));
      else box.append(button('＋ 属性筛选（且 / 或 / 非）',()=>{q.where={test:'all',items:[]};set(q);changed(true);},'add-filter'));
      box.append(disclosure('DSL 筛选字段',q.filter?dataEditor(q.filter,x=>{q.filter=x;set(q);},key+'.filter',['unitType','cardType','set','keyword','minCost','maxCost']):button('＋ 筛选字段组',()=>{q.filter={};set(q);changed(true);}))); 
      const extra=Object.keys(q).filter(k=>!['sel','side','zone','ref','count','queries','includeAnchor','where','filter'].includes(k));
      box.append(disclosure('其它选择器字段',dataEditor(Object.fromEntries(extra.map(k=>[k,q[k]])),x=>{extra.forEach(k=>delete q[k]);Object.assign(q,x);set(q);},key+'.extra',['excludeSelf','count'],true))); 
      return box;
    }
    function enumEditor(type,v,set,key){
      const options=type==='trigger'?Object.entries(Core.TRIGGERS):type==='keyword'?Object.entries(K.KEYWORDS).map(([id,d])=>[id,d.cn||id]):enums[type];
      return select(v,options,set,key);
    }
    function cardPoolEditor(pool,set,key,fixed){
      const box=node('div','ins-card-pool');
      if(!pool||typeof pool!=='object'){box.append(json(pool,set,key));return box;}
      box.append(row('候选来源',select(pool.source,fixed?[['cards','指定卡牌']]:[['cards','指定卡牌'],['pool','完整卡池'],['deck','卡组'],['hand','手牌'],['discard','弃牌区']],s=>{pool.source=s;if(s==='cards')pool.cardIds=pool.cardIds||[];set(pool);},key+'.source')));
      if(pool.source==='cards'){
        const ids=Array.isArray(pool.cardIds)?pool.cardIds:[],cards=api.list(),byId=new Map(cards.map(c=>[c.id,c]));
        box.append(node('p','muted','已指定 '+ids.length+' 张候选牌'+(fixed?'（最多三张，按此顺序展示）':'；随机抽取最多三张不同的牌')));
        ids.forEach((id,i)=>{const line=node('div','ins-pool-card');line.append(node('span','',byId.get(id)?.name||'缺失卡牌：'+id),button('移除',()=>{pool.cardIds.splice(i,1);changed(true);},'danger'));box.append(line);});
        const search=node('input','ins-input');search.placeholder='搜索候选卡名、国家或 ID';search.setAttribute('aria-label','搜索候选卡牌');search.dataset.focusKey=key+'.search';
        const picker=node('select','ins-input');picker.setAttribute('aria-label','候选卡牌');picker.dataset.focusKey=key+'.card';
        const refill=()=>{const previous=picker.value,term=search.value.trim().toLowerCase();picker.replaceChildren();const empty=node('option','','选择候选卡牌…');empty.value='';picker.append(empty);for(const c of cards){if(ids.includes(c.id)||!`${c.name} ${c.id} ${c.set}`.toLowerCase().includes(term))continue;const o=node('option','',c.name+' · '+c.id);o.value=c.id;picker.append(o);}if([...picker.options].some(o=>o.value===previous))picker.value=previous;};
        search.oninput=refill;refill();box.append(search,picker,button('加入候选卡池',()=>{if(!picker.value)return;if(fixed&&ids.length>=3){message('固定三选一最多指定三张牌；更多候选请改为随机模式。');return;}pool.cardIds=[...ids,picker.value];set(pool);changed(true);}));
      }else{
        if(pool.source!=='pool')box.append(row('卡池归属',select(pool.side||'friendly',pool.source==='hand'?[['friendly','我方']]:[['friendly','我方'],['enemy','敌方'],['both','双方']],s=>pool.side=s,key+'.side')));
        for(const [k,label] of [['includeTokens','包含衍生牌'],['includeReserved','包含预备牌']])box.append(row(label,select(String(!!pool[k]),[['false','否'],['true','是']],s=>pool[k]=s==='true',key+'.'+k)));
      }
      if(pool.where)box.append(row('候选筛选',predicateEditor(pool.where,x=>pool.where=x,key+'.where')),button('取消卡池筛选',()=>{delete pool.where;changed(true);}));
      else box.append(button('＋ 卡池筛选条件',()=>{pool.where={test:'all',items:[]};changed(true);}));
      box.append(node('p','muted',fixed?'选中的牌加入手牌。':'从符合条件的卡池抽取最多三张不同的候选牌，再选择一张加入手牌；卡池不足三张时展示实际数量。'));
      return box;
    }
    function effectFields(e,key,title='什么时候生效') {
      const box=node('div','ins-effect-fields'),optional=node('div');
        box.append(timingControls(e,key,title));
        if(!K.parseBehaviorTrigger(e.trigger))box.append(row('谁的事件',select(e.eventSide||(e.watchOpponent||e.forOpponent?'enemy':e.anyOwner?'any':'friendly'),[['friendly','友方'],['enemy','敌方'],['any','双方']],x=>{e.eventSide=x;delete e.watchOpponent;delete e.forOpponent;delete e.anyOwner;},key+'.eventSide')));
        if(e.condition){
          box.append(row('满足什么条件',conditionEditor(e.condition,x=>e.condition=x,key+'.condition','self')),button('取消生效条件',()=>{delete e.condition;changed(true);}));
        }else {const conditionButton=button('＋ 生效条件',()=>{e.condition={op:'predicate',test:{test:'all',items:[]}};changed(true);});if(simple)optional.append(conditionButton);else box.append(conditionButton);}
        if(e.targets?.length){const targets=node('div','ins-targets');e.targets.forEach((t,j)=>{
          targets.append(row('目标名称',input(t.id,s=>t.id=s,key+'.targets.'+j+'.id')),row('玩家提示',input(t.prompt,s=>t.prompt=s,key+'.targets.'+j+'.prompt')));
          const tk=key+'.targets.'+j;
          targets.append(row('目标归属',select(t.side||'enemy',[['friendly','友方'],['enemy','敌方'],['both','双方']],s=>t.side=s,tk+'.side')),
            row('可选区域',select(t.zone||'field',[['field','整个战场'],['support','支援阵线'],['frontline','前线']],s=>t.zone=s,tk+'.zone')),
            row('对象种类',select(t.kind||'unit',[['unit','单位'],['hq','总部'],['any','单位或总部']],s=>t.kind=s,tk+'.kind')));
          if(t.filter?.where)targets.append(predicateEditor(t.filter.where,x=>t.filter.where=x,tk+'.where'));
          else targets.append(button('＋ 目标属性筛选',()=>{t.filter={...(t.filter||{}),where:{test:'all',items:[]}};changed(true);}));
          targets.append(disclosure('其它目标字段',dataEditor(Object.fromEntries(Object.entries(t).filter(([k])=>!['id','prompt','side','zone','kind'].includes(k))),x=>{for(const k of Object.keys(t))if(!['id','prompt','side','zone','kind'].includes(k))delete t[k];Object.assign(t,x);},tk+'.extra',['optional','filter'],true))); 
          targets.append(button('移除这个目标',()=>{e.targets.splice(j,1);changed(true);},'danger'));
        });box.append(disclosure('玩家选择的目标',targets,true));}
        const targetButton=button('＋ 玩家先选一个目标',()=>{e.targets=e.targets||[];const used=Core.usedTargetIds(fx);let id='target1';while(used.includes(id))id='target'+(Number(id.slice(6))+1);e.targets.push({id,side:'enemy',kind:'unit',prompt:'选择一个单位'});changed(true);});if(simple&&!e.targets?.length)optional.append(targetButton);else box.append(targetButton);
        if(Array.isArray(e.actions))box.append(actionList(e.actions,key+'.actions'));
        else box.append(button('＋ 动作列表',()=>{e.actions=[];changed(true);}));
        if(e.else)box.append(row('条件不满足时',actionList(e.else,key+'.else')),button('取消条件不满足时的动作',()=>{delete e.else;changed(true);}));else box.append(button('＋ 条件不满足时的动作',()=>{e.else=[];changed(true);}));
        if(optional.children.length)box.append(disclosure('添加限制条件或让玩家先选目标（可选）',optional));
        const extras=Object.keys(e).filter(k=>!['trigger','condition','targets','actions','else','once','oncePerTurn','firstEvent','eventSide',...(K.parseBehaviorTrigger(e.trigger)?['eventSide','selfOnly','listenZone']:[])].includes(k));
        box.append(disclosure('光环 / 常驻及其它 DSL 字段',dataEditor(Object.fromEntries(extras.map(k=>[k,e[k]])),x=>{extras.forEach(k=>delete e[k]);Object.assign(e,x);},key+'.extra',['vars','aura','passiveRules','cardFields','selfOnly','listenZone','choices'],true))); 
        const limits=node('div','ins-effect-limits');
        for(const [name,label] of [['once','整局只生效一次'],['oncePerTurn','每回合只生效一次']])limits.append(row(label,select(String(!!e[name]),[['false','否'],['true','是']],x=>{if(x==='true')e[name]=true;else delete e[name];},key+'.'+name)));
        limits.append(row('第几次事件',select(e.firstEvent||'any',[['any','每次'],['turn','本回合第一次'],['match','整局第一次']],x=>{if(x==='any')delete e.firstEvent;else e.firstEvent=x;},key+'.firstEvent')));
        box.append(disclosure('生效次数限制',limits));
      return box;
    }
    function fields(a,key){
      const def=C[a.op]||{fields:[]},body=node('div','ins-parameters');
      if(simple&&a.op==='loop'&&Object.keys(a).every(k=>['op','times','actions'].includes(k))&&a.actions?.length===1&&a.actions[0]?.op==='drawOne'&&Object.keys(a.actions[0]).every(k=>['op','side'].includes(k))){
        body.append(row('抽几张牌',valueEditor(a.times,x=>a.times=x,key+'.times')),row('谁来抽牌',enumEditor('side',a.actions[0].side||'self',x=>a.actions[0].side=x,key+'.actions.0.side')));return body;
      }
      for(const f of def.fields){
        if(a.op==='bindEvent'&&a.effect&&['trigger','actions'].includes(f.name))continue;
        if(!Object.prototype.hasOwnProperty.call(a,f.name)){
          const b=button('＋ '+fieldLabel(f.name),()=>{a[f.name]=clone(f.name==='effect'&&a.op==='bindEvent'&&a.trigger?{trigger:a.trigger,actions:a.actions||[]}:f.name==='effect'&&a.op==='grantEffect'?{trigger:a.effectTrigger||'death',actions:a.effects||[]}:f.initial);changed(true);});b.dataset.parameter=f.name;b.title=f.desc||f.name;body.append(b);continue;
        }
        const v=a[f.name],set=x=>{a[f.name]=x;if(a.op==='threeChoice'&&f.name==='mode'&&x==='fixed')a.pool={...a.pool,source:'cards',cardIds:a.pool?.cardIds||[]};},p=key+'.'+f.name;let control;
        if(f.type==='actions'){body.append(row(fieldLabel(f.name),parameterControl(v,set,p,f)));continue;}
        if(f.type==='options'){
          if(!Array.isArray(v)){body.append(row(labels[f.name],json(v,set,p)));continue;}
          const ob=node('div','ins-options');
          v.forEach((o,i)=>{if(Array.isArray(o)){ob.append(actionList(o,p+'.'+i),button('删除选项',()=>{v.splice(i,1);changed(true);},'danger'));return;}if(!o||typeof o!=='object'){ob.append(dataEditor(o,x=>v[i]=x,p+'.'+i));return;}const box=node('div','ins-option');box.append(row('选项文字',input(o.label,s=>o.label=s,p+'.'+i+'.label')));
            if(o.test)box.append(predicateEditor(o.test,x=>o.test=x,p+'.'+i+'.test'));
            else if(a.op==='choice')box.append(button('＋ 选项可用条件',()=>{o.test={test:'all',items:[]};changed(true);}));
            if(o.condition)box.append(conditionEditor(o.condition,x=>o.condition=x,p+'.'+i+'.condition'));else box.append(button('＋ DSL 选项条件',()=>{o.condition={op:'and',items:[]};changed(true);}));
            const extra=Object.keys(o).filter(k=>!['label','test','condition','actions'].includes(k));box.append(disclosure('选项目标、费用及其它字段',dataEditor(Object.fromEntries(extra.map(k=>[k,o[k]])),x=>{extra.forEach(k=>delete o[k]);Object.assign(o,x);},p+'.'+i+'.extra',['targets','cost','requireKredits'],true))); 
            if(o.actions)box.append(actionList(o.actions,p+'.'+i+'.actions'));else box.append(button('＋ 选项动作',()=>{o.actions=[];changed(true);}));box.append(button('删除选项',()=>{v.splice(i,1);changed(true);},'danger'));ob.append(box);});
          ob.append(button('＋ 选项',()=>{v.push({label:'新选项',actions:[]});changed(true);}));body.append(row(labels[f.name],ob));continue;
        }
        if(f.type==='effect'){
          control=node('div','ins-sub-effect');
          if(!v||typeof v!=='object'||Array.isArray(v))control.append(json(v,set,p));
          else control.append(effectFields(v,p,'什么时候执行'));
        }
        else if(f.type==='effectList')control=effectList(v,p);
        else if(f.type==='cardPool')control=cardPoolEditor(v,set,p,a.op==='threeChoice'&&a.mode==='fixed');
        else if(f.type==='eventData')control=dataEditor(v,set,p);
        else if(f.type==='query')control=queryEditor(v,set,p);
        else if(f.type==='value'||f.type==='card'&&typeof v==='object')control=valueEditor(v,set,p);
        else if(f.type==='predicate')control=predicateEditor(v,set,p);
        else if(f.type==='boolean')control=select(String(v),[['true','是'],['false','否']],s=>set(s==='true'),p);
        else if(enums[f.type]||f.type==='keyword'||f.type==='trigger')control=enumEditor(f.type,v,set,p);
        else control=parameterControl(v,set,p,f);
        body.append(row(f.type==='offerMode'?'候选方式':['bindHQEvent','hqEnchant'].includes(a.op)&&f.name==='name'?'效果名称':fieldLabel(f.name),control));
        if(f.optional)body.append(button('移除参数 '+f.name,()=>{delete a[f.name];changed(true);},'danger'));
      }
      const extra=Object.keys(a).filter(k=>k!=='op'&&!def.fields.some(f=>f.name===k));
      body.append(disclosure('其它 DSL 参数',dataEditor(Object.fromEntries(extra.map(k=>[k,a[k]])),x=>{extra.forEach(k=>delete a[k]);for(const [name,value] of Object.entries(x))if(name!=='op')a[name]=value;},key+'.extra',[],true))); 
      return body;
    }
    function effectList(list,key){
      const box=node('div','ins-sub-effect-list');
      if(!Array.isArray(list)){box.append(node('p','error','子效果应为列表。'));return box;}
      list.forEach((effect,i)=>{const item=node('section','ins-sub-effect ins-effect');
        if(effect&&typeof effect==='object'&&!Array.isArray(effect))item.append(effectFields(effect,key+'.'+i,'子效果什么时候执行'));
        else item.append(json(effect,x=>list[i]=x,key+'.'+i+'.json'));
        item.append(button('删除这条子效果',()=>{list.splice(i,1);changed(true);},'danger'));box.append(item);
      });
      box.append(button('＋ 再加一条子效果',()=>{list.push({trigger:'turnStart',actions:[]});changed(true);}));return box;
    }
    function actionList(list,key){
      const box=node('section','ins-action-list');
      if(!Array.isArray(list)){box.append(node('p','error','动作列表格式有误，请修正原始数据。'));return box;}
      list.forEach((a,i)=>{
        if(!a||typeof a!=='object'||Array.isArray(a)){box.append(row('动作格式有误',json(a,x=>list[i]=x,key+'.'+i+'.json')));return;}
        const item=node('article','ins-action'),head=node('div','ins-action-head');
        const drawLoop=simple&&a.op==='loop'&&Object.keys(a).every(k=>['op','times','actions'].includes(k))&&a.actions?.length===1&&a.actions[0]?.op==='drawOne'&&Object.keys(a.actions[0]).every(k=>['op','side'].includes(k));
        head.append(node('b','',i+1+'. '+(drawLoop?'抽牌':actionNames[a.op]||C[a.op]?.name||Core.SEED_OP[a.op]||a.op||'已有动作')));
        for(const step of [-1,1]){const b=button(step<0?'↑':'↓',()=>{const x=list.splice(i,1)[0];list.splice(i+step,0,x);changed(true);});b.disabled=i+step<0||i+step>=list.length;head.append(b);}
        head.append(button('删除',()=>{list.splice(i,1);changed(true);},'danger'));item.append(head);
        if(C[a.op]){item.append(fields(a,key+'.'+i));if(C[a.op].composite)item.append(button('展开为基本动作',()=>{try{const result=P.expand([a],'offer_'+Date.now().toString(36));if(result.unresolved.length){message('请先完善卡池设置。');return;}list.splice(i,1,...result.actions);changed(true);}catch(e){message(e.message);}}));}
        else {
          const result=P.expand([a],'expanded_'+Date.now().toString(36));
          item.append(node('p','muted',result.unresolved.length?'此动作尚未注册到当前引擎。参数可以编辑，保存时会检查引擎支持情况。':'这个已有动作可以拆成几步，方便逐步修改。'));
          if(!result.unresolved.length)item.append(button('展开为基本动作',()=>{list.splice(i,1,...result.actions);changed(true);}));
          for(const k of ['actions','then','else'])if(Array.isArray(a[k]))item.append(actionList(a[k],key+'.'+i+'.'+k));
          if(a.effect&&typeof a.effect==='object')item.append(effectFields(a.effect,key+'.'+i+'.effect','子效果什么时候执行'));
          if(Array.isArray(a.effects)){if(a.effects.every(e=>e&&!e.op))item.append(effectList(a.effects,key+'.'+i+'.effects'));else item.append(actionList(a.effects,key+'.'+i+'.effects'));}
          item.append(fields(a,key+'.'+i));
        }
        box.append(item);
      });
      const add=node('div','ins-add-action');const picker=node('select','ins-input');picker.dataset.focusKey=key+'.add';
      const empty=node('option','','选择一个动作…');empty.value='';picker.append(empty);
      const common=['dealDamage','restoreHealth','changeAttribute','setKeyword','drawOne','createUnit','createCard','threeChoice','developCard','destroyUnit'];
      const groups={};for(const [id,d] of Object.entries(C).sort(([a],[b])=>(common.includes(a)?0:1)-(common.includes(b)?0:1))){const group=common.includes(id)?'常用动作':d.group;if(!groups[group]){groups[group]=node('optgroup');groups[group].label=group;picker.append(groups[group]);}const o=node('option','',(actionNames[id]||d.name)+' · '+id);o.value=id;groups[group].append(o);}
      add.append(picker,button('添加动作',()=>{if(C[picker.value]){list.push(seed(picker.value));changed(true);}}));box.append(add);return box;
    }
    function render(){
      const main=$('insTree'),st=main.scrollTop,active=document.activeElement,key=active?.dataset.focusKey,start=active?.selectionStart,end=active?.selectionEnd;
      invalidJSON.clear();main.replaceChildren();if(!card){const empty=node('div','ins-empty');empty.append(node('h2','','给卡牌设计效果'),node('p','','先从左边选一张卡，或者点击「新建卡」。'),node('p','muted','选好后，套用一个例子，再修改数字和目标即可。'));main.append(empty);return;}
      const head=node('div','ins-card-heading');head.append(node('h2','',patch.name??card.name??card.id),node('p','muted',patch.text??card.text??''));main.append(head);
      const starter=node('div','ins-starter');starter.append(node('p','ins-steps','① 选发生时机　→　② 选对象和动作　→　③ 保存并试玩'));
      const choices=node('div','ins-examples');
      for(const example of examples){const title=card.cardType==='unit'?example.name:example.name.replace('部署时','使用时');const b=button(title,()=>{
        const effect=clone(example.effect);effect.trigger=card.cardType==='unit'?'deploy':'order';
        const blank=fx.length===1&&Array.isArray(fx[0]?.actions)&&!fx[0].actions.length&&Object.keys(fx[0]).every(k=>['trigger','actions'].includes(k));
        const merged=Core.mergeCompiled(blank?[]:fx,[effect],'append');fx.splice(0,fx.length,...merged);changed(true);message('已添加「'+title+'」。'+example.hint+'保存后生效。');
      });b.dataset.example=example.id;b.title=example.hint;choices.append(b);}
      const hasActions=Array.isArray(fx)&&fx.some(e=>Array.isArray(e?.actions)&&e.actions.length);
      starter.append(node('p','muted','选一个例子开始，再改成你想要的效果。例子会追加一条效果；想重新做时，先删除不需要的效果。'),choices);
      main.append(disclosure('从常见效果开始',starter,!hasActions));
      const face=node('div','ins-face');
      for(const k of ['name','text'])face.append(row(k==='name'?'卡名':'卡面文字',input(patch[k]??card[k],s=>patch[k]=s,'card.'+k)));
      face.append(node('p','muted','卡面文字是给玩家看的说明。修改文字不会自动修改下面的实际效果。'));main.append(disclosure('卡名和卡面说明',face));
      const datalist=(id,values)=>{const dl=node('datalist');dl.id=id;for(const [key,label] of values){const o=node('option');o.value=key;o.label=label;dl.append(o);}return dl;};
      const paths={...properties};for(const c of api.list())for(const k of Object.keys(c))if(!forbiddenField(k))paths['printed.'+k]=paths['printed.'+k]||k;
      main.append(datalist('insProperties',Object.entries(paths)),datalist('insCards',api.list().map(c=>[c.id,c.name||c.id])));
      const refs=new Set(['candidate','current','self','source','event','eventUnit','victim','attacker','defender','friendlyPlayer','enemyPlayer','state']);
      const walk=x=>{if(!x||typeof x!=='object')return;if(x.op==='let'&&x.name)refs.add(x.name);if(x.op==='iterate')refs.add(x.as||'current');if(x.id&&x.side)refs.add(x.id);Object.values(x).forEach(walk);};walk(fx);main.append(datalist('insRefs',[...refs].map(r=>[r,r])));
      if(!Array.isArray(fx)){main.append(node('p','error','效果根级应为列表；请修正原始数据。'));return;}
      fx.forEach((e,i)=>{
        if(!e||typeof e!=='object'||Array.isArray(e)){main.append(row('效果格式有误',json(e,x=>fx[i]=x,'fx.'+i+'.json')));return;}
        const box=node('section','ins-effect'),h=node('div','ins-action-head'),optional=node('div');h.append(node('h3','','效果 '+(i+1)),button('删除效果',()=>{fx.splice(i,1);changed(true);},'danger'));box.append(h);
        box.append(effectFields(e,'fx.'+i));
        main.append(box);
      });
      main.append(button('＋ 再加一条效果',()=>{fx.push({trigger:card.cardType==='unit'?'deploy':'order',actions:[]});changed(true);}));
      if(rawInvalid)main.append(node('p','error','原始数据尚未完成，请先修正再保存。'));
      main.scrollTop=st;if(key){const next=main.querySelector('[data-focus-key="'+CSS.escape(key)+'"]');if(next){next.focus({preventScroll:true});try{next.setSelectionRange(start,end);}catch(e){}}}
    }
    const forbiddenField=k=>['__proto__','prototype','constructor'].includes(k);
    function renderList(){
      const list=$('insList'),q=$('insSearch').value.toLowerCase();list.replaceChildren();
      for(const c of api.list().filter(c=>[c.name,c.id,c.set,c.unitType,c.text].join(' ').toLowerCase().includes(q))){const b=button(c.name||c.id,()=>selectCard(c));b.className='ins-card-item'+(card?.id===c.id?' selected':'');b.append(node('small','',c.set||'自定义'));list.append(b);}
    }
    function syncHistory(){$('insBtnUndo').disabled=!history.canUndo();$('insBtnRedo').disabled=!history.canRedo();}
    function ask(title,choices){
      if(pendingDialog)return pendingDialog;
      const prior=document.activeElement,overlay=node('div','ins-modal'),panel=node('div','ins-modal-panel');panel.setAttribute('role','dialog');panel.setAttribute('aria-modal','true');panel.setAttribute('aria-label',title);panel.tabIndex=-1;
      panel.append(node('p','',title));overlay.append(panel);root.append(overlay);
      pendingDialog=new Promise(resolve=>{
        const done=v=>{overlay.remove();pendingDialog=null;if(prior?.isConnected)prior.focus({preventScroll:true});resolve(v);};
        choices.forEach(([id,label])=>{const b=button(label,()=>done(id),id==='save'?'primary':'');b.onclick=()=>done(id);panel.append(b);});
        panel.onkeydown=e=>{if(e.key==='Escape'){e.stopPropagation();done('cancel');}if(e.key==='Tab'){const bs=panel.querySelectorAll('button'),first=bs[0],last=bs[bs.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus();}}};
        panel.querySelector('button').focus();
      });return pendingDialog;
    }
    async function leave(){
      if(!dirty)return true;
      const choice=await ask('当前修改尚未保存。',[['save','保存后继续'],['discard','放弃修改'],['cancel','继续编辑']]);
      if(choice==='save')return await save();
      if(choice==='discard'){dirty=false;localStorage.removeItem('kg.inspectorDraft');return true;}
      return false;
    }
    async function selectCard(c,recovered){
      if(card&&card.id!==c.id&&!(await leave()))return false;
      clearTimeout(historyTimer);clearTimeout(checkTimer);card=clone(c);fx=clone(recovered?.fx||api.getFx(c.id));patch=clone(recovered?.patch||{});dirty=!!recovered;rawInvalid=!!recovered?.raw;
      $('insRaw').value=recovered?.raw||JSON.stringify(fx,null,2);history.reset(snapshot());render();renderList();check();syncHistory();root.classList.add('ins-card-selected');root.classList.remove('ins-cards-open');message(recovered?'已恢复未保存的草稿':'');return true;
    }
    async function save(confirmed=false){
      if(!card)return false;clearTimeout(historyTimer);history.push(snapshot());syncHistory();
      if(rawInvalid||invalidJSON.size){message('原始数据无效，已有卡牌未被覆盖。');return false;}
      const report=check();if(report.errors.length){message('请先修正结构错误，草稿仍保留。');persistDraft();return false;}
      const status=confirmed?'manual-confirmed':JSON.stringify(fx)!==JSON.stringify(api.getFx(card.id))?'unreviewed':card.effectStatus||'unreviewed';
      try{api.saveCard(card.id,{...patch,effects:clone(fx),effectStatus:status,compileWarnings:[]});}
      catch(e){message('保存失败：'+e.message+'，修改仍在编辑器中。');persistDraft();return false;}
      card={...card,...patch,effectStatus:status};patch={};dirty=false;localStorage.removeItem('kg.inspectorDraft');history.reset(snapshot());syncHistory();renderList();message(confirmed?'已保存并标记已验证':'已保存修改');
      try{if(g.KG_AUTO_SAVE)await g.KG_AUTO_SAVE.flush();}catch(e){message('卡牌已保存，自动备份失败：'+e.message);}return true;
    }
    function undo(redo=false){clearTimeout(historyTimer);history.push(snapshot());const next=redo?history.redo():history.undo();if(next==null)return;const v=JSON.parse(next);fx=v.fx;patch=v.patch;dirty=true;rawInvalid=false;$('insRaw').value=JSON.stringify(fx,null,2);render();check();syncHistory();persistDraft();}
    $('insSearch').oninput=renderList;
    $('insBtnCards').onclick=()=>root.classList.toggle('ins-cards-open');
    $('insBtnSave').onclick=()=>save();$('insBtnConfirm').onclick=()=>save(true);
    $('insBtnUndo').onclick=()=>undo();$('insBtnRedo').onclick=()=>undo(true);
    $('insBtnRaw').onclick=()=>{$('insRawPanel').hidden=false;$('insRaw').focus();};$('insRawClose').onclick=()=>{$('insRawPanel').hidden=true;$('insBtnRaw').focus();};
    $('insRaw').oninput=()=>{dirty=true;try{const next=JSON.parse($('insRaw').value);if(!Array.isArray(next))throw Error('效果应为列表');fx=next;rawInvalid=false;history.push(snapshot());render();}catch(e){rawInvalid=true;}check();syncHistory();persistDraft();};
    $('insBtnNew').onclick=async()=>{if(!(await leave()))return;const c=api.newCard({name:'新卡',effects:[{trigger:'deploy',actions:[]}]});await selectCard(c);};
    document.addEventListener('keydown',e=>{if(!(e.ctrlKey||e.metaKey)||/^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)||pendingDialog)return;if(e.key.toLowerCase()==='z'){e.preventDefault();undo(e.shiftKey);}else if(e.key.toLowerCase()==='y'){e.preventDefault();undo(true);}});
    g.addEventListener('kg-save-error',e=>message('自动存档失败：'+e.detail));
    g.KG_BEFORE_NAVIGATE=leave;g.KG_BEFORE_CLOSE=leave;
    renderList();render();if(api.loadImages)api.loadImages().catch(()=>{});
    try{const draft=JSON.parse(localStorage.getItem('kg.inspectorDraft'));if(draft?.cardId){const c=api.list().find(c=>c.id===draft.cardId);if(c)selectCard(c,draft);}}catch(e){message('旧草稿无法读取，卡牌数据未被修改。');}
    return {root,api,select:selectCard,current:()=>card,getEffects:()=>clone(fx),hasUnsavedChanges:()=>dirty,save,leave,history:()=>history,flushHistory:()=>{clearTimeout(historyTimer);persistDraft();return history.push(snapshot());},setNovice:()=>{},isNovice:()=>true,isTextFirst:()=>false,render,primitiveCatalog:()=>clone(C)};
  }
  g.KG_EFFECT_EDITOR={mount};
})(window);
