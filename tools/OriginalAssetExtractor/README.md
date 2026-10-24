# Original KARDS asset extraction

Read-only source: `K:\SteamLibrary\steamapps\common\KARDS\kards\Content\Paks`.
This tool uses CUE4Parse / CUE4Parse-Conversion 1.2.2.202610, SDK 10.0.400.
Run from this directory so its `global.json` selects the required SDK.

```powershell
dotnet run -- '<installed Content\Paks>' '<output directory>' '<local key file>'
dotnet run -- '<installed Content\Paks>' '<output directory>' '<local key file>' --metadata-only
```

The key file contains one locally supplied candidate per line. Never add keys or
candidate files to source control. The output directory is ignored under
`artifacts/original-install-assets`. No installed source files are modified.

Place the public `Kards.usmap` mapping file at `<output>/Kards-legacy.usmap`.
Source: https://cdn.jsdelivr.net/gh/GMatrixGames/TempMappingsRepo@master/Kards.usmap

The installed build uses UE 5.6. The available mapping has outdated native texture
and static-mesh property layouts. The tool recovers texture platform data from the
native pixel-format marker and cooked mesh render data from validated native
headers (editor strip flags, cooked flag, package references, socket count and LOD
count). This is specific to the inspected build, not a general-purpose UE parser.
Failed exports are reported, not replaced with generated artwork.

Font payloads have a serialized length prefix; only the declared font bytes are
written. Localization is loaded after mount using `zh-Hans`.

Follow extraction with these workspace-root commands:

```powershell
python tools/prepare-original-props.py
& '<Godot console executable>' --path . --script res://tools/render-original-props.gd
python tools/prepare-original-cosmetics.py
```

The prop preparation step only accepts groups with an unambiguous shared original
color atlas and one material per mesh. Other groups need explicit material and
component configuration. It embeds original color/normal/packed material textures
in GLB files, converting DirectX normal-map Y and packed RMA channels to glTF
conventions. The preview renderer renders those actual meshes with transparency.

`native-provenance.json`, `runtime-catalog.json`, model `props.json` and the raw
extraction manifest record source paths and SHA-256 hashes. Raw PNG/font/GLB
payloads use `.bin` so Godot exports preserve bytes needed by `FileAccess`.
