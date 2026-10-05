using Godot;

namespace Kards.Ui;

public static class AnimTiming
{
    public const double Fast = 120, Base = 260, Slow = 420, Fly = 420, Lunge = 150, Die = 380, Move = 360;
}

public partial class AnimClock : Node
{
    public enum Speed
    {
        Normal, Fast, Faster
    }
    public Speed Current
    {
        get; private set;
    }
    public double Scale => Current switch { Speed.Fast => .6, Speed.Faster => .32, _ => 1 };
    public bool ReducedMotion
    {
        get; private set;
    }
    public float Volume { get; private set; } = .45f;
    public bool Muted
    {
        get; private set;
    }
    public event Action? Changed;
    public double Ms(double ms) => Math.Max(30, Math.Round(ms * Scale, MidpointRounding.AwayFromZero));
    public override void _Ready()
    {
        var config = new ConfigFile();
        if (config.Load("user://fx_speed.cfg") != Error.Ok)
            return;
        Current = (Speed)Math.Clamp(config.GetValue("fx", "speed", 0).AsInt32(), 0, 2);
        ReducedMotion = config.GetValue("fx", "reduced_motion", false).AsBool();
        Volume = Math.Clamp(config.GetValue("audio", "volume", .45).AsSingle(), 0, 1);
        Muted = config.GetValue("audio", "muted", false).AsBool();
    }
    public void SetSpeed(Speed speed)
    {
        Current = speed;
        Save();
    }
    public void SetReducedMotion(bool reduced)
    {
        ReducedMotion = reduced;
        Save();
    }
    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        Save();
    }
    public void SetMuted(bool muted)
    {
        Muted = muted;
        Save();
    }
    private void Save()
    {
        var config = new ConfigFile();
        config.SetValue("fx", "speed", (int)Current);
        config.SetValue("fx", "reduced_motion", ReducedMotion);
        config.SetValue("audio", "volume", Volume);
        config.SetValue("audio", "muted", Muted);
        var err = config.Save("user://fx_speed.cfg");
        if (err != Error.Ok)
            GD.PushWarning($"Could not save settings: {err}");
        Changed?.Invoke();
    }
}
