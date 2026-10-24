"""Embed original texture files in converted original meshes. No replacement artwork."""
from pathlib import Path
import hashlib
import io
import json
import struct
from PIL import Image

ROOT=Path(__file__).resolve().parents[1]
SOURCE=ROOT/'artifacts/original-install-assets'
OUT=ROOT/'proto/art/original-cosmetics/models'
OUT.mkdir(parents=True,exist_ok=True)
records=[]
for folder in sorted((SOURCE/'models/kards/Content/Assets/BoardProps').iterdir()):
    if not folder.is_dir() or folder.name=='BoardActorsSystem':continue
    textures=list((SOURCE/'textures/kards/Content/Assets/BoardProps'/folder.name).rglob('*.png'))
    diffuse=[p for p in textures if any(s in p.stem.lower() for s in ['basecolor','base_color','diffuse']) or p.stem.endswith('_BC')]
    # A single shared original atlas can be assigned without guessing between materials.
    if len(diffuse)!=1:continue
    parts=[]
    for model in sorted(folder.glob('*.glb')):
        raw=model.read_bytes();json_size=struct.unpack_from('<I',raw,12)[0]
        doc=json.loads(raw[20:20+json_size]);binary=bytearray(raw[28+json_size:])
        if len(doc.get('materials',[]))!=1:continue
        def embed(path,packing='color'):
            image=Image.open(path).convert('RGBA');image.thumbnail((2048,2048),Image.Resampling.LANCZOS)
            if packing=='rma':
                r,g,b,a=image.split();image=Image.merge('RGBA',(b,r,g,a))
            if packing=='directx-normal':
                r,g,b,a=image.split();image=Image.merge('RGBA',(r,g.point(lambda v:255-v),b,a))
            encoded=io.BytesIO();image.save(encoded,'PNG');png=encoded.getvalue()
            while len(binary)%4:binary.append(0)
            offset=len(binary);binary.extend(png)
            views=doc.setdefault('bufferViews',[]);view=len(views);views.append({'buffer':0,'byteOffset':offset,'byteLength':len(png)})
            images=doc.setdefault('images',[]);index=len(images);images.append({'bufferView':view,'mimeType':'image/png'})
            tex=doc.setdefault('textures',[]);texture_index=len(tex);tex.append({'source':index})
            return texture_index
        texture_index=embed(diffuse[0])
        material=doc['materials'][0];pbr=material.setdefault('pbrMetallicRoughness',{})
        pbr['baseColorTexture']={'index':texture_index};pbr['metallicFactor']=0;pbr['roughnessFactor']=0.8
        packed=[p for p in textures if p.stem.endswith(('_ORM','_RoughnessMetallicAO'))]
        if len(packed)==1:
            texture_index=embed(packed[0],'rma' if packed[0].stem.endswith('_RoughnessMetallicAO') else 'color')
            pbr['metallicRoughnessTexture']={'index':texture_index};pbr['metallicFactor']=1;pbr['roughnessFactor']=1
            material['occlusionTexture']={'index':texture_index}
        normals=[p for p in textures if p.stem.endswith(('_Normal','_Normal_OpenGL'))]
        if len(normals)==1:material['normalTexture']={'index':embed(normals[0],'color' if normals[0].stem.endswith('_OpenGL') else 'directx-normal')}
        material['doubleSided']=True
        while len(binary)%4:binary.append(0)
        doc['buffers'][0]['byteLength']=len(binary)
        data=json.dumps(doc,separators=(',',':')).encode();data+=b' '*((-len(data))%4)
        glb=struct.pack('<III',0x46546C67,2,28+len(data)+len(binary))+struct.pack('<II',len(data),0x4E4F534A)+data+struct.pack('<II',len(binary),0x004E4942)+binary
        destination=OUT/(folder.name+'-'+model.stem+'.model.bin');destination.write_bytes(glb)
        parts.append({'Resource':'res://'+destination.relative_to(ROOT).as_posix(),'Source':str(model.relative_to(SOURCE)),'MeshSha256':hashlib.sha256(raw).hexdigest().upper(),'Texture':str(diffuse[0].relative_to(SOURCE)),'TextureSha256':hashlib.sha256(diffuse[0].read_bytes()).hexdigest().upper(),'Sha256':hashlib.sha256(glb).hexdigest().upper()})
    if parts:records.append({'Name':folder.name,'Parts':parts})
(OUT/'props.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'ORIGINAL_PROP_MODELS groups={len(records)} parts={sum(len(g["Parts"]) for g in records)}')
