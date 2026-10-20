"""Read the Astral PSD text metrics and cached reference glyphs; never modify PSDs."""
import argparse, hashlib, json
from pathlib import Path
from PIL import Image
from psd_tools import PSDImage

p=argparse.ArgumentParser()
p.add_argument('--source',type=Path,required=True)
p.add_argument('--output',type=Path,default=Path('proto/assets/cardface/astral/typography.json'))
p.add_argument('--references',type=Path)
a=p.parse_args()
def layer(root,path):
    for part in path.split('/'):
        root=next(x for x in root if x.name==part)
    return root

def style(t):
    s=t.engine_dict['StyleRun']['RunArray'][0]['StyleSheet']['StyleSheetData']
    tr=t.transform; color=[float(v) for v in s['FillColor']['Values']]
    primary=t.resource_dict['FontSet'][int(s['Font'])]['Name'].value
    return {'fonts':[primary]+[n for n in t.font_names if n!=primary],'size':float(s['FontSize'])*abs(tr[3]),'baseline':list(tr[4:]),
        'horizontalScale':float(s.get('HorizontalScale',1)),'tracking':int(s.get('Tracking',0)),
        'leading':float(s.get('Leading',0))*abs(tr[3]),'color':color[1:]+color[:1],
        'bbox':list(t.bbox),'sample':t.text.replace('\r','\n')}

result={'canvas':[500,701],'sources':[],'masters':{}}
for kind,key in [('单位','unit'),('指令','order')]:
    path=a.source/f'Astral{kind}模板.psd';psd=PSDImage.open(path)
    result['sources'].append({'name':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    paths={'name':'名称和效果/有词条/卡名','namePlain':'名称和效果/有词条/卡名' if key=='unit' else '名称和效果/无词条/卡名',
        'keyword':'名称和效果/有词条/词条','body':'名称和效果/有词条/效果描述','bodyPlain':'名称和效果/无词条/效果描述',
        'cost':'费用/0-9费/'+('部署费用' if key=='unit' else '费用'),
        'costDouble':'费用/10-99费/'+('部署费用' if key=='unit' else '费用'),
        'k':'费用/0-9费/K','kDouble':'费用/10-99费/K'}
    if key=='unit':paths.update({'opCost':'费用/0-9费/行动费用','opCostDouble':'费用/10-99费/行动费用',
        'attack':'数值/0-9点/攻击力','defense':'数值/0-9点/2','defenseDouble':'数值/10-99点/防御力'})
    result['masters'][key]={name:style(layer(psd,path))|{'layer':path} for name,path in paths.items()}
    if a.references:
        a.references.mkdir(parents=True,exist_ok=True)
        chosen=['name','keyword','body','cost','opCost','attack','defense','k'] if key=='unit' else ['namePlain','bodyPlain','cost','k']
        reference=Image.new('RGBA',psd.size)
        for name in chosen:
            t=layer(psd,paths[name]);reference.alpha_composite(t.topil().convert('RGBA'),(t.left,t.top))
        reference.save(a.references/f'{key}-psd-text.png')
        (a.references/f'{key}-input.json').write_text(json.dumps({name:result['masters'][key][name] for name in chosen},ensure_ascii=False,indent=2),encoding='utf-8')
result['fonts']={}
for name in ['Roboto-Black.ttf','SiYuanHeiTi-Normal.otf','SourceHanSansCN-Bold.otf','FRADMCN.TTF','FRAMDCN.TTF']:
    f=a.source/'模板字体'/name
    result['fonts'][name]=hashlib.sha256(f.read_bytes()).hexdigest()
a.output.parent.mkdir(parents=True,exist_ok=True)
a.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print('ASTRAL_TYPOGRAPHY_EXTRACTED')
