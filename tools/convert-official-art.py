"""Convert official Chinese full-card AVIF images to Godot's PNG texture format."""
import json
import sys
from pathlib import Path
from PIL import Image

directory = Path(sys.argv[1])
sources = json.loads((directory / 'sources.json').read_text(encoding='utf-8-sig'))
for card_id in sources:
    with Image.open(directory / (card_id + '.avif')) as image:
        image.load()
        if image.width < 100 or image.height < 100:
            raise ValueError(f'Invalid card image: {card_id}')
        image.convert('RGBA').save(directory / (card_id + '.png'))
print(f'PNG_CONVERSION_OK cards={len(sources)}')
