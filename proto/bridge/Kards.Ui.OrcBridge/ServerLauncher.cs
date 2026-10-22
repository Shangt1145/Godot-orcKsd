using System.Text.Json.Nodes;
using Orc.Manager;
using Orc.Protocol;

namespace Kards.Ui.OrcBridge;

/// <summary>房主私有管理通道；业务连接无法调用管理命令。</summary>
public sealed class ServerLauncher : IAsyncDisposable
{
    private readonly ServerManager _manager;
    private readonly string _id;
    private readonly IReadOnlyList<PackageRef> _packages;
    public int Port { get; }
    public bool HasExited => !_manager.List().Any(i => i.InstanceId == _id && i.Running);
    private ServerLauncher(ServerManager manager, string id, int port, IReadOnlyList<PackageRef> packages)
        => (_manager, _id, Port, _packages) = (manager, id, port, packages);
    public static Task<ServerLauncher> StartAsync(string packsDirectory, string sandboxDirectory, CancellationToken ct = default)
        => StartAsync([packsDirectory], sandboxDirectory, ct);
    public static async Task<ServerLauncher> StartAsync(IReadOnlyList<string> packsDirectories, string sandboxDirectory,
        CancellationToken ct = default, int? aiSeat = null, IReadOnlyList<string>? aiDeck = null)
    {
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
        var r = await _manager.AdminAsync(_id, new ManagerAdminCall(op, args), ct).ConfigureAwait(false);
        return new(r.Ok, r.Code, r.Message, r.Payload);
    }
    public Task<RemoteToolResult> AdmitAsync(string connectionId, CancellationToken ct = default)
        => AdminAsync("conn.admit", new JsonObject { ["connectionId"] = connectionId }, ct);
    private async Task<bool> StartMatchAsync(CancellationToken ct)
    {
        var plan = _manager.Assets.Plan(new PackageDeclaration("seat0", _packages), new PackageDeclaration("seat1", _packages));
        if (!plan.Ok) throw new InvalidOperationException(plan.Message ?? plan.Code);
        var reply = await AdminAsync("match.start", new JsonObject { ["plan"] = plan.Plan!.ToJson() }, ct);
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

    public ValueTask DisposeAsync() => _manager.DisposeAsync();
}
