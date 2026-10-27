using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kards.Ui.Core;

/// <summary>
/// 房主侧的本地房间设置：房间名、是否固定端口、固定成哪个端口。
///
/// 只存"想怎么开房"，不存任何对局数据；口径与卡库/卡组一致——带 schema、原子写、
/// 读失败保留原文件并给出可读原因（<see cref="LoadError"/>）。
/// </summary>
public sealed class RoomSettings
{
    private sealed record FileData(int Schema, string? Name, bool FixedPort, int Port);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>固定端口的默认值。挑一个冷门端口，避开常见游戏/服务段。</summary>
    public const int DefaultPort = 4717;

    /// <summary>1024 以下需要管理员权限，不让填。</summary>
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    /// <summary>房间名上限。广播载荷有 512 字节硬上限，24 个汉字也远远够用。</summary>
    public const int NameLimit = 24;

    private readonly string _path;
    private string _name = "";
    private bool _fixedPort;
    private int _port = DefaultPort;

    public RoomSettings(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 64_000) throw new InvalidDataException("房间设置文件过大");
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("房间设置文件为空");
            SchemaVersioning.Require(data.Schema, "房间设置");
            _name = Clean(data.Name);
            _fixedPort = data.FixedPort;
            _port = ValidPort(data.Port) ? data.Port : DefaultPort;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            // 读不懂就用默认值，但把原因留住——不能悄悄把原文件覆盖掉。
            LoadError = $"房间设置读取失败，本次用默认值：{e.Message}";
        }
    }

    /// <summary>读失败的原因；非空表示这次运行没读到你上次的设置（原文件仍在）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>房间名。空串＝没起名，局域网列表显示"好友的房间"。</summary>
    public string Name
    {
        get => _name;
        set => _name = Clean(value);
    }

    public bool FixedPort
    {
        get => _fixedPort;
        set => _fixedPort = value;
    }

    /// <summary>固定端口的取值（未开启固定端口时无意义）。</summary>
    public int Port
    {
        get => _port;
        set => _port = ValidPort(value) ? value : DefaultPort;
    }

    /// <summary>启动服务端时要用的端口：0＝让系统分配。</summary>
    public int RequestedPort => _fixedPort ? _port : 0;

    /// <summary>端口是否可用（1024–65535）。</summary>
    public static bool ValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>解析玩家手填的端口；失败返回 false 且不改任何状态。</summary>
    public static bool TryParsePort(string? text, out int port)
        => int.TryParse((text ?? "").Trim(), out port) && ValidPort(port);

    public void Save()
    {
        if (LoadError is not null) throw new IOException(LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        var encoded = JsonSerializer.Serialize(new FileData(SchemaVersioning.Latest, _name, _fixedPort, _port), Json);
        File.WriteAllText(temporary, encoded, new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>去掉控制字符与首尾空白，并截到上限——房间名会进 UDP 广播，不能带换行。</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var filtered = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return filtered.Length <= NameLimit ? filtered : filtered[..NameLimit];
    }
}
