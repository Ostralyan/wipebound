using Godot;
using System.Collections.Generic;
using Wipebound.Combat;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.UI;

/// <summary>
/// Boss frame, cast bar, and the local player's vitals, ability slots and buffs.
///
/// The cast bar is driven from the same absolute server timestamps as the
/// telegraph, so the bar and the circle on the ground always finish together.
/// Deriving it from a local countdown would let them drift and players would
/// learn to trust whichever happened to be right.
///
/// Ability slots are built from the hero's kit at runtime rather than laid out in
/// the scene, so adding a fifth ability is a data change and not a scene edit.
/// </summary>
public partial class EncounterHud : Control
{
    private static readonly string[] SlotKeys = { "Q", "W", "E", "R", "1", "2" };

    private sealed class SlotView
    {
        public PanelContainer Root;
        public Label Title;
        public ProgressBar Cooldown;
    }

    // Boss frame
    private Label _bossName;
    private Label _castLabel;
    private ProgressBar _bossHealth;
    private HBoxContainer _bossStatusRow;
    private ProgressBar _castBar;

    // Player frame
    private Control _playerFrame;
    private ProgressBar _heroHealth;
    private ProgressBar _heroMana;
    private HBoxContainer _abilityRow;
    private HBoxContainer _buffRow;

    private VBoxContainer _meterRows;
    private readonly List<Label> _meterLabels = new();
    private Label _netDebug;

    private Boss _boss;
    private Hero _hero;
    private readonly List<SlotView> _slots = new();
    private readonly List<Label> _buffLabels = new();
    private readonly List<Label> _bossStatusLabels = new();

    private double _castStart;
    private double _castEnd;
    private bool _casting;

    public override void _Ready()
    {
        _bossName = GetNode<Label>("Encounter/BossName");
        _bossHealth = GetNode<ProgressBar>("Encounter/BossHealth");
        _bossStatusRow = GetNode<HBoxContainer>("Encounter/BossStatuses");
        _castLabel = GetNode<Label>("Encounter/CastLabel");
        _castBar = GetNode<ProgressBar>("Encounter/CastBar");

        _playerFrame = GetNode<Control>("PlayerFrame");
        _buffRow = GetNode<HBoxContainer>("PlayerFrame/Buffs");
        _heroHealth = GetNode<ProgressBar>("PlayerFrame/HeroHealth");
        _heroMana = GetNode<ProgressBar>("PlayerFrame/HeroMana");
        _abilityRow = GetNode<HBoxContainer>("PlayerFrame/Abilities");

        _meterRows = GetNode<VBoxContainer>("Meter");
        _netDebug = GetNode<Label>("NetDebug");

        CombatDirector.Instance.CastStarted += OnCastStarted;
        CombatDirector.Instance.CastInterrupted += OnCastInterrupted;
        NetworkManager.Instance.LocalHeroReady += OnLocalHeroReady;

        _playerFrame.Visible = false;
    }

    private void OnLocalHeroReady(Node3D node)
    {
        if (node is not Hero hero) return;

        _hero = hero;
        BuildAbilitySlots();
        _playerFrame.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_boss is null || !IsInstanceValid(_boss)) TryBindBoss();

        double now = NetClock.Instance.ServerTime;
        UpdateBoss();
        UpdateCast(now);
        UpdatePlayer(now);
        UpdateNetDebug();
        UpdateMeter();
    }

    // -- boss ------------------------------------------------------------

    private void TryBindBoss()
    {
        if (GetTree().GetFirstNodeInGroup(Boss.GroupName) is Boss boss) _boss = boss;
    }

    private void OnCastStarted(int casterTeam, string casterName, string label,
                               double startTime, double endTime, Color color)
    {
        // The boss frame shows boss casts. A player's own cast is their business.
        if ((Team)casterTeam != Team.Enemies) return;

        _castLabel.Text = label;
        _castLabel.Modulate = color;
        _castBar.Modulate = color;
        _castStart = startTime;
        _castEnd = endTime;
        _casting = true;
    }

    /// An interrupted cast never resolves, so the bar must not keep filling as if
    /// it will.
    private void OnCastInterrupted(int casterTeam)
    {
        if ((Team)casterTeam == Team.Enemies) _casting = false;
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

        _bossHealth.MaxValue = _boss.HealthMax;
        _bossHealth.Value = _boss.Health;

        // The boss's own statuses, because its Sundered stacks are the entire
        // reason to coordinate a burst window and nobody could see them.
        RenderStatuses(_bossStatusRow, _bossStatusLabels, _boss.Status, NetClock.Instance.ServerTime);
    }

    private void UpdateCast(double now)
    {
        _castLabel.Visible = _casting;
        _castBar.Visible = _casting;
        if (!_casting) return;

        double span = Mathf.Max(_castEnd - _castStart, 0.0001);
        _castBar.MaxValue = 1.0;
        _castBar.Value = Mathf.Clamp((now - _castStart) / span, 0.0, 1.0);

        // Linger briefly past the deadline so the resolve is legible.
        if (now > _castEnd + 0.4) _casting = false;
    }

    // -- player ----------------------------------------------------------

    private void BuildAbilitySlots()
    {
        foreach (Node child in _abilityRow.GetChildren()) child.QueueFree();
        _slots.Clear();

        for (int i = 0; i < _hero.Kit.Count; i++)
        {
            Ability ability = _hero.Kit[i];

            var panel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(124, 46),
                MouseFilter = MouseFilterEnum.Ignore,
            };

            var rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };

            var title = new Label
            {
                Text = $"[{(i < SlotKeys.Length ? SlotKeys[i] : "?")}]  {ability.DisplayName}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };

            var cooldown = new ProgressBar
            {
                MaxValue = 1.0,
                Step = 0.001,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 6),
                MouseFilter = MouseFilterEnum.Ignore,
            };

            rows.AddChild(title);
            rows.AddChild(cooldown);
            panel.AddChild(rows);
            _abilityRow.AddChild(panel);

            _slots.Add(new SlotView { Root = panel, Title = title, Cooldown = cooldown });
        }
    }

    private void UpdatePlayer(double now)
    {
        if (_hero is null || !IsInstanceValid(_hero))
        {
            _playerFrame.Visible = false;
            return;
        }

        _heroHealth.MaxValue = _hero.HealthMax;
        _heroHealth.Value = _hero.Health;
        _heroMana.MaxValue = _hero.ManaMax;
        _heroMana.Value = _hero.Mana;

        bool silenced = _hero.Status.Silenced;

        for (int i = 0; i < _slots.Count; i++)
        {
            SlotView slot = _slots[i];
            Ability ability = _hero.AbilityAt(i);
            if (ability is null) continue;

            float remaining = _hero.CooldownFraction(i, now);
            slot.Cooldown.Value = remaining;

            bool usable = remaining <= 0f
                          && _hero.ManaPool.CanAfford(ability.ManaCost)
                          && !silenced
                          && _hero.IsAlive;

            slot.Root.Modulate = usable ? Colors.White : new Color(1f, 1f, 1f, 0.38f);
        }

        UpdateBuffs(now);
    }

    private void UpdateBuffs(double now)
        => RenderStatuses(_buffRow, _buffLabels, _hero.Status, now);

    /// <summary>
    /// Draw one status row. Labels are pooled rather than rebuilt, because this runs
    /// every frame and a status set changes perhaps once a second.
    /// </summary>
    private static void RenderStatuses(HBoxContainer row, List<Label> pool, StatusTracker tracker, double now)
    {
        IReadOnlyList<ActiveStatus> active = tracker.Active;

        for (int i = 0; i < active.Count; i++)
        {
            Label label = i < pool.Count ? pool[i] : NewStatusLabel(row, pool);
            ActiveStatus status = active[i];

            string name = status.Definition.DisplayName;
            if (status.Stacks > 1) name += $" x{status.Stacks}";
            if (status.AbsorbRemaining > 0f) name += $" [{Mathf.RoundToInt(status.AbsorbRemaining)}]";

            label.Text = $"{name}  {status.RemainingAt(now):0.0}s";
            label.Modulate = status.Definition.Tint;
            label.Visible = true;
        }

        for (int i = active.Count; i < pool.Count; i++)
            pool[i].Visible = false;
    }

    private static Label NewStatusLabel(HBoxContainer row, List<Label> pool)
    {
        var label = new Label { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(label);
        pool.Add(label);
        return label;
    }

    /// <summary>
    /// Who actually did what. Costs nothing to display because the numbers are
    /// recorded at the damage chokepoint and replicate with the rest of a hero's
    /// state -- and it answers "why did we wipe" with something other than opinion.
    /// </summary>
    private void UpdateMeter()
    {
        var heroes = new List<Hero>();
        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero hero) heroes.Add(hero);

        heroes.Sort((a, b) => b.DamageDone.CompareTo(a.DamageDone));

        for (int i = 0; i < heroes.Count; i++)
        {
            Label label = i < _meterLabels.Count ? _meterLabels[i] : NewMeterLabel();
            Hero hero = heroes[i];

            string healing = hero.HealingDone > 0f ? $"  +{Mathf.RoundToInt(hero.HealingDone)}" : "";
            label.Text = $"{hero.PeerId}   {Mathf.RoundToInt(hero.DamageDone)}{healing}";
            label.Modulate = hero.IsLocalPlayer ? new Color("4ade80") : new Color(1f, 1f, 1f, 0.75f);
            label.Visible = true;
        }

        for (int i = heroes.Count; i < _meterLabels.Count; i++)
            _meterLabels[i].Visible = false;
    }

    private Label NewMeterLabel()
    {
        var label = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _meterRows.AddChild(label);
        _meterLabels.Add(label);
        return label;
    }

    // -- diagnostics -----------------------------------------------------

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
