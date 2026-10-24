extends SceneTree

func _initialize():
    var prefix="res://proto/art/original-cosmetics/"
    var catalog=JSON.parse_string(FileAccess.get_file_as_string(prefix+"runtime-catalog.json"))
    var counts={}
    var decoded={}
    for entry in catalog:
        if not FileAccess.file_exists(entry.Payload):
            push_error("Missing original payload in exported pack: "+entry.Payload)
            quit(1)
            return
        counts[entry.Kind]=counts.get(entry.Kind,0)+1
        if not decoded.has(entry.Kind):
            var image=Image.new()
            var bytes=FileAccess.get_file_as_bytes(entry.Payload)
            var digest=HashingContext.new()
            digest.start(HashingContext.HASH_SHA256)
            digest.update(bytes)
            if digest.finish().hex_encode().to_upper()!=entry.Image or image.load_png_from_buffer(bytes)!=OK:
                push_error("Original payload failed integrity/decode: "+entry.Name)
                quit(1)
                return
            decoded[entry.Kind]=true
    var emotes=JSON.parse_string(FileAccess.get_file_as_string(prefix+"emotes.json"))
    var variants=0
    for entry in emotes:
        if entry.EnemyImage!="":
            variants+=1
            if not FileAccess.file_exists(prefix+"payload/"+entry.EnemyImage+".bin"):
                push_error("Missing opponent emote texture in exported pack")
                quit(1)
                return
    var font=FontFile.new()
    font.data=FileAccess.get_file_as_bytes(prefix+"fonts/NotoSansSC-Regular.ttf.bin")
    if font.get_string_size("原版表情",HORIZONTAL_ALIGNMENT_LEFT,-1,20).x<=0:
        push_error("Original Chinese font failed in exported pack")
        quit(1)
        return
    print("ORIGINAL_PACK_VERIFY_OK ",JSON.stringify(counts)," emote-variants=",variants," chinese-font=ok")
    quit()
