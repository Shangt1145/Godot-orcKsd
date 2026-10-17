/* General expressions, predicates and atomic effects. No card-specific recipes. */
(function(g) {
  'use strict';
  const K=g.KG, F=K.effects, clone=x=>JSON.parse(JSON.stringify(x));
  const own=(o,k)=>o!=null&&Object.prototype.hasOwnProperty.call(o,k);
  const forbidden=new Set(['__proto__','prototype','constructor']);
  function path(o,key) {
    for(const part of String(key||'').split('.')) {
      if(forbidden.has(part)) throw Error('不支持这个属性路径');
      if(!own(o,part)) return undefined;
      o=o[part];
    }
    return o;
  }
  function ref(state,ctx,name,candidate) {
    if(name==='candidate') return candidate;
    if(name==='current') return candidate??(ctx.vars||{}).current;
    if(name==='self'||name==='source') {
      if(ctx.hqOwner!=null){const owner=ctx.hqOwner,p=state.players[owner];return {kind:'player',owner,player:p,zone:'hq',hq:p.hq,hqMax:p.hqMax};}
      return ctx.source||ctx.unit;
    }
    if(name==='state') return state;
    if(name==='event') return ctx.event||{};
    if(name==='friendlyPlayer'||name==='enemyPlayer') {const owner=name==='friendlyPlayer'?ctx.owner:1-ctx.owner;return {...state.players[owner],kind:'player',owner,player:state.players[owner]};}
    if(ctx.vars&&own(ctx.vars,name)) return ctx.vars[name];
    if(ctx.targets&&own(ctx.targets,name)) {
      const x=ctx.targets[name];return (Array.isArray(x)?x:[x]).map(t=>{
        if(t.kind==='hq'){const owner=t.player??Number(String(t.value).replace(/^hq/,''));return {...state.players[owner],kind:'player',owner,player:state.players[owner]};}
        return t.unit||K.unitByUid(state,t.value||t)||t;
      });
    }
    return F.refUnit(state,ctx,name);
  }
  function read(entity,key,state) {
    if(Array.isArray(entity)) { if(key==='length')return entity.length; entity=entity[0]; }
    if(!entity)return undefined;
    if(entity.kind==='player'&&key==='defense')return entity.player.hq;
    if(entity.kind==='card'&&entity.zone==='hand'&&key==='cost')return K.instCost(state,entity.owner,entity.instance,entity.def);
    const printed=entity.def||(entity.id&&K.cardDef(state,entity.id))||entity;
    if(key==='keywords')return entity.kws?Object.keys(entity.kws).filter(k=>entity.kws[k]):[...new Set([...(printed.keywords||[]),...Object.keys(printed.kwMap||{}).filter(k=>printed.kwMap[k]),...(state.players[entity.owner]?.cardMods?.[entity.id]?.keywords||[]),...(entity.instance?.mods?.keywords||[])].map(k=>k==='valor'?'fury':k))];
    if(entity.kind==='card'&&entity.zone==='hand'&&['attack','defense'].includes(key)){
      const mods=entity.instance?.mods||{},pile=state.players[entity.owner]?.cardMods?.[entity.id]||{};
      return (mods[key+'Set']??((printed[key]||0)+(pile[key]||0)))+(mods[key]||0);
    }
    if(key==='alive')return !entity.dead;
    if(key==='damaged')return entity.defense<entity.maxDefense;
    if(key==='pinned')return entity.pinnedTurns>0||!!entity.kws?.pin;
    if(key==='missingDefense')return (entity.maxDefense||0)-(entity.defense||0);
    if(key==='opCost'&&entity.uid&&entity.kind!=='card')return K.effOpCost(state,entity.owner,entity);
    if(key.startsWith('printed.'))return path(printed,key.slice(8));
    const value=path(entity,key);return value===undefined?path(printed,key):value;
  }
  function memory(state,ctx,scope,create) {
    const owner=scope==='unit'?(ctx.enchant||ctx.source||ctx.unit):scope==='player'?state.players[ctx.owner]:state;
    if(!owner)throw Error('当前事件没有可存储数据的来源');
    if(create&&!owner.effectMemory)owner.effectMemory={};
    return owner.effectMemory||{};
  }
  function value(v,state,ctx,candidate) {
    if(v===null||typeof v!=='object')return v;
    if(Array.isArray(v))return clone(v);
    if(!v.expr)return F.num(v,state,ctx);
    if(v.expr==='literal')return clone(v.value);
    if(v.expr==='read')return read(ref(state,ctx,v.of||'candidate',candidate),v.path,state);
    if(v.expr==='variable')return (ctx.vars||{})[v.name];
    if(v.expr==='memory')return memory(state,ctx,v.scope||'unit')[v.key]??0;
    if(v.expr==='query')return query(v.query,state,ctx);
    if(v.expr==='count')return query(v.query,state,ctx).length;
    if(v.expr==='size') {const x=value(v.value,state,ctx,candidate);return Array.isArray(x)||typeof x==='string'?x.length:0;}
    if(v.expr==='union'||v.expr==='intersection'){
      const lists=(v.items||[]).map(x=>value(x,state,ctx,candidate));if(lists.some(x=>!Array.isArray(x)))throw Error('集合运算需要列表');
      return v.expr==='union'?[...new Set(lists.flat())]:[...new Set(lists[0]||[])].filter(x=>lists.slice(1).every(list=>list.includes(x)));
    }
    if(v.expr==='random') {
      const a=Number(value(v.min,state,ctx,candidate)),b=Number(value(v.max,state,ctx,candidate));
      if(!Number.isFinite(a)||!Number.isFinite(b))throw Error('随机范围需要数值');
      const lo=Math.ceil(Math.min(a,b)),hi=Math.floor(Math.max(a,b));
      if(hi<lo)throw Error('范围中没有整数');
      return lo+Math.floor(state.rng()*(hi-lo+1));
    }
    if(v.expr==='math') {
      const xs=(v.items||[]).map(x=>Number(value(x,state,ctx,candidate)));
      if(xs.some(x=>!Number.isFinite(x)))throw Error('算式的每一项都需要数值');
      if(v.operator==='add')return xs.reduce((a,b)=>a+b,0);
      if(v.operator==='multiply')return xs.reduce((a,b)=>a*b,1);
      if(v.operator==='subtract')return xs.slice(1).reduce((a,b)=>a-b,xs[0]||0);
      if(v.operator==='divide') {if(xs.slice(1).includes(0))throw Error('不能除以零');return xs.slice(1).reduce((a,b)=>a/b,xs[0]||0);}
      if(v.operator==='min')return xs.length?Math.min(...xs):0;
      if(v.operator==='max')return xs.length?Math.max(...xs):0;
      if(v.operator==='floor')return Math.floor(xs[0]||0);
      if(v.operator==='ceil')return Math.ceil(xs[0]||0);
    }
    throw Error('未知的取值方式：'+v.expr);
  }
  function test(t,state,ctx,candidate) {
    if(!t)return true;
    if(!t.test)return F.evalCond(state,ctx,t);
    if(t.test==='all')return (t.items||[]).every(x=>test(x,state,ctx,candidate));
    if(t.test==='any')return (t.items||[]).some(x=>test(x,state,ctx,candidate));
    if(t.test==='not')return !test(t.item,state,ctx,candidate);
    const a=value(t.left,state,ctx,candidate),b=value(t.right,state,ctx,candidate);
    if(t.test==='exists')return a!==undefined&&a!==null;
    if(t.test==='compare') {
      switch(t.cmp||'==') {
        case '==':return a===b; case '!=':return a!==b;
        case '>':return a>b; case '>=':return a>=b;case '<':return a<b;case '<=':return a<=b;
        case 'contains':return (Array.isArray(a)||typeof a==='string')&&a.includes(b);
        case 'in':return (Array.isArray(b)||typeof b==='string')&&b.includes(a);
      }
    }
    throw Error('未知的判断方式：'+t.test);
  }
  function query(q,state,ctx) {
    if(typeof q==='string')q={sel:'refAll',ref:q};q=q||{sel:'all',side:'friendly'};
    let out=[];
    if(q.sel==='union'){
      const seen=new Set();out=(q.queries||[]).flatMap(x=>query(x,state,ctx)).filter(x=>{const key=x.kind==='player'?'hq:'+x.owner:x.kind==='card'&&x.zone==='hand'?x.instance:x.kind==='card'?'card:'+x.owner+':'+x.zone+':'+x.index+':'+x.id:x.uid||x;if(seen.has(key))return false;seen.add(key);return true;});
    }
    else if(q.sel==='adjacent'){
      const anchors=ref(state,ctx,q.ref||'self'),list=Array.isArray(anchors)?anchors:[anchors];
      for(const anchor of list){if(!anchor?.uid)continue;
        const neighbors=K.unitNeighbors(state,anchor);
        if(q.includeAnchor&&K.zoneOf(state,anchor.uid))neighbors.push(anchor);
        out.push(...neighbors);
      }
      const allowed=new Set(F.selectUnits(state,{...ctx,unit:ctx.source||ctx.unit},{sel:'all',side:q.side||'both'}).map(u=>u.uid));out=[...new Set(out)].filter(u=>allowed.has(u.uid));
    }
    else if(['self','ref','refAll'].includes(q.sel)) {
      const x=ref(state,ctx,q.sel==='self'?'self':q.ref);out=x==null?[]:Array.isArray(x)?x.slice():[x];
      out=out.filter(x=>!x.dead);
      const permitted=new Set(F.selectUnits(state,{...ctx,unit:ctx.source||ctx.unit},{sel:'all',side:'both'}).map(u=>u.uid));
      out=out.filter(x=>x.kind==='card'||!(x.uid&&x.attack!=null)||permitted.has(x.uid));
      if(q.sel==='ref')out=out.slice(0,1);
    } else {
      const sides=q.side==='both'||q.side==='any'?[0,1]:[typeof q.side==='number'?q.side:F.sideIdx(state,ctx,q.side||'friendly')];
      const zone=q.zone||'field';
      if(zone==='pool'){const pool=state.pool||K.pool||{};out=(q.cardIds?q.cardIds.map(id=>pool[id]).filter(Boolean):Object.values(pool)).map(def=>({...def,kind:'card',id:def.id,def,zone:'pool',owner:ctx.owner}));}
      else if(zone==='field'||zone==='support'||zone==='frontline') {
        const side=typeof q.side==='number'?(q.side===ctx.owner?'friendly':'enemy'):q.side||'friendly';
        out=F.selectUnits(state,{...ctx,unit:ctx.source||ctx.unit},{sel:'all',side,zone:zone==='field'?undefined:zone,filter:q.filter});
      } else for(const side of sides) {
        const p=state.players[side];if(!p)continue;
        if(zone==='hq'||zone==='player')out.push({kind:'player',owner:side,player:p,hq:p.hq,hqMax:p.hqMax,kredits:p.kredits,maxKredits:p.maxKredits,zone});
        else if(['hand','deck','discard'].includes(zone)) (p[zone]||[]).forEach((item,index)=>{
          const id=typeof item==='string'?item:item.id,def=K.cardDef(state,id)||{};
          out.push({...def,...(typeof item==='object'?item:{}),kind:'card',owner:side,zone,index,id,def,instance:item});
        });
        else throw Error('未知的区域：'+zone);
      }
    }
    out=out.filter(x=>test(q.where,state,ctx,x));
    if(q.cardIds&&q.zone!=='pool')out=out.filter(x=>q.cardIds.includes(x.id));
    if(q.distinctBy){const seen=new Set();out=out.filter(x=>{const key=read(x,q.distinctBy,state);if(seen.has(key))return false;seen.add(key);return true;});}
    if(q.excludeSelf)out=out.filter(x=>x!==ctx.source&&x!==ctx.unit);
    if(q.sel==='random')out=F.shuffle(state,out).slice(0,Math.max(0,Number(value(q.count??1,state,ctx))||0));
    if(q.sel==='first'||q.sel==='last')out=q.sel==='first'?out.slice(0,Math.max(0,Number(value(q.count??1,state,ctx))||0)):out.slice().reverse().slice(0,Math.max(0,Number(value(q.count??1,state,ctx))||0));
    return out;
  }
  const number=(x,state,ctx,candidate)=>{
    const n=Number(value(x,state,ctx,candidate));if(!Number.isFinite(n))throw Error('需要一个有效数值');return n;
  };
  async function run(actions,state,ctx) {
    for(const a of actions||[]) { if(state.over)return;await F.performAction(state,ctx,a); }
  }
  const sub=ctx=>({...ctx,vars:{...ctx.vars},targets:{...ctx.targets}});
  const bind=(ctx,u)=>({...ctx,targets:{...ctx.targets,__atomic:[{kind:'unit',value:u.uid,unit:u}]},vars:{...ctx.vars,__candidate:u}});
  function unitQuery(q,state,ctx){const out=query(q,state,ctx);if(out.some(x=>x.kind==='player'||x.kind==='card'||!x.uid||!K.zoneOf(state,x.uid)))throw Error('这个动作需要场上单位，请修改作用对象或筛选');return out;}
  const keywordFields={ambush:['ambushReady'],shield:['shieldReady','shield'],magnetic:['magneticCharges'],deathrattle:['mods.suppressDeathrattle','deathSuppressed']};
  function capture(u,paths){return paths.map(key=>{const v=path(u,key);return {key,exists:v!==undefined,value:v===undefined?null:clone(v)};});}
  function restore(u,fields){for(const f of fields){const parts=f.key.split('.'),last=parts.pop();let at=u;for(const p of parts)at=at[p]||(at[p]={});if(f.exists)at[last]=clone(f.value);else delete at[last];}}
  function cardPoolQuery(pool,mode){
    if(!pool||!['pool','cards','deck','hand','discard'].includes(pool.source))throw Error('请选择候选卡池来源');
    if(pool.source==='cards'&&(!Array.isArray(pool.cardIds)||!pool.cardIds.length))throw Error('请添加候选卡牌');
    if(pool.source==='hand'&&pool.side&&pool.side!=='friendly')throw Error('手牌卡池只能使用我方手牌');
    if(mode==='fixed'&&(pool.source!=='cards'||new Set(pool.cardIds).size>3))throw Error('固定三选一需要指定最多三张不同的候选卡');
    const items=[{test:'compare',left:{expr:'read',path:'referenceCard'},cmp:'!=',right:true}];
    if(pool.where)items.push(clone(pool.where));
    if(pool.source!=='cards'){
      if(!pool.includeTokens)items.push({test:'compare',left:{expr:'read',path:'token'},cmp:'!=',right:true});
      if(!pool.includeReserved)items.push({test:'compare',left:{expr:'read',path:'reserved'},cmp:'!=',right:true});
    }
    return {sel:mode==='fixed'?'all':'random',count:3,zone:pool.source==='cards'?'pool':pool.source,side:pool.side||'friendly',distinctBy:'id',...(pool.source==='cards'?{cardIds:[...new Set(pool.cardIds)]}:{}),where:{test:'all',items}};
  }
  function cardOfferActions(a,namespace){
    const chosen=namespace+'_chosen',id=namespace+'_id',mode=a.op==='developCard'?'random':a.mode||'fixed';
    return [{op:'selectObjects',target:cardPoolQuery(a.pool,mode),as:chosen,count:1,asCopies:true,prompt:a.prompt||(a.op==='developCard'?'开发：选择一张牌':'三选一：选择一张牌')},
      {op:'let',name:id,value:{expr:'read',of:chosen,path:'id'}},
      {op:'createCard',cardId:{expr:'variable',name:id},side:a.side||'self',...(a.as?{as:a.as}:{})},
      ...(a.op==='developCard'?[{op:'emitEvent',trigger:'developed',side:a.side||'self',test:{test:'exists',left:{expr:'variable',name:id}},data:{cardId:{expr:'variable',name:id},amount:1}}]:[])];
  }
  function remember(u,store,key,fields,untilTurn){
    const records=u[store]||(u[store]=[]);
    if(!records.some(r=>r.key===key))records.push({key,fields:capture(u,fields),untilTurn});
  }
  function expire(state,u){u.primitiveExpiry=(u.primitiveExpiry||[]).filter(r=>{if(r.untilTurn>state.turn)return true;restore(u,r.fields);return false;});}
  function clearAura(u){for(const r of (u.primitiveAura||[]).slice().reverse())restore(u,r.fields);u.primitiveAura=[];}
  async function keyword(state,ctx,u,a){
    const key=a.keyword==='valor'?'fury':a.keyword;
    if(a.duration==='turn')remember(u,'primitiveExpiry','kw:'+key,['kws.'+key,'kwValues.'+key,...(keywordFields[key]||[])],state.turn);
    else u.primitiveExpiry=(u.primitiveExpiry||[]).filter(r=>r.key!=='kw:'+key);
    await F.OPS[a.enabled===false?'removeKeyword':'grant'](state,bind(ctx,u),{target:'__atomic',keyword:key,value:number(a.value??1,state,ctx,u)});
  }
  const atoms={
    let:async(state,ctx,a)=>{if(!a.name||forbidden.has(a.name))throw Error('变量名称无效');ctx.vars=ctx.vars||{};ctx.vars[a.name]=value(a.value,state,ctx);},
    store:async(state,ctx,a)=>{
      if(!a.key||forbidden.has(a.key))throw Error('存储名称无效');const x=value(a.value,state,ctx);
      if(x!==null&&!['string','number','boolean'].includes(typeof x))throw Error('持久记忆只保存单个数值、文字或布尔值');
      memory(state,ctx,a.scope||'unit',true)[a.key]=x;
    },
    sequence:async(state,ctx,a)=>run(a.actions,state,ctx),
    branch:async(state,ctx,a)=>run(test(a.test,state,ctx)?a.then:a.else,state,sub(ctx)),
    loop:async(state,ctx,a)=>{
      const n=Math.max(0,Math.floor(number(a.times,state,ctx)));if(n>10000)throw Error('单次循环超过 10000 次');
      for(let i=0;i<n&&!state.over;i++)await run(a.actions,state,sub(ctx));
    },
    iterate:async(state,ctx,a)=>{
      for(const item of query(a.target,state,ctx)) {if(state.over)return;const s=sub(ctx);s.vars[a.as||'current']=item;await run(a.actions,state,s);}
    },
    choice:async(state,ctx,a)=>{
      const options=(a.options||[]).filter(o=>test(o.test,state,ctx));if(!options.length)return;
      const pick=await K.ask(ctx.chooser,{kind:'chooseOne',prompt:a.prompt||'选择一项',state,options:options.map((o,i)=>({value:i,label:o.label||'选项 '+(i+1)}))});
      const chosen=options[typeof pick==='number'?pick:0]||options[0],local=sub(ctx);
      if(chosen.targets?.length)await F.resolveDeclaredTargets(state,local,[{targets:chosen.targets}],ctx.chooser);
      await run(chosen.actions,state,local);
    },
    selectObjects:async(state,ctx,a)=>{
      if(!a.as||forbidden.has(a.as))throw Error('选择结果的名称无效');
      const candidates=query(a.target,state,ctx),selected=[],count=Math.max(0,Math.floor(number(a.count??1,state,ctx)));
      if(count>100)throw Error('单次选择超过 100 个对象');
      if(candidates.some(x=>x.kind!=='card'||!(a.asCopies?['hand','pool','deck','discard']:['hand','pool']).includes(x.zone)))throw Error('选择对象需要手牌或卡池候选');
      if(candidates.some(x=>x.zone==='hand'&&x.owner!==ctx.owner&&!x.instance.revealed)&&!a.reveal)throw Error('不能选择未公开的敌方手牌');
      const hand=!a.asCopies&&candidates.length&&candidates.every(x=>x.zone==='hand'&&x.owner===candidates[0].owner);
      for(let i=0;i<count&&candidates.length;i++){
        const options=candidates.map((x,j)=>({value:hand?state.players[x.owner].hand.indexOf(x.instance):x.id,label:x.def.name||x.id}));
        const pick=await K.ask(ctx.chooser,{kind:hand?'target':'discover',...(hand?{from:'hand',owner:candidates[0].owner}:{}),prompt:a.prompt||'选择一张牌',options,state});
        const index=options.findIndex(o=>o.value===pick),chosen=candidates.splice(index<0?0:index,1)[0];selected.push(chosen);
      }
      (ctx.vars||(ctx.vars={}))[a.as]=selected;
    },
    emitEvent:async(state,ctx,a)=>{
      if(!test(a.test,state,ctx))return;if(!a.trigger||forbidden.has(a.trigger))throw Error('事件名称无效');
      const data={};for(const [key,v] of Object.entries(a.data||{})){if(forbidden.has(key)||['owner','trigger','source','chooser'].includes(key))throw Error('不能覆盖事件所属方或来源');data[key]=value(v,state,ctx);}
      await K.runTrigger(state,{...data,trigger:a.trigger,owner:F.sideIdx(state,ctx,a.side||'self'),source:ctx.source||ctx.unit||null,chooser:ctx.chooser});
    },
    threeChoice:async(state,ctx,a)=>run(cardOfferActions(a,'__offer'+(ctx._offerSerial=(ctx._offerSerial||0)+1)),state,ctx),
    developCard:async(state,ctx,a)=>run(cardOfferActions(a,'__offer'+(ctx._offerSerial=(ctx._offerSerial||0)+1)),state,ctx),
    dealDamage:async(state,ctx,a)=>{
      for(const u of query(a.target,state,ctx)) {
        const n=number(a.amount,state,ctx,u);
        if(u.kind==='player')await F.OPS.damageHQ(state,ctx,{side:u.owner===ctx.owner?'self':'enemy',amount:n});
        else if(u.uid&&u.attack!=null)await F.OPS.damage(state,bind(ctx,u),{target:'__atomic',amount:n});
      }
    },
    restoreHealth:async(state,ctx,a)=>{
      for(const u of query(a.target,state,ctx)) {
        const n=number(a.amount,state,ctx,u);
        if(u.kind==='player')await F.OPS.healHQ(state,ctx,{side:u.owner===ctx.owner?'self':'enemy',amount:n});
        else if(u.uid&&u.attack!=null)await F.OPS.heal(state,bind(ctx,u),{target:'__atomic',amount:n});
      }
    },
    destroyUnit:async(state,ctx,a)=>{
      for(const u of unitQuery(a.target,state,ctx))if(u.uid&&u.attack!=null)await F.OPS.destroy(state,bind(ctx,u),{target:'__atomic'});
    },
    changeAttribute:async(state,ctx,a)=>{
      for(const u of query(a.target,state,ctx)) {
        const n=number(a.value,state,ctx,u),key=a.property,mode=a.mode||'add';
        if(!['add','set'].includes(mode))throw Error('未知的修改方式');
        if(u.kind==='player'){
          if(key!=='defense'||mode!=='add'||a.duration==='turn')throw Error('总部支持永久增减防御力');
          await F.OPS.hqMaxUp(state,ctx,{side:u.owner===ctx.owner?'self':'enemy',amount:n});K.checkWin(state);continue;
        }
        if(u.kind==='card'){
          const inst=u.instance,p=state.players[u.owner];if(u.zone!=='hand'||!p.hand.includes(inst))throw Error('这个属性修改需要仍在手牌中的实例');
          if(a.duration==='turn')throw Error('尚不支持手牌属性的临时修改');
          if(key==='attack'||key==='defense'){const mods=inst.mods||(inst.mods={attack:0,defense:0,keywords:[]});mods[key]=(mods[key]||0)+(mode==='set'?n-read(u,key,state):n);}
          else if(key==='cost'){if(mode==='set')inst.costSet=Math.max(0,n);else if(inst.costSet!=null)inst.costSet=Math.max(0,inst.costSet+n);else inst.costMod=(inst.costMod||0)+n;}
          else if(key==='opCost')inst.opCostMod=(inst.opCostMod||0)+(mode==='set'?n-(u.def.opCost||0)-(inst.opCostMod||0):n);
          else throw Error('不支持修改这个手牌属性');continue;
        }
        if(!u.uid||!K.zoneOf(state,u.uid))throw Error('这个属性修改需要单位或手牌实例');
        const c=bind(ctx,u);
        if(key==='attack'||key==='defense') {
          if(!(u.uid&&u.attack!=null))throw Error('这个属性修改需要场上单位');
          if(mode==='set'){
            const oldDefense=u.defense,delta=n-(key==='attack'?u.attack:u.maxDefense);
            await F.OPS.buff(state,c,{target:'__atomic',[key]:delta,...(a.duration==='turn'?{duration:'turn'}:{})});
            if(key==='defense'){u.defense=Math.min(oldDefense,n);if(n<=0)await K.killUnit(state,u,ctx.source);}
          }else await F.OPS[mode==='set'?'setStats':'buff'](state,c,{target:'__atomic',[key]:n,...(a.duration?{duration:a.duration}:{})});
        } else if(key==='opCost') {
          if(mode==='set'){
            if(a.duration==='turn')remember(u,'primitiveExpiry','opCost',['instOpCostSet'],state.turn);
            else u.primitiveExpiry=(u.primitiveExpiry||[]).filter(r=>r.key!=='opCost');
            await F.OPS.setOpCost(state,c,{target:'__atomic',value:n});
          }else{
            u.mods.opCostMod=(u.mods.opCostMod||0)+n;
            (u.dynMods=u.dynMods||[]).push({mod:'opCostAdd',value:n,...(a.duration==='turn'?{untilTurn:state.turn}:{})});
          }
        } else throw Error('不支持直接修改这个属性，请使用对应的状态或资源动作');
      }
    },
    setKeyword:async(state,ctx,a)=>{
      for(const u of unitQuery(a.target,state,ctx))if(u.uid&&u.attack!=null)await keyword(state,ctx,u,a);
    },
    drawOne:async(state,ctx,a)=>{
      if(!a.target&&!a.as)return F.OPS.draw(state,ctx,{side:a.side||'self',count:1});
      if(a.as&&forbidden.has(a.as))throw Error('抽牌记录的名称无效');
      const side=F.sideIdx(state,ctx,a.side||'self'),p=state.players[side];if(a.as)(ctx.vars||(ctx.vars={}))[a.as]=[];
      let entry;
      if(a.target){const card=query(a.target,state,ctx)[0];if(!card)return;if(card.kind!=='card'||card.zone!=='deck'||card.owner!==side)throw Error('抽牌筛选需要己方卡组中的卡牌');if(p.deck[card.index]!==card.instance)throw Error('抽牌目标已改变');entry=card.instance;}
      const inst=await K.drawCard(state,p,false,{extra:true,...(entry===undefined?{}:{entry})});if(inst&&a.as)ctx.vars[a.as]=[{...K.cardDef(state,inst.id),kind:'card',id:inst.id,def:K.cardDef(state,inst.id),owner:side,zone:'hand',instance:inst}];
    },
    discardOne:async(state,ctx,a)=>{
      if(!a.target)return F.OPS.discard(state,ctx,{side:a.side||'self',count:1});
      for(const card of query(a.target,state,ctx)){
        if(card.kind!=='card'||card.zone!=='hand')throw Error('弃牌目标需要手牌实例');
        const p=state.players[card.owner],index=p.hand.indexOf(card.instance);if(index<0)continue;
        p.hand.splice(index,1);p.discard.push(card.id);(ctx.vars||(ctx.vars={})).lastDiscardedCardId=card.id;state.__lastDiscardedCardId=card.id;
        K.log(state,p.name+' 弃掉了 '+card.def.name,'discard');
      }
    },
    createUnit:async(state,ctx,a)=>F.OPS.summon(state,ctx,{cardId:value(a.cardId,state,ctx),side:a.side||'self',to:a.to||'support',count:1}),
    createCard:async(state,ctx,a)=>{
      const id=value(a.cardId,state,ctx);if(a.as)(ctx.vars||(ctx.vars={}))[a.as]=[];if(!id)return;
      if(typeof id!=='string'||!(state.pool||K.pool||{})[id])throw Error('创建的卡牌不在当前卡池中');
      const p=state.players[F.sideIdx(state,ctx,a.side||'self')],before=new Set(p.hand);
      await F.OPS.addCardToHand(state,ctx,{cardId:id,side:a.side||'self',count:1});
      if(a.as)ctx.vars[a.as]=p.hand.filter(inst=>!before.has(inst)).map(inst=>({...K.cardDef(state,inst.id),kind:'card',id:inst.id,def:K.cardDef(state,inst.id),owner:p.idx,zone:'hand',instance:inst}));
    },
    moveCard:async(state,ctx,a)=>{
      const cards=query(a.target,state,ctx),changed=new Set();
      for(const card of cards){
        if(card.kind!=='card'||card.zone!=='hand')throw Error('卡牌移动需要手牌实例');
        const p=state.players[card.owner],index=p.hand.indexOf(card.instance);if(index<0)continue;
        if(a.to==='deck'){p.hand.splice(index,1);const saved=clone(card.instance);delete saved.suspended;delete saved.ordersAtEntry;delete saved.intelAtEntry;delete saved.kwAtEntry;
          if(a.position==='top')p.deck.unshift(saved);else if(a.position==='bottom')p.deck.push(saved);else {p.deck.push(saved);changed.add(p);}
        }else if(a.to==='hand'&&['left','right'].includes(a.position)){p.hand.splice(index,1);if(a.position==='left')p.hand.unshift(card.instance);else p.hand.push(card.instance);}
        else throw Error('请选择卡组位置或手牌左右位置');
      }
      for(const p of changed){F.shuffle(state,p.deck);if(K.runTrigger)await K.runTrigger(state,{trigger:'shuffle',owner:p.idx,source:null});}
    },
    setPinned:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS[a.enabled===false?'unpin':'pin'](state,bind(ctx,u),{target:'__atomic',turns:number(a.turns??1,state,ctx,u)});},
    suppressUnit:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.silence(state,bind(ctx,u),{target:'__atomic'});},
    moveUnit:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.move(state,bind(ctx,u),{target:'__atomic',to:a.to});},
    returnUnit:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.returnToHand(state,bind(ctx,u),{target:'__atomic'});},
    leaveUnit:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.removeUnit(state,bind(ctx,u),{target:'__atomic'});},
    battleUnits:async(state,ctx,a)=>{
      const sources=unitQuery(a.target,state,ctx),opponents=unitQuery(a.with,state,ctx);
      for(const source of sources)for(const opponent of opponents){if(source.dead||opponent.dead||source===opponent)continue;await F.OPS.fight(state,bind({...ctx,unit:source,source},opponent),{target:'__atomic'});}
    },
    transformBody:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.transformUnit(state,bind(ctx,u),{target:'__atomic',cardId:value(a.cardId,state,ctx,u)});},
    bindEvent:async(state,ctx,a,recipients)=>{
      for(const u of recipients||query(a.target,state,ctx)){
        const effect=clone(a.effect||{trigger:a.trigger,actions:a.actions||[]});
        if(u.kind==='player'){
          const p=state.players[u.owner];
          if(!p||p!==u.player)throw Error('绑定效果需要有效的总部');
          (p.hqEnchants||(p.hqEnchants=[])).push({name:a.name||ctx.card?.name||'总部效果',effects:[effect],card:ctx.card||null,source:null,untilTurn:null});
          K.log(state,p.name+' 的总部获得效果：'+(a.name||'总部效果'),'keyword');
        }else if(u.kind==='card'){
          if(u.zone!=='hand'||!state.players[u.owner].hand.includes(u.instance))throw Error('绑定效果需要仍在手牌中的实例');
          const inst=u.instance;if(effect.trigger==='play')(inst.playEffects||(inst.playEffects=[])).push(effect);
          else {if(u.def.cardType!=='unit')throw Error('这个持续能力需要单位手牌');const mods=inst.mods||(inst.mods={attack:0,defense:0,keywords:[]});(mods.effects||(mods.effects=[])).push(effect);}
        }else {if(!u.uid||!K.zoneOf(state,u.uid))throw Error('绑定效果需要单位或手牌实例');(u.extraEffects||(u.extraEffects=[])).push(effect);}
      }
      K.recomputeAuras(state);
    },
    bindHQEvent:async(state,ctx,a)=>{
      const targets=query(a.target,state,ctx);
      if(targets.some(u=>u.kind!=='player'))throw Error('总部附加效果需要选择总部');
      await atoms.bindEvent(state,ctx,a,targets);
    },
    setModifier:async(state,ctx,a)=>{
      if(!a.property||forbidden.has(a.property))throw Error('修正名称无效');
      for(const u of unitQuery(a.target,state,ctx)){
        const v=value(a.value,state,ctx,u),record={mod:'primitiveField',property:a.property,value:v};
        u.mods[a.property]=v;
        (u.dynMods=u.dynMods||[]).push({...record,...(a.duration==='turn'?{untilTurn:state.turn}:{})});
      }
    },
    transferControl:async(state,ctx,a)=>{for(const u of unitQuery(a.target,state,ctx))await F.OPS.takeControl(state,bind({...ctx,owner:F.sideIdx(state,ctx,a.side||'self')},u),{target:'__atomic'});},
    changeResource:async(state,ctx,a)=>{
      const side=typeof a.side==='number'?a.side:F.sideIdx(state,ctx,a.side||'self'),p=state.players[side],key=a.property||'kredits';
      let n=number(a.value,state,ctx);if(a.mode==='set')n-=p[key];
      const relative=side===ctx.owner?'self':'enemy';
      if(key==='kredits')await F.OPS[n>=0?'gainKredits':'loseKredits'](state,ctx,{side:relative,amount:Math.abs(n)});
      else if(key==='maxKredits')await F.OPS[n>=0?'gainKreditSlot':'loseKreditSlots'](state,ctx,{side:relative,amount:Math.abs(n)});
      else throw Error('未知资源属性');
    },
    changeWeather:async(state,ctx,a)=>F.OPS.setWeather(state,ctx,{kind:a.kind||'clear'}),
  };
  const field=(name,type,initial)=>({name,type,initial});
  const target=()=>({sel:'all',side:'enemy',zone:'field'});
  const catalog={
    let:{name:'定义变量',group:'记录',fields:[field('name','text','v1'),field('value','value',1)]},
    store:{name:'写入记忆',group:'记录',fields:[field('scope','scope','unit'),field('key','text','counter'),field('value','value',1)]},
    sequence:{name:'顺序执行',group:'流程',fields:[field('actions','actions',[])]},
    branch:{name:'条件分支',group:'流程',fields:[field('test','predicate',{test:'all',items:[]}),field('then','actions',[]),field('else','actions',[])]},
    loop:{name:'重复执行',group:'流程',fields:[field('times','value',2),field('actions','actions',[])]},
    iterate:{name:'逐个执行',group:'流程',fields:[field('target','query',target()),field('as','text','current'),field('actions','actions',[])]},
    choice:{name:'玩家选择',group:'流程',fields:[field('prompt','text','选择一项'),field('options','options',[])]},
    selectObjects:{name:'选择并记录卡牌',group:'卡牌',fields:[field('target','query',{sel:'all',side:'friendly',zone:'hand'}),field('as','text','chosen'),field('count','value',1),field('prompt','text','选择一张牌'),{...field('reveal','boolean',false),optional:true},{...field('asCopies','boolean',false),optional:true}]},
    emitEvent:{name:'触发事件',group:'记录',fields:[field('trigger','trigger','developed'),field('side','side','self'),field('data','eventData',{}),{...field('test','predicate',{test:'all',items:[]}),optional:true}]},
    dealDamage:{name:'造成伤害',group:'单位与总部',fields:[field('target','query',target()),field('amount','value',1)]},
    restoreHealth:{name:'恢复生命',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('amount','value',1)]},
    destroyUnit:{name:'消灭单位',group:'单位与总部',fields:[field('target','query',target())]},
    changeAttribute:{name:'修改一个属性',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('property','attribute','attack'),field('mode','mode','add'),field('value','value',1),field('duration','duration','permanent')]},
    setKeyword:{name:'设置一个词条',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('keyword','keyword','blitz'),field('enabled','boolean',true),field('value','value',1),field('duration','duration','permanent')]},
    setPinned:{name:'设置压制状态',group:'单位与总部',fields:[field('target','query',target()),field('enabled','boolean',true),field('turns','value',1)]},
    suppressUnit:{name:'抑制单位',group:'单位与总部',fields:[field('target','query',target())]},
    moveUnit:{name:'移动单位',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('to','position','support')]},
    returnUnit:{name:'单位撤退',group:'单位与总部',fields:[field('target','query',target())]},
    leaveUnit:{name:'移除单位（不触发亡计）',group:'单位与总部',fields:[field('target','query',target())]},
    battleUnits:{name:'两单位交战一次',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('with','query',target())]},
    transformBody:{name:'转换单位形态',group:'单位与总部',fields:[field('target','query',target()),field('cardId','card','')]},
    bindEvent:{name:'绑定一个触发效果',group:'记录',fields:[field('target','query',{sel:'self'}),field('effect','effect',{trigger:'death',actions:[]}),{...field('trigger','trigger','death'),optional:true},{...field('actions','actions',[]),optional:true}]},
    grantEffect:{name:'授予效果（已有格式）',group:'记录',fields:[field('target','query',{sel:'self'}),field('effect','effect',{trigger:'death',actions:[]})]},
    hqEnchant:{name:'总部附魔（已有格式）',group:'单位与总部',fields:[field('side','side','self'),field('name','text','总部效果'),field('effects','effectList',[{trigger:'turnStart',actions:[]}]),field('once','boolean',false),field('untilTurnEnd','boolean',false)]},
    bindHQEvent:{name:'给总部附加效果',group:'单位与总部',fields:[field('target','query',{sel:'all',side:'friendly',zone:'hq'}),field('name','text','总部效果'),field('effect','effect',{trigger:'turnStart',actions:[]})]},
    setModifier:{name:'设置一个规则修正',group:'单位与总部',fields:[field('target','query',{sel:'self'}),field('property','text','immuneOrders'),field('value','value',true),field('duration','duration','permanent')]},
    transferControl:{name:'改变控制权',group:'单位与总部',fields:[field('target','query',target()),field('side','side','self')]},
    drawOne:{name:'额外抽一张牌',group:'卡牌',fields:[field('side','side','self'),{...field('target','query',{sel:'all',side:'friendly',zone:'deck'}),optional:true},{...field('as','text','drawn'),optional:true}]},
    discardOne:{name:'弃一张牌',group:'卡牌',fields:[field('side','side','self'),{...field('target','query',{sel:'random',side:'friendly',zone:'hand',count:1}),optional:true}]},
    createUnit:{name:'放入一个单位',group:'卡牌',fields:[field('cardId','card',''),field('side','side','self'),field('to','position','support')]},
    createCard:{name:'加入手牌',group:'卡牌',fields:[field('cardId','card',''),field('side','side','self'),{...field('as','text','created'),optional:true}]},
    threeChoice:{name:'三选一',group:'卡牌',composite:true,fields:[field('mode','offerMode','fixed'),field('pool','cardPool',{source:'cards',cardIds:[]}),field('side','side','self'),{...field('as','text','chosenCard'),optional:true},{...field('prompt','text','三选一：选择一张牌'),optional:true}]},
    developCard:{name:'开发',group:'卡牌',composite:true,fields:[field('pool','cardPool',{source:'pool',includeTokens:false,includeReserved:false}),field('side','side','self'),{...field('as','text','developedCard'),optional:true},{...field('prompt','text','开发：选择一张牌'),optional:true}]},
    moveCard:{name:'移动卡牌实例',group:'卡牌',fields:[field('target','query',{sel:'all',zone:'hand',side:'friendly'}),field('to','cardZone','deck'),field('position','cardPosition','shuffle')]},
    changeResource:{name:'修改一种资源',group:'资源与环境',fields:[field('side','side','self'),field('property','resource','kredits'),field('mode','mode','add'),field('value','value',1)]},
    changeWeather:{name:'设置天气',group:'资源与环境',fields:[field('kind','weather','clear')]},
  };
  function seed(op){return Object.assign({op},Object.fromEntries((catalog[op].fields||[]).filter(f=>!f.optional).map(f=>[f.name,clone(f.initial)])));}
  // Expansion is explicit, reversible, and rejects fields it cannot faithfully translate.
  function expand(list,namespace,options={}) {
    let serial=0;
    const unresolved=[];
    const rewrite=a=>{
      if(!a||typeof a!=='object')return [clone(a)];
      if(catalog[a.op]?.composite){if(options.preserveComposites)return [clone(a)];return cardOfferActions(a,namespace+'_'+(++serial)).flatMap(rewrite);}
      if(catalog[a.op]){const copy=clone(a);for(const k of ['actions','then','else'])if(Array.isArray(copy[k]))copy[k]=copy[k].flatMap(rewrite);if(Array.isArray(copy.options))for(const option of copy.options)if(Array.isArray(option.actions))option.actions=option.actions.flatMap(rewrite);if(copy.effect?.actions)copy.effect.actions=copy.effect.actions.flatMap(rewrite);return [copy];}
      const keys=Object.keys(a).filter(k=>k!=='op');
      const only=names=>keys.every(k=>names.includes(k));
      const q=a.target||{sel:'self'};
      const map={damage:'dealDamage',damageAll:'dealDamage',heal:'restoreHealth',healAll:'restoreHealth',destroy:'destroyUnit',destroyAll:'destroyUnit'};
      if(map[a.op]&&a.target!=null&&only(['target','amount']))return [{...clone(a),op:map[a.op]}];
      if(['damageHQ','healHQ'].includes(a.op)&&only(['side','amount']))return [{op:a.op==='damageHQ'?'dealDamage':'restoreHealth',target:{sel:'all',side:a.side||(a.op==='damageHQ'?'enemy':'self'),zone:'hq'},amount:clone(a.amount??0)}];
      if(a.op==='draw'&&Number.isInteger(a.count??1)&&only(['side','count']))return [{op:'loop',times:clone(a.count??1),actions:[{op:'drawOne',side:a.side||'self'}]}];
      if(a.op==='discard'&&Number.isInteger(a.count??1)&&only(['side','count']))return [{op:'loop',times:clone(a.count??1),actions:[{op:'discardOne',side:a.side||'enemy'}]}];
      if(a.op==='setVar'&&only(['name','value']))return [{...clone(a),op:'let'}];
      if(['buff','buffAll','setStats'].includes(a.op)&&a.target!=null&&only(['target','attack','defense','duration'])) {
        const fields=['attack','defense'].filter(k=>a[k]!=null);const name=namespace+'_'+(++serial);
        // Capture numerical expressions before either attribute changes.
        return [...fields.map(property=>({op:'let',name:name+'_'+property,value:clone(a[property])})),{op:'let',name,value:{expr:'query',query:clone(q)}},...fields.map(property=>({op:'changeAttribute',target:{sel:'refAll',ref:name},property,mode:a.op==='setStats'?'set':'add',value:{expr:'variable',name:name+'_'+property},...(a.duration?{duration:a.duration}:{})}))];
      }
      if(['grant','grantAll','removeKeyword'].includes(a.op)&&a.target!=null&&only(['target','keyword','value','duration']))return [{op:'setKeyword',target:clone(q),keyword:a.keyword,enabled:a.op!=='removeKeyword',value:clone(a.value??1),...(a.duration?{duration:a.duration}:{})}];
      if(a.op==='conditional'&&only(['condition','then','actions','else']))return [{op:'branch',test:clone(a.condition),then:(a.then||a.actions||[]).flatMap(rewrite),else:(a.else||[]).flatMap(rewrite)}];
      if(a.op==='repeat'&&only(['times','actions']))return [{op:'loop',times:clone(a.times),actions:(a.actions||[]).flatMap(rewrite)}];
      // forEach uses target bindings rather than variable bindings. Preserve this distinction.
      const copy=clone(a);for(const k of ['actions','then','else','effects','forEach'])if(Array.isArray(copy[k]))copy[k]=copy[k].flatMap(rewrite);
      unresolved.push(a.op);return [copy];
    };
    const actions=(list||[]).flatMap(rewrite);return {actions,unresolved:[...new Set(unresolved)]};
  }
  const continuousOps={let:1,sequence:1,branch:1,loop:1,iterate:1,changeAttribute:1,setKeyword:1,setModifier:1};
  function continuous(a,state,ctx,apply) {
    const execute=(list,c)=>{for(const x of list||[])continuous(x,state,c,apply);};
    if(a.op==='let'){if(!a.name||forbidden.has(a.name))throw Error('变量名称无效');ctx.vars[a.name]=value(a.value,state,ctx);}
    else if(a.op==='sequence')execute(a.actions,ctx);
    else if(a.op==='branch')execute(test(a.test,state,ctx)?a.then:a.else,sub(ctx));
    else if(a.op==='loop'){const n=Math.max(0,Math.floor(number(a.times,state,ctx)));if(n>10000)throw Error('单次循环超过 10000 次');for(let i=0;i<n;i++)execute(a.actions,sub(ctx));}
    else if(a.op==='iterate')for(const u of query(a.target,state,ctx)){const c=sub(ctx);c.vars[a.as||'current']=u;execute(a.actions,c);}
    else if(a.op==='changeAttribute')for(const u of unitQuery(a.target,state,ctx)){
      const n=number(a.value,state,ctx,u),key=a.property,c=bind(ctx,u);
      const delta=a.mode==='set'?n-(key==='opCost'?read(u,key,state):key==='defense'?u.maxDefense:u[key]):n;
      if(key==='opCost')apply({op:'opCostMod',target:'__atomic',amount:delta},c);
      else if(key==='attack'||key==='defense')apply({op:'buff',target:'__atomic',[key]:delta},c);
    }
    else if(a.op==='setKeyword')for(const u of unitQuery(a.target,state,ctx)){
      const key=a.keyword==='valor'?'fury':a.keyword;
      remember(u,'primitiveAura','kw:'+key,['kws.'+key,'kwValues.'+key,...(keywordFields[key]||[])],state.turn);
      if(a.enabled===false)delete u.kws[key];else F.grantKw(state,u,key,number(a.value??1,state,ctx,u));
    }
    else if(a.op==='setModifier')for(const u of unitQuery(a.target,state,ctx))apply({op:'grantMod',target:'__atomic',mod:'primitiveField',property:a.property,value:value(a.value,state,ctx,u)},bind(ctx,u));
    else if(F.PASSIVE_CONT_OPS[a.op])apply(a,ctx);
  }
  F.composition={value,test,query,read,path,catalog,seed,expand,run,continuousOps,continuous,expire,clearAura,cardPoolQuery,cardOfferActions};
  for(const id of Object.keys(atoms))if(own(F.OPS,id))throw Error('基本动作与旧动作重名：'+id);
  Object.assign(F.OPS,atoms);F.CONDS.predicate=(state,ctx,c)=>test(c.test,state,ctx);
  F.BASIC_PARAMETERS=Object.fromEntries(Object.entries(catalog).map(([op,d])=>[op,Object.fromEntries(d.fields.map(f=>[f.name,{type:f.type}]))]));
})(typeof window!=='undefined'?window:globalThis);
