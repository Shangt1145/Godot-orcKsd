# 对战界面生成素材

本轮使用内置 `image_gen`，生成原始位图，复制入源码；原始输出仍保留。卡背和爆炸的 alpha 原样保留。原版调研图片仅为材质与构图参照。

| 资源 | 尺寸 | 用途 |
|---|---|---|
| [tabletop-v2.png](tabletop-v2.png) | 1672 × 941 | 全窗口木桌与外围道具 |
| [card-back-v2.png](card-back-v2.png) | 1060 × 1484 | 对手手牌和倾斜牌堆 |
| [explosion-v2.png](explosion-v2.png) | 1254 × 1254 | 枪口、命中、死亡、烟尘与总部爆炸的纹理层 |
| [hq-map-v2.png](hq-map-v2.png) | 1086 × 1448 | 总部地图底图，标题/徽记/防御盾另绘 |

完整提示词、原始路径、参照文件、SHA256 与 alpha 设置也保存在 `assets.json`。

## tabletop-v2.png

生成模式：内置 image_gen。

```text
Use case: historical-scene. Asset type: production background texture for a desktop WWII card game's battlefield, landscape 16:9. Create a NEW original asset using the provided KARDS screenshot only as a reference for top-down tabletop composition, material realism, lighting and muted colors. Show a worn 1940s dark walnut military planning desk viewed perfectly straight down, horizontal wide wooden planks, deep grain, small scratches, uneven patina, warm restrained diffuse light, dark vignette at the far corners. Keep the whole central playing area (x 17%-83%, y 8%-94%) empty and flat wood, medium brown and readable, without clutter. A partially cropped olive map notebook and two brass cartridges in the far upper left, a partially cropped period pistol and leather holster at the far right edge, a cropped pencil along left edge, a tiny fold of canvas upper right. Props stay exclusively at the outermost edges, as in the reference. Realistic tactile wood, brass, paper, leather. NO cards, card backs, chips, resource counters, UI, buttons, text, numbers, dividers, triangles, logos or watermark anywhere. This is a game background asset, not a screenshot or UI mockup. High detail, understated military atmosphere.
```

## card-back-v2.png

生成模式：内置 image_gen。

```text
Use case: historical-scene. Asset type: a single usable portrait card-back texture for a WWII desktop card game. Generate a new card back referencing only the olive-backed cards visible along the top of the supplied KARDS screenshot: worn olive military cardstock, cream thin double inset rectangular border, tiny cream circular dots near the four corners, faded cream eight-point compass rose centered slightly below mid-height. Straight orthographic flat scan, NO perspective and no drop shadow. One single portrait rectangular card, width:height approximately 5:7, fills almost the whole image, rounded corners radius about 1% of width. Opaque textured olive paper inside the card, truly transparent outside the card. Balanced subdued colors, scuffed cream edges, restrained patina, fine paper fibers and tiny scratches. Keep the layout and print vocabulary very close to the reference, no redesign or new emblem. No letters, logos, numbers, title, text, extra cards, game UI, weapon, table, props, watermark or checkerboard backdrop. Production-ready isolated game asset.
```

## explosion-v2.png

生成模式：内置 image_gen。

```text
Use case: stylized-concept. Asset type: one production VFX sprite for a realistic WWII card game's explosion, square image on truly transparent background. An isolated small dense fireball impact/explosion, brilliant pale yellow core, orange ember tongues, turbulent charcoal-gray and warm brown smoke puffs around the fire, a few dark shards and sparks. Detailed photographic volume and irregular organic edges, compact spherical burst centered in image with about 20% clear transparent padding on all sides. Restrained realistic battlefield smoke and warm fire suitable for KARDS tabletop card combat. Not a cartoon, not neon, not a sci-fi spell, no circular halo, no shockwave ring, no bloom covering the full image. No ground, sky, table, cards, text, logos, UI, checkerboard pattern or cast shadow. Sprite must have real alpha with softly fading smoke edges. Single burst sprite only; no sheet or multiple panels.
```

## hq-map-v2.png

生成模式：内置 image_gen。

```text
Create one new portrait 3:4 flat scan of a WWII military topographic map on aged cream and olive paper, as a texture for a small headquarters card in a WWII card game. Use the attached official KARDS gameplay image only for material, map print density, restrained color and wear reference; do not copy its city names, UI, cards, logos or emblems. The output is ONLY the unframed map paper texture, fully filling the canvas, straight-on, no perspective. Fine dense brown-olive contour lines, old rural road networks, faint rivers and terrain marks, low contrast ink, muted beige parchment with genuine fibers, flecks and worn edges. No text, digits, symbols, emblems, borders, drop shadows, objects or UI. Keep center around 45% height light enough for a separately drawn emblem and bottom 20% for a separately drawn shield. Dark ink should remain subtle at thumbnail scale. Opaque background.
```
