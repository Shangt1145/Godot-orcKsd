using System.Reflection;
using System.Text.Json.Nodes;
using Kards.Ui.Core;
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
        // v4 删除的三个开关（原样记在这里，免得下次又去引擎里找）：
        //   PluginsRoot         —— 服务端不再收插件根目录；插件包只经 PackDirectories 发现。
        //   移动端内嵌服务端使用宿主的内存程序集适配；独立桌面服务端仍使用引擎缺省求值器。
        //   ReuseProcessVocabulary —— 进程级注册已前移到 ServerBootstrap（无条件执行），无需开关。
        if (inProcess || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            var packages = packsDirectories.Select(root => Orc.Packages.PackageManifest.Parse(File.ReadAllText(Path.Combine(root, "manifest.json"))))
                .Select(p => new PackageRef(p.Id, p.Version.ToString(), p.Guid.ToString())).ToArray();
            var host = await Orc.Server.ServerBootstrap.StartAsync(new Orc.Server.ServerOptions {
                PackDirectories = packsDirectories, SandboxRoot = sandboxDirectory,
                Port = port,
                ScriptEvaluator = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? NativeScriptHost.Evaluator : null,
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

        foreach (var engineRoot in EngineRoots())
        {
            // 两条判据同时成立才认（都是踩过的坑）：
            //
            // ① **必须是可运行目录**：引擎切到统一产物槽后，槽位里只有本工程自己的产物，
            //    依赖靠 deps.json 指回兄弟槽位；但一旦别的工程（例如本 UI）把它当 ProjectReference
            //    构建过一次，槽位里的依赖副本会被增量清理掉——目录看着在、dll 也在，一 spawn 就
            //    `Could not load file or assembly 'Orc.Game'`。判据＝同目录有 Orc.Game.dll（可运行目录的
            //    最低特征），缺了就不认，宁可直接报错也不要拉起一个装不起来的东西。
            //
            // ② **取最新**，不按固定顺序：`src/Orc.Server/bin/Debug/net8.0/` 是早期布局的产物，
            //    切槽位之后没人清理，里面留着的可能是一份**旧引擎**。按"先认旧布局"取第一个的话，
            //    桌面房主会静默跑起旧引擎：2026-10-29 实测，联机单测报的是 v3 时代才有的
            //    "座位 N 的卡包尚未同步完成"，而引擎源码里根本没有这句话——真凶只是那份没人删的旧 dll。
            var candidates = new List<string>();
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                // ① 旧布局：工程旁 bin（引擎早期用默认输出路径；自带完整依赖）。
                candidates.Add(Path.Combine(engineRoot, "src", "Orc.Server", "bin", configuration, "net8.0", "Orc.Server.dll"));
                // ② 现布局：引擎统一产物槽 `artifacts/slot/bin/<工程>/<配置小写>/`。
                candidates.Add(Path.Combine(engineRoot, "artifacts", "slot", "bin", "Orc.Server",
                    configuration.ToLowerInvariant(), "Orc.Server.dll"));
            }

            var found = candidates.Where(File.Exists)
                .Select(path => (Path: path, Stamp: File.GetLastWriteTimeUtc(path), Runnable: Runnable(path)))
                .OrderByDescending(c => c.Stamp)
                .ToArray();
            if (found.FirstOrDefault(c => c.Runnable).Path is { } chosen)
            {
                GameLog.Info("server", $"服务端程序：{chosen}（{File.GetLastWriteTimeUtc(chosen):yyyy-MM-dd HH:mm} UTC）");
                return chosen;
            }

            if (found.Length > 0)
            {
                // 有 dll 但目录装不起来：直接点名，别让玩家看到"开房失败"却说不出原因。
                throw new FileNotFoundException(
                    "找到 Orc.Server.dll 但所在目录不可运行（缺依赖程序集）："
                    + string.Join("、", found.Select(c => c.Path))
                    + "。请在引擎仓库执行 `dotnet build OrcEngine.sln -c Debug -t:Rebuild` 补齐槽位依赖，"
                    + "或 `dotnet publish src/Orc.Server -c Debug -o <目录>` 后把 ORC_SERVER_DLL 指过去。");
            }
        }

        throw new FileNotFoundException(
            "找不到 Orc.Server.dll。任选其一：设置环境变量 ORC_SERVER_DLL 指向它；" +
            $"或把服务端放到 {Path.Combine(AppContext.BaseDirectory, "OrcServer")}；" +
            "或设置 ORC_ENGINE_ROOT 指向引擎源码并先构建 Orc.Server。" +
            $"（本次已试过的引擎根：{string.Join("、", EngineRoots())}）");
    }

    /// <summary>
    /// 候选引擎仓库根：环境变量优先，其次**构建时写进程序集的元数据**。
    /// <para>
    /// 为什么要有第二个来源：构建侧 <c>OrcEngineRoot</c> 是按目录约定推出来的（见 csproj），
    /// 而运行期原先只认环境变量。两者不同源，就会出现"编译时找得到引擎、运行时说找不到服务端"——
    /// 2026-10-29 发版闸门正是栽在这里：<c>tools/release.ps1</c> 确实设了环境变量，
    /// 但测试宿主进程读不到，联机用例集体报"找不到 Orc.Server.dll"。
    /// 元数据由构建写入，与二进制一一对应，不会再和实际用哪套引擎脱节。
    /// </para>
    /// </summary>
    private static IEnumerable<string> EngineRoots()
    {
        var recorded = typeof(ServerLauncher).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "OrcEngineRoot")?.Value;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("ORC_ENGINE_ROOT"), recorded })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    /// <summary>
    /// 目录是否“可运行”：至少要有引擎核心程序集在同一目录。
    /// 产物槽位只保证本工程产物；依赖缺失时进程会在启动前就崩在 <c>Could not load file or assembly</c>，
    /// 所以这一条必须先判、且判在选路之前。
    /// </summary>
    private static bool Runnable(string serverDll)
        => Path.GetDirectoryName(serverDll) is { } directory && File.Exists(Path.Combine(directory, "Orc.Game.dll"));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_embedded is not null) await _embedded.DisposeAsync();
        if (_manager is not null) await _manager.DisposeAsync();
    }
}
