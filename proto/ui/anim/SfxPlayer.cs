using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class SfxPlayer : Node
{
    private readonly List<AudioStreamPlayer> _pool = [];
    private readonly Dictionary<string, AudioStream> _cache = new();
    public AnimClock Clock { get; set; } = null!;
    public override void _Ready()
    {
        for (var i = 0; i < 8; i++)
        {
            var p = new AudioStreamPlayer();
            AddChild(p);
            _pool.Add(p);
        }
        Clock.Changed += StopWhenMuted;
    }
    private void StopWhenMuted()
    {
        if (Clock.Muted)
            foreach (var p in _pool)
                p.Stop();
    }
    public override void _ExitTree()
    {
        Clock.Changed -= StopWhenMuted;
        foreach (var p in _pool)
        {
            p.Stop();
            p.Stream = null;
        }
        _cache.Clear();
    }
    public void Play(string eventName, UiCardView? card = null, float gain = 1, UiCardView? target = null)
    {
        // Dummy audio in headless validation cannot check sound and may retain native MP3 playback handles.
        if (DisplayServer.GetName() == "headless")
            return;
        if (Clock.Muted || Clock.Volume <= 0)
            return;
        var kind = card?.Definition.UnitType ?? "infantry";
        var specialized = eventName is "attack" or "deploy" or "move";
        var prefix = eventName switch
        {
            "deploy" => "dp_",
            "move" => "mv_",
            _ => ""
        };
        var stem = eventName switch
        {
            "attack" => "attack1",
            "deploy" => "deploy1",
            "move" => "move1",
            "die" => "die1",
            "hit" => "hit3",
            "counterSet" => "deploy2",
            "counterFire" or "burn" => "hit2",
            "shuffle" => "move1",
            "intel" => "ui1",
            _ => "ui2"
        };
        var variants = eventName switch
        {
            "attack" => new[] { "attack1", "attack2", "attack3" },
            "deploy" => new[] { "deploy1", "deploy2" },
            "move" => new[] { "move1", "move2", "move3" },
            "die" => new[] { "die1", "die2", "die3" },
            "hit" => new[] { "hit1", "hit2", "hit3" },
            "ui" => new[] { "ui1", "ui2" },
            _ => new[] { stem }
        };
        if (specialized && kind != "infantry")
        {
            var typeVariants = Enumerable.Range(1, 2).Select(i => prefix + kind + i)
                .Where(name => ResourceLoader.Exists($"res://proto/sfx/{name}.mp3")).ToArray();
            if (typeVariants.Length > 0) variants = typeVariants;
        }
        stem = variants[Random.Shared.Next(variants.Length)];
        if (eventName == "hit" && target?.Definition.UnitType is "tank" or "cruiser" or "landcruiser")
            stem = "hit1";
        else if (eventName == "hit" && target?.Definition.UnitType is "structure" or "artillery")
            stem = "hit2";
        else if (eventName == "hit" && target is not null)
            stem = "hit3";
        var path = $"res://proto/sfx/{stem}.mp3";
        if (!_cache.TryGetValue(path, out var stream))
        {
            if (!ResourceLoader.Exists(path))
                return;
            stream = ResourceLoader.Load<AudioStream>(path);
            _cache[path] = stream;
        }
        var player = _pool.FirstOrDefault(x => !x.Playing);
        if (player is null)
            return;
        var eventGain = eventName switch
        {
            "attack" => 2f,
            "hit" => 1.3f,
            "die" or "counterFire" => 1.2f,
            "deploy" => 1.1f,
            "ui" => .7f,
            "shuffle" => .5f,
            "counterSet" => .8f,
            "intel" => .6f,
            _ => 1f
        };
        if (eventName == "burn") gain *= .7f;
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(Math.Clamp(Clock.Volume * eventGain * gain, .001f, 1f));
        var defenseSource = eventName == "hit" ? target ?? card : card;
        var baseRate = defenseSource is null ? 1 : Math.Clamp(1f - (Mathf.Log(Math.Max(1, defenseSource.EffectiveDefense ?? 4)) / Mathf.Log(2) - 2) * .085f, .8f, 1.3f);
        player.PitchScale = eventName switch
        {
            "veteran" => 1.35f, "counterSet" => .85f, "counterFire" => 1.1f,
            "shuffle" => 1.3f, "intel" => 1.5f, "burn" => .7f, _ => baseRate
        };
        player.Play();
    }
}
