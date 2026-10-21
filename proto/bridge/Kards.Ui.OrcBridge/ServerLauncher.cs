using Orc.Client;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 拉起本地 <c>Orc.Server</c> 子进程——联机时房主机器上的"食堂"。
///
/// <para>
/// 约定（来自引擎接入指南）：stdout 首行 <c>{"ready":true,"port":N}</c> 即就绪，其余输出走 stderr；
/// 端口传 0 由系统分配，避免和玩家机器上别的东西撞端口。
/// </para>
///
/// <para>
/// **服务端可执行文件从哪来**，按顺序找：
/// ① 环境变量 <c>ORC_SERVER_DLL</c>（开发期最省事）；
/// ② 发行布局 <c>&lt;游戏目录&gt;/OrcServer/Orc.Server.dll</c>（打包时一并带上）；
/// ③ 开发布局 <c>ORC_ENGINE_ROOT</c> 指向的引擎源码里的构建产物。
/// 三条都落空就抛带指引的错误，而不是让玩家看到一个莫名的连接失败。
/// </para>
/// </summary>
public sealed class ServerLauncher : IAsyncDisposable
{
    private readonly ServerProcess _process;

    private ServerLauncher(ServerProcess process) => _process = process;

    /// <summary>实际监听端口（系统分配）。</summary>
    public int Port => _process.Port;

    /// <summary>服务端进程是否已经退出（房主关掉它就等于散场）。</summary>
    public bool HasExited => _process.HasExited;

    public static async Task<ServerLauncher> StartAsync(
        string packsDirectory, string sandboxDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxDirectory);

        if (!Directory.Exists(packsDirectory))
            throw new DirectoryNotFoundException($"卡包目录不存在：{packsDirectory}");

        Directory.CreateDirectory(sandboxDirectory);
        var dll = ResolveServerDll();

        var options = new ServerProcessOptions
        {
            ExecutablePath = "dotnet",
            Arguments = [dll, "--packs", packsDirectory, "--sandbox", sandboxDirectory, "--port", "0"],
            // 首次启动要装配卡包（项目里有 14 个包），15 秒的默认值不够宽裕。
            StartupTimeout = TimeSpan.FromSeconds(60),
            LogPath = Path.Combine(sandboxDirectory, "server.log"),
        };

        return new ServerLauncher(await ServerProcess.StartAsync(options, ct).ConfigureAwait(false));
    }

    private static string ResolveServerDll()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ORC_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var shipped = Path.Combine(AppContext.BaseDirectory, "OrcServer", "Orc.Server.dll");
        if (File.Exists(shipped)) return shipped;

        var engineRoot = Environment.GetEnvironmentVariable("ORC_ENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(engineRoot))
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var built = Path.Combine(engineRoot, "src", "Orc.Server", "bin", configuration, "net8.0", "Orc.Server.dll");
                if (File.Exists(built)) return built;
            }
        }

        throw new FileNotFoundException(
            "找不到 Orc.Server.dll。任选其一：设置环境变量 ORC_SERVER_DLL 指向它；" +
            $"或把服务端放到 {Path.Combine(AppContext.BaseDirectory, "OrcServer")}；" +
            "或设置 ORC_ENGINE_ROOT 指向引擎源码并先构建 Orc.Server。");
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
