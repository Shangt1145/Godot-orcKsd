'use strict';
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const names=['engine','primitives-extra','primitives','effects','effect-primitives','compiler','effect-contract','orc','card-import','effect-inspector'];
const source=process.argv.slice(2).find(x=>!x.startsWith('--'))||'H:/Working Folder/Kards-Desktop/electron/game';
const vendor=path.resolve(__dirname,'../proto/bridge/Kards.Ui.OrcBridge/DesktopAuthoring');
function load(root){const c=vm.createContext({console:{log(){},warn(){},error(){}}});for(const name of names)vm.runInContext(fs.readFileSync(path.join(root,'js',name+'.js'),'utf8'),c,{filename:name});return c;}
const original=load(source),ported=load(vendor);
const corpus=JSON.parse(fs.readFileSync(path.resolve(__dirname,'../tests/fixtures/official-raw.json'),'utf8')).cards.map(c=>c.json).filter(c=>c.text?.['zh-Hans']!==undefined);
const reviewed=new Map(JSON.parse(fs.readFileSync(path.resolve(__dirname,'../tests/fixtures/desktop-orc-hq-extensions.json'),'utf8')).map(c=>[c.id,c]));
const reference=[];let complete=0,portedComplete=0,changed=0;
const hash=s=>crypto.createHash('sha256').update(s).digest('hex').toUpperCase();
for(const card of corpus){
 const type=card.type==='order'?'order':card.type==='countermeasure'?'counter':'unit';
 const a=JSON.stringify(original.KG.OrC.compile(card.text['zh-Hans'],{cardType:type},{pool:[]}));
 const b=JSON.stringify(ported.KG.OrC.compile(card.text['zh-Hans'],{cardType:type},{pool:[]}));
 const extension=reviewed.get(card.id);
 if(extension){assert.equal(card.text['zh-Hans'],extension.text,card.id);assert.equal(hash(b),extension.sha256,card.id);assert.notEqual(a,b,card.id);changed++;}
 else assert.equal(b,a,card.id);
 reference.push({id:card.id,sha256:hash(a)});if(JSON.parse(a).complete)complete++;if(JSON.parse(b).complete)portedComplete++;
}
assert.equal(changed,reviewed.size);
const a=Object.keys(original.KG.effects.OPS).sort(),b=Object.keys(ported.KG.effects.OPS).sort();assert.deepEqual(a,b);
const report={corpus:corpus.length,identicalResults:corpus.length-changed,reviewedHqFixes:changed,sourceComplete:complete,portedComplete,sourceOperations:a.length,portedOperations:b.length};
fs.mkdirSync('artifacts/desktop-authoring',{recursive:true});fs.writeFileSync('artifacts/desktop-authoring/source-parity.json',JSON.stringify(report,null,2));console.log(JSON.stringify(report));
if(process.argv.includes('--write-reference'))fs.writeFileSync(path.resolve(__dirname,'../tests/fixtures/desktop-orc-reference.json'),JSON.stringify(reference,null,2));
