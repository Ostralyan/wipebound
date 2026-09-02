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
    /// The keycap is asked of the InputMap, never assumed. A hard-coded row was
    /// fine while the keys were hard-coded too; now that players can remap them,
    /// a fixed label is just a lie printed on the button.
    private static string SlotKey(int slot)
    {
        string cap = Player.Bindings.Keycap(Player.Bindings.Ability(slot));
        return cap.Length > 0 ? cap : "--";
    }

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
    private readonly List<MeterRow> _meterViews = new();

    /// <summary>
    /// What the meter is counting.
    ///
    /// One panel, cycled, rather than three. A Verdant reading a damage column
    /// learns nothing about whether it is doing its job, and a raid with three
    /// meters on screen has no room left for the fight.
    /// </summary>
    private enum MeterMode
    {
        Damage,
        Healing,
        Taken,
    }

    private MeterMode _meterMode = MeterMode.Damage;
    private Label _meterHeader;

    /// <summary>
    /// When this client first saw anybody do anything.
    ///
    /// Per-second figures need a start, and the server's is not replicated. It
    /// is derived here instead: the first frame a total is non-zero is the first
    /// frame anything happened. Slightly late by one tick, which is invisible
    /// against a fight measured in minutes, and it needs no new wire traffic.
    /// </summary>
    private double _combatStartedAt;

    private sealed class MeterRow
    {
        public Control Root;
        public ColorRect Bar;
        public Label Name;
        public Label Value;
    }
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

        // Says what is being counted, so a column of numbers is never ambiguous.
        _meterHeader = new Label
        {
            Text = "DAMAGE",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _meterRows.AddChild(_meterHeader);
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

        // Two rows, split by what the buttons are FOR: the six you press
        // constantly on top, the situational tools and the two panic buttons and
        // the ultimate below. That is not decoration -- twelve panels at their
        // old 124px minimum came to 1488px on a 1280px viewport, so the ultimate
        // was pushed off the edge of the screen. Splitting by role fixes the
        // overflow and makes the shape of the kit legible at a glance.
        var stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var rotational = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var everythingElse = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        stack.AddChild(rotational);
        stack.AddChild(everythingElse);
        _abilityRow.AddChild(stack);

        for (int i = 0; i < _hero.Kit.Count; i++)
        {
            Ability ability = _hero.Kit[i];

            var panel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(118, 46),
                MouseFilter = MouseFilterEnum.Ignore,
            };

            var rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };

            var title = new Label
            {
                Text = $"[{SlotKey(i)}]  {ability.DisplayName}",
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

            // Bucketed by role rather than by index, so a reordered kit still
            // lands in the right row.
            (ability.Role == AbilityRole.Rotational ? rotational : everythingElse).AddChild(panel);

            // Added in kit order regardless of which row it went to, so slot i
            // here is still ability i when cooldowns tick.
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
    /// <summary>
    /// Who is doing the work, and how much of it.
    ///
    /// Bars are proportional to the LEADER rather than to a fixed scale, which
    /// is the only version that stays readable: absolute numbers vary by an
    /// order of magnitude between the opening and the last phase, and a bar
    /// scaled to a guess is empty for most of a fight.
    /// </summary>
    private void UpdateMeter()
    {
        var heroes = new List<Hero>();
        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero hero) heroes.Add(hero);

        heroes.Sort((a, b) => Amount(b).CompareTo(Amount(a)));

        double now = NetClock.Instance.ServerTime;
        float total = 0f;
        foreach (Hero hero in heroes) total += Amount(hero);

        if (_combatStartedAt <= 0.0 && total > 0f) _combatStartedAt = now;
        double elapsed = _combatStartedAt > 0.0 ? Mathf.Max(now - _combatStartedAt, 0.001) : 0.0;

        float leader = heroes.Count > 0 ? Mathf.Max(Amount(heroes[0]), 1f) : 1f;
        float width = Mathf.Max(_meterRows.Size.X, 120f);

        for (int i = 0; i < heroes.Count; i++)
        {
            MeterRow row = i < _meterViews.Count ? _meterViews[i] : NewMeterRow();
            Hero hero = heroes[i];
            float amount = Amount(hero);

            row.Name.Text = $"{hero.CombatName}  {PlayerKit.NameOf(hero.Class)}";

            row.Value.Text = elapsed > 0.0
                ? $"{Mathf.RoundToInt(amount):N0}   {Mathf.RoundToInt(amount / (float)elapsed):N0}/s"
                : $"{Mathf.RoundToInt(amount):N0}";

            row.Bar.Color = ClassTint(hero.Class, hero.IsLocalPlayer);
            row.Bar.Size = new Vector2(width * (amount / leader), MeterRowHeight - 2f);
            row.Root.Visible = true;
        }

        for (int i = heroes.Count; i < _meterViews.Count; i++)
            _meterViews[i].Root.Visible = false;

        _meterHeader.Text = elapsed > 0.0
            ? $"{Header()}   {(int)elapsed / 60:0}:{(int)elapsed % 60:00}"
            : Header();
    }

    private float Amount(Hero hero) => _meterMode switch
    {
        MeterMode.Healing => hero.HealingDone,
        MeterMode.Taken => hero.DamageTaken,
        _ => hero.DamageDone,
    };

    private string Header() => _meterMode switch
    {
        MeterMode.Healing => "HEALING",
        MeterMode.Taken => "DAMAGE TAKEN",
        _ => "DAMAGE",
    };

    /// The class palette, so a row is recognisable before it is read. The local
    /// player is brighter, because the first thing anybody looks for is
    /// themselves.
    private static Color ClassTint(HeroClass hero, bool mine)
    {
        Color tint = hero switch
        {
            HeroClass.Warden => new Color("38bdf8"),
            HeroClass.Ember => new Color("fb923c"),
            _ => new Color("4ade80"),
        };

        tint.A = mine ? 0.85f : 0.45f;
        return tint;
    }

    private const float MeterRowHeight = 20f;

    private MeterRow NewMeterRow()
    {
        var root = new Control
        {
            CustomMinimumSize = new Vector2(0f, MeterRowHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };

        // Behind the text, sized every frame. A ColorRect rather than a
        // ProgressBar because the bar is the only thing being styled and a
        // theme override for one rectangle is more machinery than a rectangle.
        var bar = new ColorRect { MouseFilter = MouseFilterEnum.Ignore };

        var name = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorRight = 1f,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var value = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorRight = 1f,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        root.AddChild(bar);
        root.AddChild(name);
        root.AddChild(value);
        _meterRows.AddChild(root);

        var row = new MeterRow { Root = root, Bar = bar, Name = name, Value = value };
        _meterViews.Add(row);
        return row;
    }

    /// <summary>Cycle what the meter counts. Bound, like everything else.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed(Player.Bindings.MeterMode)) return;

        _meterMode = _meterMode switch
        {
            MeterMode.Damage => MeterMode.Healing,
            MeterMode.Healing => MeterMode.Taken,
            _ => MeterMode.Damage,
        };

        GetViewport().SetInputAsHandled();
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
            : $"client {net.LocalPeerId}  |  rtt {clock.Rtt * 1000.0:0}ms  " +
              $"|  clock {clock.OffsetSeconds:+0.000;-0.000}s  |  {(clock.Synced ? "synced" : "SYNCING")}";
    }
}
