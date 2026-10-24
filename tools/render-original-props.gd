extends SceneTree

func _initialize():
    call_deferred("render_props")

func render_props():
    var folder="res://proto/art/original-cosmetics/models/"
    var groups=JSON.parse_string(FileAccess.get_file_as_string(folder+"props.json"))
    var previews=[]
    for group in groups:
        var viewport=SubViewport.new()
        viewport.size=Vector2i(512,512)
        viewport.transparent_bg=true
        viewport.own_world_3d=true
        viewport.render_target_update_mode=SubViewport.UPDATE_ALWAYS
        root.add_child(viewport)
        var model=Node3D.new()
        viewport.add_child(model)
        for part in group.Parts:
            var state=GLTFState.new()
            var document=GLTFDocument.new()
            if document.append_from_buffer(FileAccess.get_file_as_bytes(part.Resource),"",state)!=OK:
                push_error("Original mesh failed: "+part.Resource)
                quit(1)
                return
            model.add_child(document.generate_scene(state))
        var bounds=mesh_bounds(model,Transform3D.IDENTITY)
        var center=bounds.get_center()
        var radius=max(bounds.size.length(),0.01)
        var camera=Camera3D.new()
        viewport.add_child(camera)
        camera.projection=Camera3D.PROJECTION_ORTHOGONAL
        camera.size=radius*1.15
        camera.near=0.001
        camera.position=center+Vector3(0,radius*1.2,radius*0.8)
        camera.look_at(center,Vector3.UP)
        camera.current=true
        var key=DirectionalLight3D.new()
        key.rotation_degrees=Vector3(-50,-25,0)
        key.light_energy=1.7
        viewport.add_child(key)
        var environment=WorldEnvironment.new()
        environment.environment=Environment.new()
        environment.environment.ambient_light_source=Environment.AMBIENT_SOURCE_COLOR
        environment.environment.ambient_light_color=Color.WHITE
        environment.environment.ambient_light_energy=0.55
        viewport.add_child(environment)
        await process_frame
        await process_frame
        await RenderingServer.frame_post_draw
        var destination=folder+group.Name+".png"
        viewport.get_texture().get_image().save_png(destination)
        previews.append({"Name":group.Name,"File":destination,"Parts":group.Parts})
        viewport.queue_free()
        await process_frame
    var file=FileAccess.open(folder+"previews.json",FileAccess.WRITE)
    file.store_string(JSON.stringify(previews,"  "))
    print("ORIGINAL_PROP_PREVIEWS ",previews.size())
    quit()

func mesh_bounds(node:Node, parent_transform:Transform3D)->AABB:
    var transform=parent_transform
    if node is Node3D:transform=parent_transform*node.transform
    var result=AABB()
    if node is MeshInstance3D:result=transform*node.get_aabb()
    for child in node.get_children():
        var child_bounds=mesh_bounds(child,transform)
        if child_bounds.size.length()>0:result=child_bounds if result.size.length()==0 else result.merge(child_bounds)
    return result
