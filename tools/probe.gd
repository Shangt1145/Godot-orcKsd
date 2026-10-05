extends SceneTree

func _initialize():
	print("TOOLCHAIN_PROBE csharp=", ClassDB.class_exists("CSharpScript"), " version=", Engine.get_version_info().string)
	quit()
