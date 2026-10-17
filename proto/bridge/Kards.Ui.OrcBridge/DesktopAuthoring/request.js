(()=>{
      const r=request,K=KG;
      if(r.method==='catalog')return {contract:KG_EFFECT_CONTRACT.build({},K.effects,{}),keywords:K.KEYWORDS,
        vocabulary:KG_EFFECT_CONTRACT.editorCatalog(KG_EFFECT_CONTRACT.build({},K.effects,{}),K.effects,{}),primitiveCatalog:K.effects.composition.catalog,labels:K.inspector.Core,behaviorActions:K.BEHAVIOR_ACTIONS,behaviorPhases:K.BEHAVIOR_PHASES};
      if(r.method==='infer')return KG_EFFECT_CONTRACT.editorCatalog(KG_EFFECT_CONTRACT.build({},K.effects,{}),K.effects,{}).infer(r.name);
      if(r.method==='roundtrip')return KG.inspector.fromModel(KG.inspector.toModel(r.value));
      if(r.method==='validate')return KG.inspector.validate(r.value,KG_EFFECT_CONTRACT.build({},K.effects,{}),{engine:K.effects});
      if(r.method==='parityCorpus')return r.rows.map(row=>K.OrC.compile(row.text,{cardType:row.cardType},{pool:[]}));
      if(r.method==='merge')return K.inspector.Core.renumberTargets(r.effects,r.used||[]).effects;
      if(r.method==='corpus')return r.rows.map(row=>{const result=K.OrC.compile(row.text,{cardType:row.cardType},{pool:r.pool||[]});return {id:row.id,complete:result.complete,diagnostics:result.diagnostics};});
      if(r.method==='importRows'){
        const parsed=K.cardImport.parse(r.text,r.filename||(/^[\\s\\uFEFF]*[\\[{]/.test(r.text)?'cards.json':'cards.csv'));
        return parsed.cards.map(raw=>{
          let value=raw;
          if(raw.json&&!raw.json.title){value={...raw.json};if(value.text&&typeof value.text==='object')value.text=value.text['zh-Hans']||'';}
          else if(raw.json)value={...raw,cardId:raw.cardId||raw.json.id};
          if(['infantry','tank','artillery','fighter','bomber'].includes(value.type))value={...value,cardType:'unit',unitType:value.type};
          return K.cardImport.normalize(value);
        });
      }
      const pool=r.pool||[];K.setPool(pool);
      const card=r.card||{cardType:'unit'};
      let result;
      if(card.effects?.length){
        const plan=K.cardImport.plan([card],{pool:pool.filter(c=>c.id!==card.id)}),entry=plan.entries[0];
        if(!entry)throw Error(JSON.stringify(plan.rejected));
        result={version:K.OrC.version,source:card.text||'',effects:entry.draftEffects||entry.card.effects,cardFields:{},keywords:entry.card.keywords||[],kwMap:entry.card.kwMap||{},kwValues:entry.card.kwValues||{},complete:plan.ready.length===1,diagnostics:entry.messages.map(message=>({code:'card-import',message}))};
      }else result=K.OrC.compile(r.text||'',card,{pool,atomicOnly:false});
      result.kwMap={...card.kwMap,...result.kwMap};result.kwValues={...card.kwValues,...result.kwValues};
      return result;
    })()