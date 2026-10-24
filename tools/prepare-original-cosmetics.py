"""Assemble verified original image/font assets for Godot. No drawing or generated textures."""
from pathlib import Path
import hashlib
import io
import json
import re
import shutil
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
EXTRACTED=ROOT/'artifacts/original-install-assets'
OUT=ROOT/'proto/art/original-cosmetics'
OUT.mkdir(parents=True,exist_ok=True)
CACHE={p['Name']:p for p in json.loads((OUT/'native-provenance.json').read_text(encoding='utf-8-sig'))} if (OUT/'native-provenance.json').exists() else {}

def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def write(name,data):(OUT/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
def field(row,prefix,default=None):return next((v for k,v in row.items() if k.startswith(prefix+'_')),default)
def text(value):return (value or {}).get('LocalizedString') or (value or {}).get('SourceString') or (value or {}).get('CultureInvariantString') or ''
def image_file(path,name,crop=False):
    original=path.read_bytes();source_hash=hashlib.sha256(original).hexdigest().upper()
    cached=CACHE.get(name)
    if cached and cached['SourceSha256']==source_hash and (ROOT/cached['Resource'].removeprefix('res://')).exists():
        return {k:cached[k] for k in ['Image','Resource','SourceSha256','Crop','Width','Height']}
    im=Image.open(io.BytesIO(original)).convert('RGBA');box=im.getchannel('A').getbbox() if crop else None
    if box:im=im.crop(box)
    im.thumbnail((2048,2048),Image.Resampling.LANCZOS)
    while True:
        buf=io.BytesIO();im.save(buf,'PNG',optimize=True);data=buf.getvalue()
        if len(data)<=2*1024*1024:break
        im=im.resize((int(im.width*.85),int(im.height*.85)),Image.Resampling.LANCZOS)
    dest=OUT/'native'/f'{name}.png';dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
    return {'Image':hashlib.sha256(data).hexdigest().upper(),'Resource':'res://'+dest.relative_to(ROOT).as_posix(),
            'SourceSha256':hashlib.sha256(original).hexdigest().upper(),'Crop':box,'Width':im.width,'Height':im.height}

catalog=[r for r in read(OUT/'catalog.json') if r['Kind']=='back']
ui={};native_by_asset={};provenance=[]
for entry in read(EXTRACTED/'textures.json'):
    if '/BoardProps/' in entry['Asset']:continue
    path=Path(entry['File']);name=path.stem
    item=image_file(path,name,'/emoji/' in entry['Asset'] or '/Icons/' in entry['Asset'])
    native_by_asset[entry['Asset'].removesuffix('.uasset')]=item
    provenance.append(dict(item,Asset=entry['Asset'],Archive='kards-Windows.pak',Name=name))
    if '/Images/HQs/' in entry['Asset']:
        catalog.append(dict(item,Kind='hq',Name=name,BuiltIn=True,Source=entry['Asset']))
    elif '/CustomizeBattlefields/' in entry['Asset']:
        catalog.append(dict(item,Kind='battlefield',Name=name,BuiltIn=True,Source=entry['Asset']))
    else:ui[name]=item['Resource']

def normalize_asset(value):
    path=(value or {}).get('AssetPathName','').split('.')[0]
    return 'kards/Content/'+path.removeprefix('/Game/') if path else ''

emotes=[]
for key,row in read(EXTRACTED/'emojiList.json')[0]['Rows'].items():
    label=text(field(row,'text'))
    if not label:continue
    source=normalize_asset(field(row,'emoteTexture'))
    item=native_by_asset.get(source) or native_by_asset.get('kards/Content/Assets/Textures/emoji/new_emote_base_1-01')
    variant=native_by_asset.get(normalize_asset(field(row,'emoteTextureVariant')))
    style='special' if str(field(row,'emoteStyle','')).endswith('NewEnumerator1') else 'paper'
    tint=field(row,'fontColor',{}).get('Hex','000000')
    emotes.append({'Id':'original-'+hashlib.sha256(key.encode()).hexdigest()[:16],
        'Label':label.replace(';','\n').strip(), 'Image':item['Image'], 'Style':style,'Tint':tint,
        'OriginalId':key,'Title':text(field(row,'Title')) or label.replace(';',' '),
        'Nations':[str(f).split('::')[-1].lower() for f in field(row,'factions',[])],
        'Group':str(field(row,'group','')).split('::')[-1], 'Resource':item['Resource'],'EnemyImage':variant['Image'] if variant else ''})
    catalog.append(dict(item,Kind='emote',Name=emotes[-1]['Title'],BuiltIn=True,Source=source))

props=[]
preview_file=OUT/'models/previews.json'
if preview_file.exists():
    for prop in read(preview_file):
        path=ROOT/prop['File'].removeprefix('res://')
        item=image_file(path,'prop-'+prop['Name'],True)
        catalog.append(dict(item,Kind='decoration',Name=prop['Name'],BuiltIn=True,Source='original mesh render'))
        props.append({'Image':item['Image'],'Name':prop['Name'],'Parts':[p['Resource'] for p in prop['Parts']]})
        provenance.append(dict(item,Name='prop-'+prop['Name'],Asset=prop['Parts'],Archive='kards-Windows.pak'))
write('props.json',props)

fontdir=OUT/'fonts';fontdir.mkdir(exist_ok=True)
for name in ['NotoSansSC-Regular','NotoSansSC-Bold','Courier_0_Normal','Courier_0_Bold','D_Day_Stencil','Victoria_Typewriter_Regular_Tight__1_']:
    source=EXTRACTED/'fonts'/f'{name}.ttf'
    if source.exists():
        shutil.copy2(source,fontdir/source.name)
        shutil.copy2(source,fontdir/(source.name+'.bin'))
payload=OUT/'payload';payload.mkdir(exist_ok=True)
for item in catalog+provenance:
    source=ROOT/item['Resource'].removeprefix('res://')
    dest=payload/(item['Image']+'.bin')
    if not dest.exists():shutil.copy2(source,dest)
    item['Payload']='res://'+dest.relative_to(ROOT).as_posix()
for name,resource in list(ui.items()):
    item=next(p for p in provenance if p['Resource']==resource)
    ui[name]=item['Payload']
for emote in emotes:
    item=next(p for p in provenance if p['Image']==emote['Image'])
    emote['Payload']=item['Payload']
write('runtime-catalog.json',catalog);write('ui.json',ui);write('emotes.json',emotes);write('native-provenance.json',provenance)
print(f'ORIGINAL_RUNTIME_CATALOG items={len(catalog)} emotes={len(emotes)} ui={len(ui)} native-images={len(provenance)}')
