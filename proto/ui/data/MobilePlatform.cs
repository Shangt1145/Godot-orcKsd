using Godot;

namespace Kards.Ui;

public static class MobilePlatform
{
    public static bool IsAndroid => OS.HasFeature("android");
    internal static string DocumentUri(string path) { var uri = Service().Call("documentUri", Activity(), path).AsString(); CheckJava(); return uri; }
    public static string ScriptReferences()
    {
        var directory = Service().Call("scriptReferences", Activity()).AsString(); CheckJava();
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Android 效果编译引用缺失。");
        // Engine templates, keyword prefabs and plugin assets use the standard physical base directory.
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", directory + System.IO.Path.DirectorySeparatorChar);
        return directory;
    }
    private static GodotObject Activity() => Engine.GetSingleton("AndroidRuntime").Call("getActivity").AsGodotObject();
    private static GodotObject Service() => JavaClassWrapper.Wrap("com.godot.game.RoomHostService") ?? throw new InvalidOperationException("Android 平台服务缺失，请使用完整安装包。");
    private static void CheckJava()
    {
        if (JavaClassWrapper.GetException() is { } error) throw new InvalidOperationException("Android 平台服务失败：" + error.Call("toString").AsString());
    }
    public static void SetHosting(bool hosting)
    {
        if (!IsAndroid) return;
        if (hosting) Service().Call("start", Activity()); else Service().Call("stop", Activity());
        CheckJava();
    }
    public static void InstallApk(string uri) { Service().Call("installApk", Activity(), uri); CheckJava(); }
    public static void SetDiscovery(bool enabled) { if (IsAndroid) { Service().Call("setDiscovery", Activity(), enabled); CheckJava(); } }
}
