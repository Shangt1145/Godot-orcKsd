using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// OrC 批量导入的本地持久化。存储真源＝原始行数据（id/字段/卡面文本），不存编译产物——
/// 装载后按需重编译（解析器或引擎模板演进时旧导入自动跟随新面，与"存储草稿在准入前重编译"
/// 的 DIY 口径一致）。原子写 + 限额 + 版本校验，与 DiyCardLibrary 同风格。
/// </summary>
public sealed class OrcImportLibrary
{
    private sealed record StoredRow(string Id, string Name, string Type,
        int Cost, int OpCost, int Attack, int Defense, string Text,string? DesktopJson=null);
    private sealed record FileData(int Schema, StoredRow[] Rows);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private const int MaxRows = 4096;

    private readonly string _path;
    private readonly object _gate = new();
    private List<OrcImportRow> _rows = [];

    public string? LoadError { get; private set; }
    public IReadOnlyList<OrcImportRow> Rows { get { lock (_gate) return _rows.ToArray(); } }
    public int Count { get { lock (_gate) return _rows.Count; } }

    public OrcImportLibrary(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("OrC 导入库文件过大");
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Json);
            if (data is null || data.Schema != 1 || data.Rows is null || data.Rows.Length > MaxRows)
                throw new InvalidDataException("OrC 导入库版本或内容无效");
            var rows = data.Rows.Select(r => new OrcImportRow(r.Id, r.Name, r.Type,
                r.Cost, r.OpCost, r.Attack, r.Defense, r.Text,r.DesktopJson)).ToList();
            if (rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new InvalidDataException("OrC 导入行编号重复");
            _rows = rows;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { LoadError = $"OrC 导入库读取失败，原文件保留：{e.Message}"; }
    }

    /// <summary>整批替换导入行（导入动作的落盘口径：同一 id 重导＝覆盖，新 id＝追加）。</summary>
    public void Merge(IEnumerable<OrcImportRow> rows)
    {
        lock (_gate)
        {
            var byId = _rows.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
            foreach (var row in rows) byId[row.Id] = row;
            _rows = byId.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            if (_rows.Count > MaxRows) throw new InvalidDataException($"导入行数超出上限（{MaxRows}）");
            Write();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_rows.RemoveAll(r => r.Id == id) == 0) return;
            Write();
        }
    }

    private void Write()
    {
        var data = new FileData(1, _rows.Select(r => new StoredRow(r.Id, r.Name, r.Type,
            r.Cost, r.OpCost, r.Attack, r.Defense, r.Text,r.DesktopJson)).ToArray());
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
