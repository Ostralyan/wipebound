using Godot;
using Wipebound.Combat;
using Wipebound.Net;

namespace Wipebound.UI;

/// <summary>
/// Boss health, phase, and the cast bar.
///
/// The cast bar is driven from the same absolute server timestamps the telegraph
/// uses, so the bar on screen and the circle on the ground always finish together.
/// Deriving it from a local countdown would let them drift apart, and players
/// would learn to trust whichever one happened to be right.
/// </summary>
public partial class EncounterHud : Control
{
    private Label _bossName;
    private Label _castLabel;
    private Label _netDebug;
    private ProgressBar _bossHealth;
    private ProgressBar _castBar;

    private Boss _boss;
    private double _castStart;
    private double _castEnd;
    private bool _casting;

    public override void _Ready()
    {
        _bossName = GetNode<Label>("Encounter/BossName");
        _bossHealth = GetNode<ProgressBar>("Encounter/BossHealth");
        _castLabel = GetNode<Label>("Encounter/CastLabel");
        _castBar = GetNode<ProgressBar>("Encounter/CastBar");
        _netDebug = GetNode<Label>("NetDebug");
    }

    public override void _Process(double delta)
    {
        if (_boss is null || !IsInstanceValid(_boss)) TryBindBoss();

        UpdateBoss();
        UpdateCast();
        UpdateNetDebug();
    }

    private void TryBindBoss()
    {
        if (GetTree().GetFirstNodeInGroup(Boss.GroupName) is not Boss boss) return;

        _boss = boss;
        _boss.CastStarted += OnCastStarted;
    }

    private void OnCastStarted(string label, double startTime, double endTime, Color color)
    {
        _castLabel.Text = label;
        _castLabel.Modulate = color;
        _castBar.Modulate = color;
        _castStart = startTime;
        _castEnd = endTime;
        _casting = true;
    }

    private void UpdateBoss()
    {
        bool present = _boss is not null && IsInstanceValid(_boss);
        _bossName.Visible = present;
        _bossHealth.Visible = present;
        if (!present) return;

        string phase = _boss.CurrentPhase?.Name ?? "-";
        _bossName.Text = _boss.IsAlive
            ? $"{_boss.DisplayName}   —   {phase}"
            : $"{_boss.DisplayName}   —   DEFEATED";

        _bossHealth.MaxValue = _boss.MaxHealth;
        _bossHealth.Value = _boss.Health;
    }

    private void UpdateCast()
    {
        _castLabel.Visible = _casting;
        _castBar.Visible = _casting;
        if (!_casting) return;

        double now = NetClock.Instance.ServerTime;
        double span = Mathf.Max(_castEnd - _castStart, 0.0001);

        _castBar.MaxValue = 1.0;
        _castBar.Value = Mathf.Clamp((now - _castStart) / span, 0.0, 1.0);

        // Linger briefly past the deadline so the resolve is legible.
        if (now > _castEnd + 0.4) _casting = false;
    }

    private void UpdateNetDebug()
    {
        NetworkManager net = NetworkManager.Instance;
        NetClock clock = NetClock.Instance;

        if (!net.InSession)
        {
            _netDebug.Text = "offline";
            return;
        }

        _netDebug.Text = net.IsServer
            ? $"{net.Mode}  |  worst peer rtt {clock.WorstPeerRtt * 1000.0:0}ms  |  serving the clock"
            : $"client {Multiplayer.GetUniqueId()}  |  rtt {clock.Rtt * 1000.0:0}ms  " +
              $"|  clock {clock.OffsetSeconds:+0.000;-0.000}s  |  {(clock.Synced ? "synced" : "SYNCING")}";
    }
}
