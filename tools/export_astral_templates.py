"""Export native Astral PSD master layers without generated or painted replacement art.
Requires psd-tools and Pillow. Source PSDs are read only.
"""
import argparse
import hashlib
import json
from pathlib import Path
from PIL import Image
from psd_tools import PSDImage

parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--output', type=Path, default=Path('proto/assets/cardface/astral'))
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
manifest = {'canvas': [500, 701], 'sources': [], 'layers': {}}

def child(group, name):
    return next(layer for layer in group if layer.name == name)

def paste(canvas, layer):
    canvas.alpha_composite(layer.topil().convert('RGBA'), (layer.left, layer.top))

def save(image, name, source, layers):
    image.save(args.output / name)
    manifest['layers'][name] = {'source': source, 'layers': layers, 'size': list(image.size)}

for kind, key in [('单位', 'unit'), ('指令', 'order')]:
    path = args.source / f'Astral{kind}模板.psd'
    psd = PSDImage.open(path)
    if psd.size != (500, 701):
        raise ValueError(f'Unexpected canvas: {path}: {psd.size}')
    manifest['sources'].append({'name': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
    base = Image.new('RGBA', psd.size)
    paste(base, child(psd, '边框'))
    # K is now live typography: single- and double-digit costs use distinct PSD styles.
    if key == 'unit':
        save(base, 'unit-master.png', path.name, ['边框'])
        names = {'步兵': 'infantry', '坦克': 'tank', '炮兵': 'artillery', '战斗机': 'fighter', '轰炸机': 'bomber',
                 '太空战机': 'space-fighter', '巡地舰': 'landship', '巡航舰': 'cruiser'}
        for name, token in names.items():
            layer = child(child(psd, '单位类型'), name)
            badge = Image.new('RGBA', psd.size)
            paste(badge, layer)
            save(badge, f'unit-{token}.png', path.name, ['单位类型/' + name])
    else:
        for name, token in [('指令', 'order'), ('反制指令', 'counter')]:
            master = base.copy()
            paste(master, child(child(psd, '类型'), name))
            save(master, token + '-master.png', path.name, ['边框', '类型/' + name])
(args.output / 'sources.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Exported {len(manifest["layers"])} native layers to {args.output}')


