using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

public sealed record ReplayHeader(string Format, int Version, DateTimeOffset CreatedAt, string Perspective, string Application);
public sealed record ReplayFrame(double Seconds, UiMatchView View, UiPresentationResolution? Presentation = null, int? Perspective = null);
public sealed record ReplayRecording(ReplayHeader Header, IReadOnlyList<ReplayFrame> Frames)
{
    public double Duration => Frames.Count == 0 ? 0 : Frames[^1].Seconds;
}

/// <summary>Records only projections already authorized for this viewer. Never reconstructs hidden state.</summary>
public sealed class MatchReplayRecorder : IDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => {
            if (info.Type != typeof(UiPresentationStep)) return;
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$step" };
            foreach (var type in typeof(UiPresentationStep).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(UiPresentationStep).IsAssignableFrom(t)))
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name));
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private readonly StreamWriter _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private bool _closed;
    private string? _previous;
    public MatchReplayRecorder(string path, string perspective, string application)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _writer.WriteLine(JsonSerializer.Serialize(new ReplayHeader("orc-view-replay", 1, DateTimeOffset.UtcNow, perspective, application), Json));
        _writer.Flush();
    }
    public void Record(UiMatchView view, UiPresentationResolution? presentation = null, int? perspective = null)
    {
        lock (_sync)
        {
            if (_closed) return;
            var snapshot = JsonSerializer.Serialize(view, Json);
            if (presentation is null && snapshot == _previous) return;
            _previous = snapshot;
            var line = JsonSerializer.Serialize(new ReplayFrame(_clock.Elapsed.TotalSeconds, view, presentation, perspective), Json);
            if (_writer.BaseStream.Length + System.Text.Encoding.UTF8.GetByteCount(line) + 2 > 256L * 1024 * 1024)
                throw new IOException("回放已达到 256 MB 上限，后续记录已停止。");
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }
    public void Dispose() { lock (_sync) { if (_closed) return; _closed = true; try { _writer.Dispose(); } catch (IOException) { } } }
    public static ReplayHeader ReadHeader(string path)
    {
        using var reader = File.OpenText(path);
        return ParseHeader(reader.ReadLine());
    }
    private static ReplayHeader ParseHeader(string? line)
    {
        var header = JsonSerializer.Deserialize<ReplayHeader>(line ?? "", Json);
        if (header is null || header.Format != "orc-view-replay" || header.Version != 1)
            throw new InvalidDataException("回放格式或版本不受支持。");
        return header;
    }
    public static ReplayRecording Load(string path)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("回放文件超过 256 MB。");
        using var reader = File.OpenText(path);
        var header = ParseHeader(reader.ReadLine());
        var frames = new List<ReplayFrame>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ReplayFrame? frame;
            try { frame = JsonSerializer.Deserialize<ReplayFrame>(line, Json); }
            catch (JsonException) when (reader.EndOfStream && frames.Count > 0) { break; } // recover an interrupted final write
            if (frame is null || frame.View is null || !double.IsFinite(frame.Seconds) || frame.Seconds < 0 ||
                frame.Perspective is not (null or 0 or 1) ||
                (frames.Count > 0 && (frame.Seconds < frames[^1].Seconds || frame.View.MatchId != frames[0].View.MatchId)))
                throw new InvalidDataException("回放时间线损坏。");
            frames.Add(frame);
            if (frames.Count > 100000) throw new InvalidDataException("回放帧数过多。");
        }
        if (frames.Count == 0) throw new InvalidDataException("该回放尚未记录到对局画面。");
        return new(header, frames);
    }
}

/// <summary>Read-only playback. Seeking applies a recorded snapshot; playback uses recorded presentation steps.</summary>
public sealed class ReplayMatchFeed : IMatchFeed
{
    public ReplayRecording Recording { get; }
    private int _index;
    private IReadOnlyList<ReplayFrame> _frames;
    public int Perspective { get; private set; }
    public ReplayMatchFeed(ReplayRecording recording)
    {
        Recording = recording; _frames = recording.Frames;
        if (recording.Frames.Any(f => f.Perspective.HasValue)) _frames = recording.Frames.Where(f => f.Perspective == 0).ToArray();
        if (_frames.Count == 0) _frames = recording.Frames;
        Position = _frames[0].Seconds;
    }
    public void SetPerspective(int seat)
    {
        var selected = Recording.Frames.Where(f => f.Perspective == seat).ToArray();
        if (selected.Length == 0) return;
        Perspective = seat; _frames = selected; Seek(Position);
    }
    public bool Playing { get; set; }
    public double Speed { get; set; } = 1;
    public double Position { get; private set; }
    public string MatchId => CurrentView?.MatchId ?? "";
    public UiMatchView? CurrentView => _frames[_index].View;
    private readonly UiBattleActions _noActions = new();
    public UiBattleActions CurrentActions => _noActions;
    public bool IsSubmitting => false;
    public bool MulliganPending => false;
    public bool TargetPending => false;
    public string? MulliganRequestId => null;
    public Func<UiActionInteractionState>? InteractionStateReader { get; set; }
    public void Seek(double seconds)
    {
        Position = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, Recording.Duration);
        var left = 0; var right = _frames.Count;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (_frames[middle].Seconds <= Position) left = middle + 1; else right = middle;
        }
        _index = Math.Max(0, left - 1);
        ProjectionReady?.Invoke(CurrentView!, _noActions);
    }
    public void Advance(double delta)
    {
        if (!Playing) return;
        Position = Math.Min(Recording.Duration, Position + Math.Max(0, delta) * Math.Clamp(Speed, .25, 4));
        while (_index + 1 < _frames.Count && _frames[_index + 1].Seconds <= Position)
        {
            var frame = _frames[++_index];
            if (frame.Presentation is { } presentation) PresentationReady?.Invoke(presentation, _noActions);
            else ProjectionReady?.Invoke(frame.View, _noActions);
        }
        if (Position >= Recording.Duration) Playing = false;
    }
    public Task SubmitAsync(UiCommand command, CancellationToken ct = default)
    { CommandRefused?.Invoke(command); HintRequested?.Invoke("回放中无法操作对局。"); return Task.CompletedTask; }
    public event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    public event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    public event Action<UiCommand>? CommandRefused;
    public event Action<string>? HintRequested;
#pragma warning disable CS0067
    public event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    public event Action? MulliganRequested;
    public event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    public event Action? TargetClosed;
    public event Action<string>? ErrorRaised;
#pragma warning restore CS0067
}
