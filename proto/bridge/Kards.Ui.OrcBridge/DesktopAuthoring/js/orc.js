/* OrC: text -> composable effects, with explicit import diagnostics. */
(function(g){
  'use strict';
  const K=g.KG, C=K.compiler, P=g.KG_PRIMITIVES, copy=x=>JSON.parse(JSON.stringify(x));
  const version='orc-4';
  const aliases={'守护':'guard','奋战':'fury'};
  const keyWords=()=>Object.fromEntries(Object.entries(K.KEYWORDS).flatMap(([id,v])=>[[id,id],[v.cn,id]]).concat(Object.entries(aliases)));
  function number(s){
    s=String(s).trim().replace(/两/g,'二');
    if(/^[+-]?\d+$/.test(s))return Number(s);
    if(!/^[零〇一二三四五六七八九十百千]+$/.test(s))return null;
    let total=0,digit=0;const nums='零一二三四五六七八九';
    for(const ch of s){if('十百千'.includes(ch)){total+=(digit||1)*({十:10,百:100,千:1000}[ch]);digit=0;}else digit=ch==='〇'?0:nums.indexOf(ch);}
    return total+digit;
  }
  // Normalize outside quotes only: card names and literal strings remain exact.
  function normalize(text){
    let quote=null,out='';const pairs={'“':'”','「':'」','『':'』','"':'"'};
    for(const ch of String(text||'').replace(/^\uFEFF/,'').replace(/\r\n?/g,'\n')){
      if(quote){out+=ch;if(ch===quote)quote=null;continue;}
      if(pairs[ch]){quote=pairs[ch];out+=ch;continue;}
      out+=ch==='\u00a0'||ch==='\u3000'?' ':ch==='−'||ch==='–'?'-':ch>='０'&&ch<='９'?String(ch.charCodeAt(0)-65296):ch==='＋'?'+':ch==='／'?'/':ch;
    }
    // Unwrap known language tokens while keeping clause boundaries and quoted names.
    const words=['对战词条','战斗词条','相邻单位','本单位','此单位','指挥点','攻击力','防御力',...Object.keys(P.UNIT_TYPES||{}),...Object.values(K.KEYWORDS).map(k=>k.cn)];
    return out.split(/(“[^”]*”|「[^」]*」|『[^』]*』|"[^"]*")/).map((part,i)=>i%2?part:words.reduce((s,word)=>s.replace(new RegExp(word.split('').join('[ \\t]*\\n?[ \\t]*'),'g'),word),part.replace(/\\r\\n|\\n/g,'\n').split('\n').map(line=>keywordLine(line)?line.replace(/[ \t]+/g,'、'):line).join('\n')).replace(/(?<!或)(?<=[\p{Script=Han}\d+\-])[ \t]+(?=[\p{Script=Han}\d+\-])(?!或)/gu,'').replace(/[，,](?=(?:若|如果)[^。；\n，,]+[，,]改为)/g,'。')).join('').trim();
  }
  function split(text,separators){
    const parts=[];let start=0,quote=null,depth=0;const pairs={'“':'”','「':'」','『':'』','"':'"'};
    for(let i=0;i<text.length;i++){const c=text[i];if(quote){if(c===quote)quote=null;continue;}if(pairs[c]){quote=pairs[c];continue;}
      if('（(['.includes(c))depth++;else if('）)]'.includes(c))depth--;
      else if(!depth&&separators.includes(c)){parts.push(text.slice(start,i));start=i+1;}}
    parts.push(text.slice(start));return parts.map(s=>s.trim()).filter(Boolean);
  }
  const read=(path,of='candidate')=>({expr:'read',of,path});
  const compare=(left,cmp,right)=>({test:'compare',left,cmp,right});
  const attributes={'攻击力':'attack','防御力':'defense','生命值':'hq','行动花费':'opCost','操作费':'opCost','部署费用':'cost','费用':'cost','花费':'cost','剩余指挥点':'kredits','指挥点上限':'maxKredits'};
  function value(text,subject='candidate'){
    const s=String(text).trim().replace(/^等同于/,''),n=number(s);if(n!=null)return n;
    if(/^指挥点槽(?:数|数量)$/.test(s))return read('maxKredits','friendlyPlayer');
    if(/^剩余指挥点(?:数)?$/.test(s))return read('kredits','friendlyPlayer');
    if(/^手牌(?:数|数量)$/.test(s))return {expr:'count',query:{sel:'all',side:'friendly',zone:'hand'}};
    const product=s.match(/^(.+?)的([\d一二两三四五六七八九十百]+)倍$/);
    if(product){const x=value(product[1],subject),times=number(product[2]);if(x!=null&&times!=null)return {expr:'math',operator:'multiply',items:[x,times]};}
    const sum=s.match(/^(.+?)(?:加上|加|减去|减)([\d一二两三四五六七八九十百]+)$/);
    if(sum){const x=value(sum[1]);if(x!=null)return {expr:'math',operator:/减/.test(sum[0].slice(sum[1].length))?'subtract':'add',items:[x,number(sum[2])]};}
    const range=s.match(/^(\d+)-(\d+)$/);if(range)return {expr:'random',min:Number(range[1]),max:Number(range[2])};
    const count=s.match(/^(友方|我方|敌方)(.+?)(?:数量|数)$/);if(count&&!/指挥点|手牌|卡组|弃牌/.test(count[2])){const q=target('所有'+count[1]+count[2],{targets:[]});if(q)return {expr:'count',query:q};}
    const m=s.match(/^(本单位|此单位|其|友方总部|我方总部|敌方总部|友方|我方|敌方)(?:的)?(.+)$/);
    if(m){const of=m[1]==='其'?subject:/单位/.test(m[1])?'self':/敌/.test(m[1])?'enemyPlayer':'friendlyPlayer';
      if(attributes[m[2]])return read(attributes[m[2]],of);
      if(m[2]==='指挥点槽数'||m[2]==='指挥点槽数量')return read('maxKredits',of);
      const zones={'手牌数量':'hand','手牌数':'hand','卡组数量':'deck','卡组牌数':'deck','弃牌数量':'discard'};
      if(zones[m[2]])return {expr:'count',query:{sel:'all',side:/敌/.test(m[1])?'enemy':'friendly',zone:zones[m[2]]}};
    }
    return null;
  }
  function predicate(text,subject='candidate'){
    let s=text.trim().replace(/^(如果|若)/,'').replace(/[，,]$/,'');
    for(const [word,test] of [['或者','any'],['或','any'],['并且','all'],['且','all']]){
      if(s.includes(word)){const items=s.split(word).map(x=>predicate(x,subject));if(items.every(Boolean))return {test,items};return null;}}
    if(/^未受伤$/.test(s))return compare(read('damaged',subject),'==',false);
    if(/^(已)?受伤$/.test(s))return compare(read('damaged',subject),'==',true);
    if(/^(其)?(?:已被)?压制$/.test(s))return compare(read('pinned',subject),'==',true);
    if(/^(将其|其被)消灭$/.test(s))return compare(read('alive',subject),'==',false);
    if(/^(?:其)?(?:是)?友方单位$/.test(s))return compare(read('owner',subject),'==',read('owner','friendlyPlayer'));
    const national=s.match(/^(?:其)?(?:是)?(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团)牌$/);if(national)return compare(read('set',subject),'==',national[1]);
    if(/^(其)?(?:不在|在)前线$/.test(s)){const p=compare(read('zone',subject),'==','frontline');return s.includes('不在')?{test:'not',item:p}:p;}
    if(/^(友方|我方|敌方)回合中$/.test(s))return compare(read('active','state'),'==',read('owner',s.startsWith('敌方')?'enemyPlayer':'friendlyPlayer'));
    if(/^(其)?(?:没有|有)相邻单位$/.test(s))return compare({expr:'count',query:{sel:'adjacent',ref:subject}},s.includes('没有')?'==':'>',0);
    if(/^(没有|有)/.test(s)){const q=target('所有'+s.replace(/^(没有|有)/,''),{targets:[]});if(q)return compare({expr:'count',query:q},s.startsWith('没有')?'==':'>',0);}
    const type=s.match(/^(?:其)?(?:是)?(步兵|坦克|炮兵|火炮|空军|陆军|战斗机|轰炸机|单位)$/);if(type)return type[1]==='单位'?compare(read('cardType',subject),'==','unit'):unitPredicate(type[1],subject);
    const combat=s.match(/^(?:(?:其)?(?:具有|有))?(?:(不小于|至少)(\d+)个|没有|无)(?:对战|战斗)词条$/);
    if(combat)return compare({expr:'size',value:{expr:'intersection',items:[read('keywords',subject),{expr:'literal',value:K.COMBAT_KEYWORDS}]}},combat[2]?'>=':'==',Number(combat[2]||0));
    s=s.replace(/^其(?:的)?(?=攻击力|防御力|行动花费)/,'');
    const kw=s.match(/^(不具有|没有|具有|拥有)(.+)$/);
    if(kw&&keyWords()[kw[2]]){const p=compare(read('keywords',subject),'contains',keyWords()[kw[2]]);return /不|没有/.test(kw[1])?{test:'not',item:p}:p;}
    const m=s.match(/^(.+?)(不大于|不小于|大于等于|小于等于|等于|大于|小于|至少|至多|为|>=|<=|==|>|<)(.+)$/);
    if(!m)return null;const left=attributes[m[1]]?read(attributes[m[1]],subject):value(m[1],subject),right=value(m[3],subject);
    if(left==null||right==null)return null;
    const cmp={'不大于':'<=','不小于':'>=','大于等于':'>=','小于等于':'<=','等于':'==','为':'==','大于':'>','小于':'<','至少':'>=','至多':'<='}[m[2]]||m[2];return compare(left,cmp,right);
  }
  function unitPredicate(s,subject='candidate'){
    s=s.trim().replace(/的$/,'');const items=[];
    const types=Object.entries(P.UNIT_TYPES||{}).sort((a,b)=>b[0].length-a[0].length);
    const type=types.find(([cn,id])=>s.endsWith(cn)&&!['nonspace','space'].includes(id));
    if(type){s=s.slice(0,-type[0].length);const ids=Array.isArray(type[1])?type[1]:[type[1]];items.push({test:'any',items:ids.flatMap(id=>[compare(read('unitType',subject),'==',id),compare(read('extraTypes',subject),'contains',id)])});}
    else if(s.endsWith('单位'))s=s.slice(0,-2);else return null;
    const countries=s.match(/(非)?(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团)(?:和(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团))?$/);
    if(countries){const p={test:'any',items:[countries[2],countries[3]].filter(Boolean).map(n=>compare(read('set',subject),'==',n))};items.push(countries[1]?{test:'not',item:p}:p);s=s.slice(0,-countries[0].length);}
    s=s.replace(/的$/,'');if(s){const kw=keyword(s);const p=kw?compare(read('keywords',subject),'contains',kw.id):predicate(s,subject);if(!p)return null;items.push(p);}
    return {test:'all',items};
  }
  function cardPredicate(s,subject='candidate'){
    s=s.trim().replace(/^的/,'').replace(/的$/,'');const items=[];
    const cost=s.match(/^花费(不大于|不小于|大于|小于|等于|为)(\d+)(?:的)?/);
    if(cost){const p=predicate(cost[0].replace(/的$/,''),subject);if(!p)return null;items.push(p);s=s.slice(cost[0].length);}
    const country=s.match(/^(英国|美国|德国|苏联|日本|法国|意大利|波兰|芬兰|澳新军团)/);
    if(country){items.push(compare(read('set',subject),'==',country[1]));s=s.slice(country[0].length);}
    const rarity=s.match(/^(精英|特殊|限定|普通)/);if(rarity){items.push(compare(read('rarity',subject),'==',({精英:'gold',特殊:'silver',限定:'bronze',普通:'iron'})[rarity[1]]));s=s.slice(rarity[0].length);}
    s=s.replace(/^的/,'');
    if(/^[“「"].+[”」"]$/.test(s))items.push(compare(read('name',subject),'==',s.slice(1,-1)));
    else if(s==='指令'||s==='反制')items.push(compare(read('cardType',subject),'==',s==='指令'?'order':'counter'));
    else if(s==='非单位')items.push(compare(read('cardType',subject),'!=','unit'));
    else if(s&&!['牌','卡牌','手牌'].includes(s)){const unit=unitPredicate(s,subject);if(!unit)return null;items.push(compare(read('cardType',subject),'==','unit'),unit);}
    return {test:'all',items};
  }
  function target(text,fx){
    let s=text.trim();
    if(s==='最左侧和最右侧手牌')return {sel:'union',queries:[{sel:'first',zone:'hand',side:'friendly',count:1},{sel:'last',zone:'hand',side:'friendly',count:1}]};
    const joined=split(s,'与和及');if(joined.length>1&&(joined.some(x=>/^(友方|我方|敌方|对方)总部$/.test(x))||joined.every(x=>/^(其|该单位|本单位|此单位)$/.test(x)))){const queries=joined.map(x=>target(x,fx));return queries.every(Boolean)?{sel:'union',queries}:null;}
    const neighbors=s.match(/^(.+?)(?:及其|和其|与其)(?:左右)?相邻(?:的)?单位$/);
    if(neighbors){const anchor=target(neighbors[1],fx);if(!anchor)return null;const ref='orcAnchor'+(fx._serial=(fx._serial||0)+1);(fx._pending||(fx._pending=[])).push({op:'let',name:ref,value:{expr:'query',query:anchor}});return {sel:'adjacent',ref,includeAnchor:true};}
    const adjacent=s.match(/^(?:与)?(?:(本单位|此单位|其|该单位)(?:左右)?)?相邻(?:的)?(.+)$/);
    if(adjacent){const anchor=target(adjacent[1]||'本单位',fx);if(!anchor)return null;const where=unitPredicate(adjacent[2]);if(!where)return null;const ref='orcAnchor'+(fx._serial=(fx._serial||0)+1);(fx._pending||(fx._pending=[])).push({op:'let',name:ref,value:{expr:'query',query:anchor}});return {sel:'adjacent',ref,where};}
    if(/^(本单位|此单位)$/.test(s))return {sel:'self'};
    if(/^(其|该单位|该牌|此牌)$/.test(s))return fx._lastTarget?copy(fx._lastTarget):null;
    const hand=s.match(/^(?:(所有|全部|随机|最左侧|最右侧))?(友方|我方|敌方|对手)?(?:手牌|手中)(?:中|的)?(?:的)?(?:所有|全部)?(.*)$/);
    if(hand){const descriptor=hand[3].replace(/^的/,''),where=descriptor?cardPredicate(descriptor):null;if(descriptor&&!where)return null;const q={sel:hand[1]==='随机'?'random':hand[1]==='最左侧'?'first':hand[1]==='最右侧'?'last':'all',side:/敌|对手/.test(hand[2]||'')?'enemy':'friendly',zone:'hand',...(hand[1]&&hand[1]!=='所有'&&hand[1]!=='全部'?{count:1}:{}),...(where?{where}:{})};fx._lastTarget=copy(q);return q;}
    const side=/敌方|对方/.test(s)?'enemy':/友方|我方/.test(s)?'friendly':'both';
    if(/^(友方|我方|敌方|对方|双方)总部$/.test(s))return fx._lastTarget={sel:'all',side,zone:'hq'};
    const anyTarget=s.endsWith('目标');if(anyTarget)s=s.replace(/目标$/,'单位');
    const random=s.includes('随机'),all=/所有|全部|其他/.test(s)||!/(?:\d+|一)(?:个|辆|架|支)/.test(s)&&side!=='both',other=s.includes('其他'),zone=s.includes('前线')?'frontline':s.includes('支援')?'support':'field';
    s=s.replace(/(?:所有|全部|随机|其他|一个|一辆|一架|一支|1个|1辆|1架|1支|敌方|对方|友方|我方|前线|支援阵线|支援线|场上)/g,'').trim();
    const where=unitPredicate(s);if(!where)return null;
    let q;if(all||random)q={sel:random?'random':'all',side,zone,...(random?{count:1}:{}),...(other?{excludeSelf:true}:{}),...(where?{where}:{})};
    else {const id=(fx._targetPrefix||'orcTarget')+(fx.targets.length+1);fx.targets.push({id,kind:anyTarget?'any':'unit',side:side==='both'?'any':side,zone,...(where.items.length?{filter:{where}}:{}),prompt:anyTarget?'选择一个目标':'选择一个单位'});q={sel:'refAll',ref:id};}
    if(anyTarget&&all&&!random)q={sel:'union',queries:[q,{sel:'all',side,zone:'hq'}]};
    if(anyTarget&&random)return null;
    if(random){const ref='orcRandom'+(fx._serial=(fx._serial||0)+1);(fx._pending||(fx._pending=[])).push({op:'let',name:ref,value:{expr:'query',query:q}});q={sel:'refAll',ref};}
    fx._lastTarget=copy(q);return q;
  }
  function action(text,fx,registry){
    let s=text.trim().replace(/^(然后|并且|并|则)/,'').trim(),m;
    if((m=s.match(/^抉择[：:](.+)$/))){
      const body=m[1],parts=split(body.replace(/[ \t]+或[ \t]+/g,'\u0001'),body.includes(' 或 ')?'\u0001':'或');if(parts.length<2)return null;
      const first=parts[0],last=parts.at(-1),verb=first.match(/^(随机将|将|使|对|抽)(?:所有|全部)?/),suffix=last.match(/((?:加入|添加到)(?:手牌|手中|支援阵线|前线)|造成.+点伤害|洗入卡组)$/);
      const options=[];for(let i=0;i<parts.length;i++){
        let clause=parts[i];if(suffix&&!/(?:加入|添加到|造成|洗入)/.test(clause))clause+=suffix[1];
        if(i&&verb&&!/^(?:将|使|对|抽|消灭|压制|移除|开发|研发)/.test(clause)){
          if(/^(?:获得|具有|移至)/.test(clause)){const shared=first.match(/^(使.+?)(?:获得|具有)/);if(!shared)return null;clause=shared[1]+clause;}
          else {const gain=first.match(/^(.+?(?:获得|具有))(.+)$/);clause=gain&&!suffix?gain[1]+clause:verb[0]+clause;}
        }
        const local={targets:[],_serial:fx._serial||0,_targetPrefix:'orcOption'+(fx._serial=(fx._serial||0)+1)+'Target',...(fx._lastTarget?{_lastTarget:copy(fx._lastTarget)}:{})};
        const actions=actionsFor(clause,local,registry);if(!actions)return null;fx._serial=Math.max(fx._serial,local._serial||0);options.push({label:clause,actions, ...(local.targets.length?{targets:local.targets}:{})});
      }return [{op:'choice',prompt:'抉择',options}];
    }
    s=s.replace(/^将(其|该单位)(消灭|压制|抑制)$/,'$2$1');
    s=s.replace(/^将(其|该牌|此牌)弃掉$/,'弃掉$1');
    s=s.replace(/^随机(消灭|压制|抑制)(.+)$/,'$1随机$2');
    if(/^造成/.test(s)&&fx._lastTarget)s='对其'+s;
    const permanentMod=({无法攻击敌方总部:'noAttackHQ',无法攻击总部:'noAttackHQ',无法攻击空军:'noAttackAir',无法被压制:'noPin',无法被抑制:'noSuppress',无法撤退:'noRetreat'})[s.replace(/^本单位/,'')];
    if(permanentMod)return [{op:'setModifier',target:{sel:'self'},property:permanentMod,value:true}];
    if((m=s.match(/^(?:使(.+?))?与(.+?)战斗$/))){const q=target(m[1]||'本单位',fx),withTarget=target(m[2],fx);return q&&withTarget?[{op:'battleUnits',target:q,with:withTarget}]:null;}
    if((m=s.match(/^(?:花费)?(?:至少为|至少是)(\d+)$/))){if(!fx._lastTarget)return null;return [{op:'changeAttribute',target:copy(fx._lastTarget),property:'cost',mode:'set',value:{expr:'math',operator:'max',items:[Number(m[1]),read('cost')]}}];}
    if((m=s.match(/^(?:本单位)?(?:对(.+?))?造成双倍伤害$/))){
      if(!m[1])return [{op:'setModifier',target:{sel:'self'},property:'damageDouble',value:true}];
      if(m[1]==='前线单位')return [{op:'setModifier',target:{sel:'self'},property:'doubleDamageFrontline',value:true}];
      const types=split(m[1],'和、'),vsType={},actions=[];
      for(const cn of types){if(cn==='总部'){actions.push({op:'setModifier',target:{sel:'self'},property:'vsHq',value:{expr:'literal',value:{double:true}}});continue;}const ids=P.UNIT_TYPES[cn];if(!ids)return null;for(const id of Array.isArray(ids)?ids:[ids])vsType[id]={double:true};}
      if(Object.keys(vsType).length)actions.unshift({op:'setModifier',target:{sel:'self'},property:'vsType',value:{expr:'literal',value:vsType}});return actions;
    }
    if((m=s.match(/^选择(?:手牌中的|并弃(?:掉)?)?(?:1|一)张(.+?)$/))){
      const discard=/选择并弃/.test(s),descriptor=m[1]==='牌'?'手牌':m[1],q=/^(?:友方|我方|敌方)?(?:手牌|手中)/.test(descriptor)?target(descriptor,fx):{sel:'all',side:'friendly',zone:'hand',where:cardPredicate(descriptor.replace(/(?:的)?手牌$/,''))};
      if(!q||q.where===null)return null;const name='orcChosen'+(fx._serial=(fx._serial||0)+1);fx._lastTarget={sel:'refAll',ref:name};return [{op:'selectObjects',target:q,as:name,count:1,prompt:'选择一张手牌'},...(discard?[{op:'discardOne',target:copy(fx._lastTarget)}]:[])];
    }
    if((m=s.match(/^(?:随机将(?:1|一)张|从(?:3|三)张随机)(.+?)(?:中选择(?:1|一)张)?加入(?:手牌|手中)$/))){
      const where=cardPredicate(m[1]);if(!where)return null;const ref='orcGenerated'+(fx._serial=(fx._serial||0)+1),discover=s.startsWith('从'),query={sel:'random',zone:'pool',count:discover?3:1,where:{test:'all',items:[where,compare(read('token'),'!=',true),compare(read('reserved'),'!=',true)]}};
      fx._lastTarget={sel:'refAll',ref};if(discover)return [{op:'threeChoice',mode:'random',pool:{source:'pool',where},as:ref}];return [{op:'let',name:ref,value:{expr:'query',query}},{op:'createCard',cardId:read('id',ref),as:ref}];
    }
    if((m=s.match(/^(?:从(卡组|手牌|弃牌区)(?:中|里)?)?(?:开发|研发)(?:1|一)张(.+)$/))){const description=m[2],where=/^(?:牌|卡牌)$/.test(description)?null:cardPredicate(description);if(!where&&!/^(?:牌|卡牌)$/.test(description))return null;let pool={source:({卡组:'deck',手牌:'hand',弃牌区:'discard'})[m[1]]||'pool',...(where?{where}:{})};if(!m[1]&&/^[“「"].+[”」"]$/.test(description)){const hits=registry.filter(c=>c.name===description.slice(1,-1)||c.id===description.slice(1,-1));if(hits.length!==1)return null;pool={source:'cards',cardIds:[hits[0].id]};}const name='orcDeveloped'+(fx._serial=(fx._serial||0)+1);fx._lastTarget={sel:'refAll',ref:name};return [{op:'developCard',pool,as:name}];}
    if((m=s.match(/^(?:将|使)?(.+?)(?:洗入(?:卡组|牌库)|(?:返回|放回)(?:卡组|牌库)(顶|底)?)$/))){const q=target(m[1],fx);if(!q)return null;return [{op:'moveCard',target:q,to:'deck',position:/洗入/.test(s)?'shuffle':m[2]==='底'?'bottom':'top'}];}
    if((m=s.match(/^(?:将|使)?(.+?)移至(最左侧|最右侧)$/))){const q=target(m[1],fx);return q?[{op:'moveCard',target:q,to:'hand',position:m[2]==='最左侧'?'left':'right'}]:null;}
    if((m=s.match(/^(?:(友方|我方|敌方|对手))?(随机)?弃(?:掉)?(.+)$/))){
      let q=/^(其|该牌|此牌)$/.test(m[3])?target(m[3],fx):null;
      if(!q){let rest=m[3],sel=m[2]?'random':'all',count;const n=rest.match(/^([\d一二两三四五六七八九十]+)张(.+)$/);if(n){sel='random';count=number(n[1]);rest=n[2];}if(/^最左侧/.test(rest)){sel='first';count=1;rest=rest.replace(/^最左侧/,'');}if(/^最右侧/.test(rest)){sel='last';count=1;rest=rest.replace(/^最右侧/,'');}rest=rest.replace(/^(所有|全部)/,'').replace(/^手牌(?:中|的)?/,'');const where=/^(牌|卡牌|手牌)?$/.test(rest)?null:cardPredicate(rest);if(rest&&!/^(牌|卡牌|手牌)$/.test(rest)&&!where)return null;q={sel,side:/敌|对手/.test(m[1]||'')?'enemy':'friendly',zone:'hand',...(count!=null?{count}:{}),...(where?{where}:{})};}
      const name='orcDiscarded'+(fx._serial=(fx._serial||0)+1);fx._lastTarget={sel:'refAll',ref:name};return [{op:'let',name,value:{expr:'query',query:q}},{op:'discardOne',target:copy(fx._lastTarget)}];
    }
    if((m=s.match(/^(?:使)?(.+?)(?:获得|具有)[：:]?[“「"](.+)[”」"]$/))){
      const q=target(m[1],fx);if(!q)return null;
      const nested=compile(m[2],{cardType:'unit'},{pool:registry});if(!nested.complete||Object.keys(nested.cardFields).length||nested.keywords.length)return null;
      return nested.effects.map(effect=>({op:'bindEvent',target:copy(q),effect}));
    }
    if((m=s.match(/^(其|该单位|本单位|此单位)每(?:具有|拥有)(?:1|一)(?:个|种)?(对战词条|战斗词条|词条|关键词)[，,](.+)$/))){
      const q=target(m[1],fx);if(!q)return null;const previous=fx._lastTarget,of=q.sel==='self'?'self':'orcEach'+(fx._serial=(fx._serial||0)+1);
      if(q.sel!=='self')fx._lastTarget={sel:'refAll',ref:of};
      const list=m[2]==='词条'||m[2]==='关键词'?read('keywords',of):{expr:'intersection',items:[read('keywords',of),{expr:'literal',value:K.COMBAT_KEYWORDS}]};
      const actions=actionsFor(m[3],fx,registry);fx._lastTarget=previous;const loop={op:'loop',times:{expr:'size',value:list},actions};return actions?[q.sel==='self'?loop:{op:'iterate',target:q,as:of,actions:[loop]}]:null;
    }
    if((m=s.match(/^(?:(友方|我方|敌方|对手)(?:玩家)?)?抽(?:取)?([\d一二两三四五六七八九十百]+)(?:张|个)?(.+?)(?:牌)?$/))&&m[3]!=='牌'&&m[3]!=='卡牌'){
      const n=number(m[2]),units=unitPredicate(m[3]),where=['指令','反制'].includes(m[3])?compare(read('cardType'),'==',m[3]==='指令'?'order':'counter'):units?{test:'all',items:[compare(read('cardType'),'==','unit'),units]}:null;
      if(where&&n>=0&&n<=100){const side=/敌|对手/.test(m[1]||'')?'enemy':'self',actions=[],names=[];for(let i=0;i<n;i++){const name='orcDrawn'+(fx._serial=(fx._serial||0)+1);names.push(name);actions.push({op:'drawOne',side,target:{sel:'all',side:side==='enemy'?'enemy':'friendly',zone:'deck',where},as:name});}
        const name='orcDrawnGroup'+(fx._serial=(fx._serial||0)+1);actions.push({op:'let',name,value:{expr:'union',items:names.map(name=>({expr:'variable',name}))}});fx._lastTarget={sel:'refAll',ref:name};return actions;}
    }
    if((m=s.match(/^每有(?:一个|一辆|一架|一支|1个)(.+?)[，,](.+)$/))){const q=target(m[1].startsWith('相邻')?m[1]:'所有'+m[1],fx),pre=fx._pending||[];fx._pending=[];const actions=actionsFor(m[2],fx,registry);if(q&&actions)return [...pre,{op:'loop',times:{expr:'count',query:q},actions}];return null;}
    const cond=s.match(/^(?:如果|若)(.+?)[，,](.+)$/);
    if(cond){const test=predicate(cond[1],fx._lastTarget?.ref||'candidate'),actions=actionsFor(cond[2],fx,registry);if(test&&actions)return [{op:'branch',test,then:actions,else:[]}];return null;}
    if((m=s.match(/^(?:(友方|我方|敌方|对手)(?:玩家)?)?抽(?:取)?(.+?)张(?:卡)?牌$/))){const count=value(m[2]),side=/敌|对手/.test(m[1]||'')?'enemy':'self';if(count==null)return null;
      if(Number.isInteger(count)&&count>=0&&count<=100){const actions=[],items=[];for(let i=0;i<count;i++){const as='orcDrawn'+(fx._serial=(fx._serial||0)+1);actions.push({op:'drawOne',side,as});items.push({expr:'variable',name:as});}const name='orcDrawnGroup'+(fx._serial=(fx._serial||0)+1);actions.push({op:'let',name,value:{expr:'union',items}});fx._lastTarget={sel:'refAll',ref:name};return actions;}
      return [{op:'loop',times:count,actions:[{op:'drawOne',side}]}];}
    if((m=s.match(/^(?:使)?(友方|我方|敌方|对手)?(?:获得|增加|失去|减少)(.+?)(?:个|点)?(?:临时)?指挥点(槽)?$/))){const n=value(m[2]);if(n==null)return null;return [{op:'changeResource',side:/敌|对手/.test(m[1]||'')?'enemy':'self',property:m[3]?'maxKredits':'kredits',mode:'add',value:/失去|减少/.test(s)?{expr:'math',operator:'multiply',items:[-1,n]}:n}];}
    if((m=s.match(/^对(.+?)造成(.+?)点伤害$/))){const q=target(m[1],fx),n=value(m[2],q?.ref||'candidate');return q&&n!=null?[{op:'dealDamage',target:q,amount:n}]:null;}
    if((m=s.match(/^(?:使)?(.+?)(攻击力|防御力|花费)(?:等同于|等于)(.+)$/))){const q=target(m[1].replace(/的$/,''),fx),n=value(m[3],q?.ref||'candidate');return q&&n!=null?[{op:'changeAttribute',target:q,property:attributes[m[2]],mode:'set',value:n}]:null;}
    if((m=s.match(/^(?:为|使)(.+?)恢复(.+?)点(?:防御力|生命值)$/))){const q=target(m[1],fx),n=value(m[2]);return q&&n!=null?[{op:'restoreHealth',target:q,amount:n}]:null;}
    if((m=s.match(/^(消灭|压制|解除压制|抑制|移除)(.+)$/))){const q=target(m[2],fx);return q?[{op:({消灭:'destroyUnit',压制:'setPinned',解除压制:'setPinned',抑制:'suppressUnit',移除:'leaveUnit'})[m[1]],target:q,...(/压制/.test(m[1])?{enabled:m[1]==='压制',turns:1}:{})}]:null;}
    if((m=s.match(/^(?:将|使)?(.+?)(?:返回|送回)(?:手牌|手中)$/))){const q=target(m[1],fx);return q?[{op:'returnUnit',target:q}]:null;}
    if((m=s.match(/^(?:使)?(.+?)撤退$/))){const q=target(m[1],fx);return q?[{op:'returnUnit',target:q}]:null;}
    if((m=s.match(/^(?:使)?(.+?)?(?:移至|移到)(前线|支援阵线)$/))){const q=target(m[1]||'本单位',fx);return q?[{op:'moveUnit',target:q,to:m[2]==='前线'?'frontline':'support'}]:null;}
    if((m=s.match(/^指向(.+)$/))){const q=target(m[1],fx);if(q)return [{op:'let',name:'orcSelected'+(fx._serial=(fx._serial||0)+1),value:{expr:'query',query:q}}];return null;}
    const temporary=/(?:本回合(?:内)?|直到回合结束)$/.test(s)||/^本回合[，,]/.test(s);if(temporary)s=s.replace(/^本回合[，,]/,'').replace(/(?:，|,)?(?:本回合(?:内)?|直到回合结束)$/,'').trim();
    if(/^(?:获得|具有|得到)(?:一次)?(?:[+-]|闪击|伏击|奋战|烟幕|冲击|守护|固守|动员|打捞|重甲)/.test(s))s='本单位'+s;
    s=s.replace(/(?:具有|得到)(?=(?:一次)?[+-]|闪击|伏击|奋战|烟幕|冲击|守护|固守|动员|打捞|重甲)/,'获得').replace(/获得一次(?=[+-])/,'获得');
    if((m=s.match(/^(?:使)?(.+?)(?:获得)(.+)$/))){
      const benefits=split(m[2],'和、');if(benefits.length>1||/^[+-]\d+(?:攻击力|防御力|行动花费|操作费|花费)$/.test(m[2])){
        const q=target(m[1],fx);if(!q)return null;const ref='orcRecipients'+(fx._serial=(fx._serial||0)+1),actions=[{op:'let',name:ref,value:{expr:'query',query:q}}];
        for(const benefit of benefits){const attr=benefit.match(/^([+-]\d+)(攻击力|防御力|行动花费|操作费|花费)$/),kw=keyword(benefit);
          if(attr)actions.push({op:'changeAttribute',target:{sel:'refAll',ref},property:attributes[attr[2]],mode:'add',value:Number(attr[1]),...(temporary?{duration:'turn'}:{})});
          else if(kw)actions.push({op:'setKeyword',target:{sel:'refAll',ref},keyword:kw.id,enabled:true,value:kw.value,...(temporary?{duration:'turn'}:{})});
          else return null;
        }return actions;
      }
    }
    if((m=s.match(/^(?:使)?(.+?)(?:获得|得到)([+-]\d+)\s*(?:\/|(?=[+-]))\s*([+-]?\d+)$/))){const q=target(m[1],fx);if(!q)return null;const ref='orcRecipients'+(fx._serial=(fx._serial||0)+1);return [{op:'let',name:ref,value:{expr:'query',query:q}},...['attack','defense'].map((property,i)=>({op:'changeAttribute',target:{sel:'refAll',ref},property,mode:'add',value:Number(m[i+2]),...(temporary?{duration:'turn'}:{})}))];}
    if((m=s.match(/^(?:使)?(.+?)(?:获得|得到)(.+)$/))){const q=target(m[1],fx),kw=keyword(m[2]);return q&&kw?[{op:'setKeyword',target:q,keyword:kw.id,enabled:true,value:kw.value,...(temporary?{duration:'turn'}:{})}]:null;}
    if((m=s.match(/^(?:使)?(.+?)(?:的)?(攻击力|防御力|操作费|行动花费|花费)(增加|减少|变为|设为|为)(.+)$/))){const q=target(m[1],fx),n=value(m[4],fx._lastTarget?.ref||'candidate');if(!q||n==null)return null;return [{op:'changeAttribute',target:q,property:attributes[m[2]],mode:/变为|设为|^为$/.test(m[3])?'set':'add',value:m[3]==='减少'?{expr:'math',operator:'multiply',items:[-1,n]}:n,...(temporary?{duration:'turn'}:{})}];}
    if((m=s.match(/^(?:将|把)([\d一二两三四五六七八九十百]+)?(?:张|个|辆|架)?[“「"](.+?)[”」"](?:加入|添加到)(友方|我方|敌方)?(手牌|手中|支援阵线|前线)$/))){const matches=registry.filter(c=>c.name===m[2]||c.id===m[2]),n=number(m[1]||'1');if(matches.length!==1||n==null||n>100)return null;const a={op:/手/.test(m[4])?'createCard':'createUnit',cardId:matches[0].id,side:m[3]==='敌方'?'enemy':'self',...(/手/.test(m[4])?{}:{to:m[4]==='前线'?'frontline':'support'})};if(a.op!=='createCard')return n===1?[a]:[{op:'loop',times:n,actions:[a]}];const actions=[],items=[];for(let i=0;i<n;i++){const as='orcCreated'+(fx._serial=(fx._serial||0)+1);actions.push({...a,as});items.push({expr:'variable',name:as});}const name='orcCreatedGroup'+(fx._serial=(fx._serial||0)+1);actions.push({op:'let',name,value:{expr:'union',items}});fx._lastTarget={sel:'refAll',ref:name};return actions;}
    return null;
  }
  function actionsFor(s,fx,registry){
    // A conditional comma belongs to its branch; never turn it into an unconditional sibling.
    if(/^(抉择[：:]|如果|若|每有|(?:其|该单位|本单位|此单位)每)/.test(s))return action(s,fx,registry);
    s=s.replace(/([，,])(?=(?:其|该单位|本单位|此单位)每(?:具有|拥有))/,'\u0001');
    if(s.includes('\u0001')){const at=s.indexOf('\u0001'),before=actionsFor(s.slice(0,at),fx,registry),after=before&&actionsFor(s.slice(at+1),fx,registry);return before&&after?before.concat(after):null;}
    const list=split(s.replace(/[，,](?=本回合(?:内)?$)/,'').replace(/并将其/,'，将其'),'，,');const out=[];for(const part of list){const a=action(part,fx,registry);if(!a)return null;out.push(...(fx._pending||[]),...a);fx._pending=[];}return out;
  }
  function keyword(s){
    const m=s.trim().match(/^(.+?)(?:\s*[xX×]?\s*([\d零〇一二两三四五六七八九十百]+))?$/);if(!m)return null;
    const id=keyWords()[m[1].trim()];if(!id)return null;return {id,value:m[2]?number(m[2]):1,quantified:!!m[2]};
  }
  function keywordLine(s){const items=split(s,'、,，;；').flatMap(x=>x.split(/\s+/)).filter(Boolean).map(keyword);return items.length&&items.every(Boolean)?items:null;}
  function basicParse(text,card,registry){
    const effects=[];let fx=null;
    for(const line of split(text,'\n。；;')){const head=line.match(/^(部署|亡计|遗言|使用时|己方回合开始时|己方回合结束时|友方回合开始时|友方回合结束时)[：:,，]?\s*(.+)$/);
      if(head){fx={trigger:({部署:'deploy',亡计:'death',遗言:'death',使用时:'play',己方回合开始时:'turnStart',友方回合开始时:'turnStart',己方回合结束时:'turnEnd',友方回合结束时:'turnEnd'})[head[1]],actions:[],targets:[]};effects.push(fx);}
      let body=head?head[2]:line;
      if(!head&&card.cardType==='unit'){
        const selfEvent=line.match(/^(?:本单位)?(攻击时|被攻击时|受到伤害时|移至前线时|攻击后)[，,](.+)$/);
        const listener=line.match(/^(其他)?(友方|敌方)(.+?)(部署时|被消灭时|受到伤害时)[，,](.+)$/);
        if(selfEvent){fx={trigger:({攻击时:'attack',被攻击时:'attacked',受到伤害时:'damaged',移至前线时:'mobilize',攻击后:'afterAttack'})[selfEvent[1]],selfOnly:true,actions:[],targets:[],_lastTarget:{sel:'self'}};effects.push(fx);body=selfEvent[2];}
        else if(listener){const where=unitPredicate(listener[3]);if(!where)return null;fx={trigger:({部署时:'unitDeployed',被消灭时:'friendlyDeath',受到伤害时:'friendlyDamaged'})[listener[4]],...(listener[2]==='敌方'?{watchOpponent:true}:{}),filter:{where},actions:[],targets:[],_lastTarget:{sel:'refAll',ref:listener[4]==='受到伤害时'?'eventVictim':'eventUnit'}};
          if(listener[1])fx.condition={op:'predicate',test:compare(read('uid','eventUnit'),'!=',read('uid','self'))};effects.push(fx);body=listener[5];}
        else if((/^(.+?)(?:时|中)[，,]具有/.test(line)&&!/^若|如果/.test(line))){const c=line.match(/^(.+?)(?:时|(?<=回合)中)[，,](具有.+)$/),condition=c&&predicate(c[1]+(c[1].endsWith('回合')?'中':''),'self');if(!condition)return null;fx={trigger:'passive',condition:{op:'predicate',test:condition},actions:[],targets:[]};effects.push(fx);body=c[2];}
        else if(/具有/.test(line)&&!line.includes('时')&&!/^若|如果/.test(line)||/^(?:本单位)?无法(?:攻击|被压制|被抑制|撤退)/.test(line)||/造成双倍伤害$/.test(line)){fx={trigger:'passive',actions:[],targets:[]};effects.push(fx);body=line;}
      }
      if(!fx){if(card.cardType==='counter')return null;fx={trigger:card.cardType==='order'?'order':'deploy',actions:[],targets:[]};effects.push(fx);}
      const replacement=body.match(/^(?:若|如果)(.+?)[，,]改为(.+)$/);
      const already=body.match(/^(?:若|如果)(其已被压制)[，,](将其消灭)$/);
      if((replacement||already)&&fx._lastClauseStart!=null){
        const m=replacement||already,subject=fx._lastTarget?.ref||'self',condition=predicate(m[1],subject);
        const start=fx._lastClauseStart,previous=fx.actions.splice(start);let at=0;while(previous[at]?.op==='let')at++;
        if(already&&!(previous.length===1&&previous[0].op==='setPinned'&&previous[0].enabled))return null;
        const alternative=actionsFor(m[2],fx,registry);if(!condition||!alternative)return null;
        fx.actions.push(...previous.slice(0,at),{op:'branch',test:condition,then:alternative,else:previous.slice(at)});continue;
      }
      const actions=actionsFor(body,fx,registry);if(!actions)return null;fx._lastClauseStart=fx.actions.length;fx.actions.push(...actions);
    }
    return effects.map(({_lastTarget,_serial,_pending,_lastClauseStart,...e})=>e);
  }
  function lower(a,registry){
    const only=keys=>Object.keys(a).every(k=>['op',...keys].includes(k));
    const resource={gainKredits:['kredits',1],loseKredits:['kredits',-1],gainKreditSlot:['maxKredits',1],loseKreditSlots:['maxKredits',-1]};
    if(resource[a.op]&&only(['side','amount'])){const [property,sign]=resource[a.op],amount=a.amount??(a.op==='gainKredits'?0:1);return [{op:'changeResource',property,mode:'add',side:a.side||(sign<0?'enemy':'self'),value:sign<0?{expr:'math',operator:'multiply',items:[-1,amount]}:amount}];}
    const unit={pin:'setPinned',unpin:'setPinned',silence:'suppressUnit',removeUnit:'leaveUnit',takeControl:'transferControl',withdraw:'returnUnit'};
    if(unit[a.op]&&a.target!=null&&only(['target',...(a.op==='pin'?['turns']:[])]))return [{op:unit[a.op],target:copy(a.target),...(/pin/.test(a.op)?{enabled:a.op==='pin',turns:a.turns??1}:{})}];
    if(['opCostMod','setOpCost'].includes(a.op)&&a.target!=null&&only(['target','amount','value']))return [{op:'changeAttribute',target:copy(a.target),property:'opCost',mode:a.op==='setOpCost'?'set':'add',value:a.op==='setOpCost'?a.value??0:a.amount??-1}];
    if(a.op==='setWeather'&&only(['kind']))return [{op:'changeWeather',kind:a.kind||'clear'}];
    if(a.op==='grantMod'&&a.target!=null&&only(['target','mod','value','duration'])&&!['condAtk','vsType','vsHq','takeDoubleFrom','extraTypes','primitiveField'].includes(a.mod))return [{op:'setModifier',target:copy(a.target),property:a.mod,value:{expr:'literal',value:a.value??true},...(a.duration?{duration:a.duration}:{})}];
    if(['summon','addCardToHand'].includes(a.op)&&a.cardId&&only(['cardId','name','side','to','count']))return [{op:'loop',times:a.count??1,actions:[{op:a.op==='summon'?'createUnit':'createCard',cardId:a.cardId,side:a.side||'self',...(a.op==='summon'?{to:a.to||'support'}:{})}]}];
    if(a.op==='chooseOne'&&only(['prompt','options'])&&Array.isArray(a.options)&&a.options.every(o=>!o.targets?.length&&Object.keys(o).every(k=>['label','actions'].includes(k))))return [{op:'choice',prompt:a.prompt||'选择一项',options:copy(a.options)}];
    return [copy(a)];
  }
  function walk(actions,fn){for(const a of Array.isArray(actions)?actions:[]){if(!a||typeof a!=='object')continue;fn(a);for(const key of ['actions','then','else','effects','forEach'])if(Array.isArray(a[key]))walk(a[key],fn);if(a.effect)walk(a.effect.actions,fn);for(const option of Array.isArray(a.options)?a.options:[])walk(option?.actions,fn);}}
  function compile(text,card={},options={}){
    const source=normalize(text),registry=[...new Map((options.pool||Object.values(K.pool||{})).map(c=>[c.id,c])).values()],diagnostics=(card.importDiagnostics||[]).map(message=>({code:'metadata-runtime-gap',message})),fields={},kwMap={},kwValues={},keywords=[];
    let body=[];for(const line of split(source,'\n。；;')){const items=keywordLine(line);if(items){for(const k of items){kwMap[k.id]=true;if(k.quantified)kwValues[k.id]=k.value;if(!keywords.includes(k.id))keywords.push(k.id);}}else body.push(line);}
    body=body.join('\n');let result;
    if(!body)result={effects:[],warnings:[],cardFields:{}};
    else result=basicParse(body,{...card,...fields},registry);
    if(Array.isArray(result))result={effects:result,warnings:[],cardFields:{}};
    if(!result){
      const resolve=P.resolveCardRef;
      P.resolveCardRef=function(name,quoted){const n=String(name||'').replace(/[“”"「」『』]/g,'').trim(),hits=registry.filter(c=>c.name===n||c.id===n);
        if(hits.length===1)return {name:n,cardId:hits[0].id};
        if(hits.length>1){diagnostics.push({code:'ambiguous-card',message:'卡名“'+n+'”对应多张卡，请使用唯一 ID。'});return {name:n};}
        if(quoted){diagnostics.push({code:'missing-card',message:'缺少被引用的卡牌“'+n+'”，请一同导入。'});return {name:n};}
        const ref=resolve(name,false);if(ref?.cardId&&!registry.some(c=>c.id===ref.cardId&&c.name===n)&&!(g.KG_ALIASES&&g.KG_ALIASES[n]===ref.cardId)){diagnostics.push({code:'inexact-card',message:'卡名“'+n+'”仅模糊匹配，未自动接受。'});return {name:n};}
        if(ref?.name&&!hits.length){diagnostics.push({code:'missing-card',message:'未能精确找到卡牌“'+n+'”。'});return {name:n};}return ref;
      };
      try{result=C.compile(body,card);}catch(e){result={effects:[],warnings:['解析失败：'+e.message]};}finally{P.resolveCardRef=resolve;}
    }
    (result.warnings||[]).forEach(message=>diagnostics.push({code:'unparsed-text',message}));
    if(body&&!result.effects?.length&&!Object.keys(result.cardFields||{}).length&&!result.warnings?.length&&!card.referenceCard)diagnostics.push({code:'empty-effect',message:'文本非空，但未生成效果或卡面机制字段，需核对。'});
    const effects=copy(result.effects||[]),legacyOps=new Set();
    for(let i=0;i<effects.length;i++){
      if(effects[i].unimplemented)diagnostics.push({code:'unimplemented-effect',message:'第 '+(i+1)+' 个效果尚未完整实现。'});
      function convert(list){return (list||[]).flatMap(a=>lower(a,registry)).map(a=>{for(const k of ['actions','then','else','effects','forEach'])if(Array.isArray(a[k]))a[k]=convert(a[k]);for(const o of a.options||[])o.actions=convert(o.actions);return a;});}
      effects[i].actions=convert(K.effects.composition.expand(effects[i].actions,'orc'+i,{preserveComposites:!options.atomicOnly}).actions);
      walk(effects[i].actions,a=>{if(typeof a.op==='string'&&!K.effects.composition.catalog[a.op])legacyOps.add(a.op);if(a.unimplemented||(a.op==='log'&&/^未实现[：:]/.test(a.text||'')))diagnostics.push({code:'unimplemented-effect',message:a.text||'子效果尚未完整实现。'});for(const id of [...(typeof a.cardId==='string'?[a.cardId]:[]),...(Array.isArray(a.pool?.cardIds)?a.pool.cardIds:[]),...(Array.isArray(a.target?.cardIds)?a.target.cardIds:[])])if(!registry.some(c=>c.id===id))diagnostics.push({code:'missing-card',message:'缺少卡牌 ID：'+id});if(['createUnit','transformBody'].includes(a.op)&&typeof a.cardId==='string'&&registry.some(c=>c.id===a.cardId&&c.cardType!=='unit'))diagnostics.push({code:'invalid-unit',message:'放入战场或转换的卡牌必须是单位：'+a.cardId});if(a.name&&!a.cardId&&!a.filter&&['summon','addCardToHand','shuffleIn','deckToField'].includes(a.op))diagnostics.push({code:'unresolved-card',message:'卡牌引用未解析：'+a.name});});
    }
    if(g.KG_EFFECT_CONTRACT){const report=g.KG_EFFECT_CONTRACT.validate(effects,g.KG_EFFECT_CONTRACT.build({},K.effects,{}));for(const d of [...report.errors,...report.warnings])diagnostics.push({code:'effect-contract',message:d.path+'：'+d.msg});}
    if(options.atomicOnly&&legacyOps.size)diagnostics.push({code:'legacy-operation',message:'这些动作尚未拆为基本原语：'+[...legacyOps].join('、')});
    const unique=[...new Map(diagnostics.map(d=>[d.code+'|'+d.message,d])).values()];
    return {version,source,effects,cardFields:{...result.cardFields,...fields},keywords,kwMap,kwValues,diagnostics:unique,warnings:unique.map(d=>d.message),complete:!unique.length,legacyOps:[...legacyOps]};
  }
  K.OrC={version,compile,normalize,number,predicate,value,keyword,walk};
  C.compileForImport=compile;
})(typeof window!=='undefined'?window:globalThis);
