using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// M4 大厅·房间发现：**宣告式广播**（O(N)，来自 2026-10-21 锁步文档的结论——
/// 宣告优于逐台探测 O(N²)；虚拟局域网（ZeroTier / n2n 等开源组网）当免费房间服务器）。
///
/// <para>
/// 房主起服后每 2 秒向广播地址发一条 JSON；客人监听 UDP 端口、按 roomId 去重、
/// 超时剔除。发送同时打 127.255.255.255——同机探针走环回也能收到。
/// </para>
/// </summary>
public static class RoomDiscovery
{
    /// <summary>发现通道端口（UDP）。选个冷门口，避开引擎/游戏常用段。</summary>
    public const int DefaultPort = 47188;

    /// <summary>多久没听到宣告就算房间没了。</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(7);
}

/// <summary>一条房间宣告。发送方地址即房主地址，所以载荷里不带 IP。</summary>
public sealed record DiscoveryPayload(
    int V, string RoomId, int Port, int SeatsTaken, int SeatsTotal, string? Name)
{
    public const int CurrentVersion = 1;

    public string Encode() => JsonSerializer.Serialize(this);

    public static DiscoveryPayload? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<DiscoveryPayload>(text);
            return payload is { V: CurrentVersion, RoomId.Length: > 0, Port: > 0 } ? payload : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>客人口中"看到的一个房间"。</summary>
public sealed record DiscoveredRoom(
    string RoomId, string Host, int Port, int SeatsTaken, int SeatsTotal, string? Name, long SeenMilliseconds);

/// <summary>房主侧宣告器：本机有对局房间时持续广播。离开房间必须 Dispose（停止广播）。</summary>
public sealed class RoomAnnouncer : IDisposable
{
    public const int PayloadLengthLimit = 512;

    private readonly UdpClient _client = new() { EnableBroadcast = true };
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _seatsTaken;
    private int _seatsTotal = 2;

    public RoomAnnouncer(string roomId, int port, string? name)
    {
        RoomId = roomId;
        Port = port;
        Name = name;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public string RoomId { get; }
    public int Port { get; }
    public string? Name { get; }

    /// <summary>座位占用（有客人加入/离开时由房间屏刷新）。</summary>
    public void UpdateSeats(int taken, int total) { _seatsTaken = taken; _seatsTotal = total; }

    private async Task LoopAsync(CancellationToken ct)
    {
        var targets = new[]
        {
            new IPEndPoint(IPAddress.Broadcast, RoomDiscovery.DefaultPort), // 255.255.255.255：所有网卡（含任意虚拟局域网网卡）
            new IPEndPoint(IPAddress.Parse("127.255.255.255"), RoomDiscovery.DefaultPort), // 环回广播：同机探针
        };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var payload = new DiscoveryPayload(
                DiscoveryPayload.CurrentVersion, RoomId, Port, _seatsTaken, _seatsTotal, Name).Encode();
            var datagram = Encoding.UTF8.GetBytes(payload);
            foreach (var target in targets)
            {
                try { await _client.SendAsync(datagram, target).ConfigureAwait(false); }
                catch (SocketException) { /* 某块网卡发不出去很正常，下一轮再说 */ }
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { }
        _client.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>客人侧监听器：收宣告、去重交给上层（上层负责超时剔除与渲染）。</summary>
public sealed class RoomListener : IDisposable
{
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>协议线程回调：收到一条合法宣告。UI 使用处必须自己 marshal 回主线程。</summary>
    public event Action<DiscoveredRoom>? Received;

    public void Start()
    {
        if (_client is not null) return;
        _client = new UdpClient(AddressFamily.InterNetwork);
        // 允许多个客户端同机共存（探针的双端布局）。
        try { _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
        catch (SocketException) { }
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, RoomDiscovery.DefaultPort));
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var client = _client!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            var payload = DiscoveryPayload.Decode(Encoding.UTF8.GetString(result.Buffer));
            if (payload is null) continue;
            Received?.Invoke(new DiscoveredRoom(
                payload.RoomId, result.RemoteEndPoint.Address.ToString(), payload.Port,
                payload.SeatsTaken, payload.SeatsTotal, payload.Name,
                Environment.TickCount64));
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        _client?.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        _cts?.Dispose();
        _client = null;
        _cts = null;
        _loop = null;
    }
}
