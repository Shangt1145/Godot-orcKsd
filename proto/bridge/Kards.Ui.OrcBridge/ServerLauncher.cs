using System.Text.Json.Nodes;
using Orc.Manager;
using Orc.Protocol;

namespace Kards.Ui.OrcBridge;

/// <summary>房主私有管理通道；业务连接无法调用管理命令。</summary>
public sealed class ServerLauncher : IAsyncDisposable
{
    private readonly ServerManager? _manager;
    private readonly Orc.Server.Transport.ServerHost? _embedded;
    private readonly string _id;
    private readonly IReadOnlyList<PackageRef> _packages;
    public int Port { get; }
    public bool HasExited => _disposed || (_manager is not null && !_manager.List().Any(i => i.InstanceId == _id && i.Running));
    /// <summary>
    /// 服务端子进程 PID（内嵌模式＝0，Android 走内嵌）。
    /// 排障要它：进程和"管理通道里的实例"不是一回事，子进程赖在后台占端口时只有 PID 能对症。
    /// </summary>
    public int ProcessId => _manager?.List().FirstOrDefault(i => i.InstanceId == _id)?.ProcessId ?? 0;
    private bool _disposed;
    private ServerLauncher(ServerManager manager, string id, int port, IReadOnlyList<PackageRef> packages)
        => (_manager, _id, Port, _packages) = (manager, id, port, packages);
    private ServerLauncher(Orc.Server.Transport.ServerHost host, IReadOnlyList<PackageRef> packages)
        => (_embedded, _id, Port, _packages) = (host, "embedded", host.Port, packages);
    public static Task<ServerLauncher> StartAsync(string packsDirectory, string sandboxDirectory, CancellationToken ct = default)
        => StartAsync([packsDirectory], sandboxDirectory, ct);
    public static async Task<ServerLauncher> StartAsync(IReadOnlyList<string> packsDirectories, string sandboxDirectory,
        CancellationToken ct = default, int? aiSeat = null, IReadOnlyList<string>? aiDeck = null,
        bool fixedDeckForVerification = false, bool inProcess = false, int port = 0)
    {
        // port＝0 让系统分配（默认）；非 0 就是玩家要求的固定端口，被占用时启动会失败，
        // 由调用方决定"换端口重试"还是"报错"——这里不替玩家静默换端口。
        if (inProcess || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            var packages = packsDirectories.Select(root => Orc.Packages.PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json"))))
                .Select(p => new PackageRef(p.Id, p.Version.ToString(), p.Guid.ToString())).ToArray();
            var host = await Orc.Server.ServerBootstrap.StartAsync(new Orc.Server.ServerOptions {
                PackDirectories = packsDirectories, SandboxRoot = sandboxDirectory, PluginsRoot = PluginManifests.ResolveRoot(),
                ScriptEvaluator = NativeScriptHost.Evaluator,
                Port = port,
                ReuseProcessVocabulary = true,
                DeckRules = fixedDeckForVerification ? Orc.Game.Decks.DeckRuleTier.None : Orc.Game.Decks.DeckRuleTier.Strict,
                Seats = Enumerable.Range(0, 2).Select(n => new Orc.Server.SeatConfig(
                    n == aiSeat ? Orc.Server.SeatKind.Ai : Orc.Server.SeatKind.Agent,
                    DisplayName: n == aiSeat ? "引擎 AI" : null, Packages: packages, Deck: n == aiSeat ? aiDeck : null)).ToArray()
            }, ct).ConfigureAwait(false);
            return new ServerLauncher(host, packages);
        }
        EnableBundledRuntime();
        var manager = new ServerManager(ResolveServerDll());
        try
        {
            var registered = manager.Assets.Register(packsDirectories);
            if (registered.Rejected.Count > 0) throw new InvalidOperationException(string.Join("；", registered.Rejected));
            var packages = registered.Registered.Select(p => new PackageRef(p.PackageId, p.Version, p.Guid)).ToArray();
            var id = Guid.NewGuid().ToString("N");
            var reply = await manager.SpawnAsync(new SpawnRequest {
                InstanceId = id, PackDirectories = packsDirectories, SandboxRoot = sandboxDirectory,
                Port = port,
                DeckRules = fixedDeckForVerification ? "none" : "strict",
                RunDir = Path.Combine(sandboxDirectory, "instances", id), StartupTimeout = TimeSpan.FromSeconds(60),
                Seats = Enumerable.Range(0, 2).Select(n => new SeatSpawn {
                    Kind = n == aiSeat ? "ai" : "agent", DisplayName = n == aiSeat ? "引擎 AI" : null,
                    Deck = n == aiSeat ? aiDeck : null, Packages = packages }).ToArray()
            }, ct).ConfigureAwait(false);
            if (!reply.Ok) throw new InvalidOperationException(reply.Message ?? reply.Code);
            return new ServerLauncher(manager, id, reply.Payload!["port"]!.GetValue<int>(), packages);
        }
        catch { await manager.DisposeAsync(); throw; }
    }
    public void Configure(RemoteMatchFeed feed)
    {
        feed.PackageDeclaration = _packages.Select(p => (p.PackageId, p.Version, p.Guid)).ToArray();
        feed.StartHostMatch = StartMatchAsync;
    }
    public async Task<RemoteToolResult> AdminAsync(string op, JsonObject? args = null, CancellationToken ct = default)
    {
        if (_embedded is not null)
        {
            var embedded = await _embedded.Admin.InvokeAsync(op, args, ct).ConfigureAwait(false);
            return new(embedded.Ok, embedded.Code, embedded.Message, embedded.Payload);
        }
        var r = await _manager!.AdminAsync(_id, new ManagerAdminCall(op, args), ct).ConfigureAwait(false);
        return new(r.Ok, r.Code, r.Message, r.Payload);
    }
    public Task<RemoteToolResult> AdmitAsync(string connectionId, CancellationToken ct = default)
        => AdminAsync("conn.admit", new JsonObject { ["connectionId"] = connectionId }, ct);
    private async Task<bool> StartMatchAsync(CancellationToken ct)
    {
        // The server's mutable pool includes approved session packages received after spawn.
        var reply = await AdminAsync("match.start", ct: ct);
        if (!reply.Ok) throw new InvalidOperationException(reply.Message ?? reply.Code);
        return true;
    }
    private static IEnumerable<string> ApplicationRoots()
        => new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory }.Distinct();
    private static void EnableBundledRuntime()
    {
        foreach (var root in ApplicationRoots())
        {
            var runtime = Path.Combine(root, "OrcRuntime");
            if (!File.Exists(Path.Combine(runtime, "dotnet.exe"))) continue;
            // Process-local PATH only. ServerManager starts its private child via the dotnet host.
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(Path.PathSeparator).Contains(runtime, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", runtime + Path.PathSeparator + path);
            return;
        }
    }
    /// <summary>
    /// 本机能否充当房主：移动端使用内置服务，桌面端定位独立服务程序。
    /// </summary>
    public static bool CanHost
    {
        get
        {
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return true;
            try { ResolveServerDll(); return true; }
            catch (FileNotFoundException) { return false; }
        }
    }

    private static string ResolveServerDll()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ORC_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        foreach (var root in ApplicationRoots())
        {
            var shipped = Path.Combine(root, "OrcServer", "Orc.Server.dll");
            if (File.Exists(shipped)) return shipped;
        }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_embedded is not null) await _embedded.DisposeAsync();
        if (_manager is not null) await _manager.DisposeAsync();
    }
}
