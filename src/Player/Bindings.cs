using System.Collections.Generic;
using Godot;

namespace Wipebound.Player;

/// <summary>
/// Every key the game reads, in one place, and none of them hard-coded at the
/// point of use.
///
/// Input used to be raw keycodes matched inside Hero._UnhandledInput, which is
/// why W both walked you forward and cast slot 2 -- there was nowhere for the
/// two claims on that key to notice each other. Actions fix that structurally:
/// a key belongs to exactly one action because Rebind() takes it away from
/// whoever held it.
///
/// Defaults bind PHYSICAL keycodes, so WASD is the same three-fingers-and-a-
/// thumb shape on AZERTY and QWERTZ. A player who rebinds gets whatever they
/// actually pressed.
///
/// Nothing here may ever reach ContentHash. Bindings are per-player, and a
/// fingerprint that moved when someone remapped their keys would refuse honest
/// players from the leaderboard -- the same shape of bug as hashing numbers in
/// the ambient locale. SelfTest.BindingsAreNotContent holds that line.
/// </summary>
public static class Bindings
{
    /// The kit is twelve buttons wide: 6 rotational, 3 situational, 2
    /// defensive, 1 ultimate. Slots past a class's kit simply go unbound.
    public const int AbilitySlots = 12;

    public const string MoveUp = "move_up";
    public const string MoveDown = "move_down";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string SelfCast = "self_cast";
    public const string CameraUp = "camera_up";
    public const string CameraDown = "camera_down";
    public const string CameraLeft = "camera_left";
    public const string CameraRight = "camera_right";
    public const string CameraRecenter = "camera_recenter";
    public const string MeterMode = "meter_mode";

    private const string SavePath = "user://controls.cfg";
    private const string Section = "bind";

    private static readonly string[] AbilityActions = BuildAbilityNames();

    /// <summary>The action name for an ability slot. Preallocated: this is read every frame.</summary>
    public static string Ability(int slot)
        => slot >= 0 && slot < AbilitySlots ? AbilityActions[slot] : string.Empty;

    /// <summary>Every rebindable action, in the order a settings screen should list them.</summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    // -- defaults ---------------------------------------------------------

    /// <summary>
    /// A WASD layout, laid out by what the slot is FOR rather than by counting
    /// along the number row: the six you press constantly sit under the resting
    /// hand and the mouse, the three situational tools are on the number row,
    /// and the two panic buttons plus the ultimate are far enough away that you
    /// cannot fat-finger them mid-rotation.
    /// </summary>
    private static Dictionary<string, InputEvent> Defaults() => new()
    {
        [MoveUp] = Physical(Key.W),
        [MoveDown] = Physical(Key.S),
        [MoveLeft] = Physical(Key.A),
        [MoveRight] = Physical(Key.D),

        // Rotational: hand and mouse, no reach.
        [Ability(0)] = Mouse(MouseButton.Left),
        [Ability(1)] = Mouse(MouseButton.Right),
        [Ability(2)] = Physical(Key.Q),
        [Ability(3)] = Physical(Key.E),
        [Ability(4)] = Physical(Key.R),
        [Ability(5)] = Physical(Key.F),

        // Situational: number row, pressed deliberately.
        [Ability(6)] = Physical(Key.Key1),
        [Ability(7)] = Physical(Key.Key2),
        [Ability(8)] = Physical(Key.Key3),

        // Defensive: thumb and a dedicated finger.
        [Ability(9)] = Physical(Key.Space),
        [Ability(10)] = Physical(Key.C),

        // Ultimate: deliberately awkward.
        [Ability(11)] = Physical(Key.X),

        [SelfCast] = Physical(Key.Alt),

        // Panning is on the arrows because the left hand is busy walking now.
        [CameraUp] = Physical(Key.Up),
        [CameraDown] = Physical(Key.Down),
        [CameraLeft] = Physical(Key.Left),
        [CameraRight] = Physical(Key.Right),
        [CameraRecenter] = Physical(Key.Home),

        // Healers need a different meter from everyone else, and nobody wants
        // two panels.
        [MeterMode] = Physical(Key.Tab),
    };

    // -- install / persist ------------------------------------------------

    /// <summary>
    /// Register every action, then let the player's saved file win. Safe to
    /// call on a headless dedicated server: it simply finds no save file and
    /// installs defaults nobody will ever press.
    /// </summary>
    public static void Install()
    {
        foreach ((string action, InputEvent binding) in Defaults())
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            InputMap.ActionEraseEvents(action);
            InputMap.ActionAddEvent(action, binding);
        }

        Load();
    }

    public static void ResetToDefaults()
    {
        foreach ((string action, InputEvent binding) in Defaults())
        {
            InputMap.ActionEraseEvents(action);
            InputMap.ActionAddEvent(action, binding);
        }

        Save();
    }

    /// <summary>
    /// Bind an event to an action, taking it off whoever held it.
    ///
    /// Returning the displaced action rather than refusing the bind is the
    /// kinder half: a player remapping a full keyboard is always going to
    /// collide, and a screen that says "this took Dash's key" lets them carry
    /// on instead of hunting for the conflict themselves.
    /// </summary>
    public static bool Rebind(string action, InputEvent binding, out string displaced)
    {
        displaced = null;
        if (binding is null || !InputMap.HasAction(action)) return false;
        if (Encode(binding) is null) return false;

        foreach (string other in All)
        {
            if (other == action) continue;
            if (!InputMap.ActionHasEvent(other, binding)) continue;

            InputMap.ActionEraseEvents(other);
            displaced = other;
            break;
        }

        InputMap.ActionEraseEvents(action);
        InputMap.ActionAddEvent(action, binding);
        Save();
        return true;
    }

    /// <summary>What to print on the button. Empty when the action is unbound.</summary>
    public static string Keycap(string action)
    {
        if (!InputMap.HasAction(action)) return string.Empty;

        foreach (InputEvent binding in InputMap.ActionGetEvents(action))
        {
            switch (binding)
            {
                case InputEventKey key:
                    return OS.GetKeycodeString(Printable(key));

                case InputEventMouseButton mouse:
                    return mouse.ButtonIndex switch
                    {
                        MouseButton.Left => "LMB",
                        MouseButton.Right => "RMB",
                        MouseButton.Middle => "MMB",
                        _ => $"M{(int)mouse.ButtonIndex}",
                    };
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// SelfTest clears this so that running --selftest never overwrites the
    /// keybinds of whoever happens to be sitting at the machine. Nothing else
    /// should touch it.
    /// </summary>
    internal static bool SaveEnabled = true;

    /// <summary>
    /// The letter to print for a key.
    ///
    /// Defaults are stored as PHYSICAL keycodes so WASD keeps its shape on
    /// AZERTY, but a physical code is a position rather than a letter, and only
    /// the display server knows which letter sits there. A headless one does
    /// not: asking it raised "Not supported by this display server" once per
    /// ability button, which is every frame the HUD rebuilds on a dedicated
    /// server. So ask only when someone is actually looking at a screen, and
    /// otherwise print the US-layout letter, which is what the code already is.
    /// </summary>
    private static Key Printable(InputEventKey key)
    {
        if (key.PhysicalKeycode == Key.None) return key.Keycode;
        if (DisplayServer.GetName() == "headless") return key.PhysicalKeycode;

        Key mapped = DisplayServer.KeyboardGetKeycodeFromPhysical(key.PhysicalKeycode);
        return mapped == Key.None ? key.PhysicalKeycode : mapped;
    }

    public static void Save()
    {
        if (!SaveEnabled) return;

        var file = new ConfigFile();

        foreach (string action in All)
        {
            foreach (InputEvent binding in InputMap.ActionGetEvents(action))
            {
                if (Encode(binding) is { } encoded) file.SetValue(Section, action, encoded);
                break;
            }
        }

        file.Save(SavePath);
    }

    private static void Load()
    {
        var file = new ConfigFile();
        if (file.Load(SavePath) != Error.Ok) return;

        foreach (string action in All)
        {
            if (!file.HasSectionKey(Section, action)) continue;
            if (Decode(file.GetValue(Section, action).AsString()) is not { } binding) continue;

            InputMap.ActionEraseEvents(action);
            InputMap.ActionAddEvent(action, binding);
        }
    }

    // -- wire form --------------------------------------------------------
    //
    // Deliberately narrow: keyboard and mouse only. An unrecognised line is
    // dropped and the default stands, so a file written by a later build with
    // gamepad support degrades to a playable keyboard instead of no input.

    private static string Encode(InputEvent binding) => binding switch
    {
        InputEventKey { PhysicalKeycode: not Key.None } key => $"phys:{(int)key.PhysicalKeycode}",
        InputEventKey key => $"key:{(int)key.Keycode}",
        InputEventMouseButton mouse => $"mouse:{(int)mouse.ButtonIndex}",
        _ => null,
    };

    private static InputEvent Decode(string encoded)
    {
        string[] parts = encoded.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int value)) return null;

        return parts[0] switch
        {
            "phys" => Physical((Key)value),
            "key" => new InputEventKey { Keycode = (Key)value },
            "mouse" => Mouse((MouseButton)value),
            _ => null,
        };
    }

    private static InputEventKey Physical(Key key) => new() { PhysicalKeycode = key };

    private static InputEventMouseButton Mouse(MouseButton button) => new() { ButtonIndex = button };

    private static string[] BuildAbilityNames()
    {
        var names = new string[AbilitySlots];
        for (int slot = 0; slot < AbilitySlots; slot++) names[slot] = $"ability_{slot + 1}";
        return names;
    }

    private static string[] BuildAll()
    {
        var actions = new List<string> { MoveUp, MoveLeft, MoveDown, MoveRight };
        actions.AddRange(AbilityActions);
        actions.Add(SelfCast);
        actions.Add(CameraUp);
        actions.Add(CameraDown);
        actions.Add(CameraLeft);
        actions.Add(CameraRight);
        actions.Add(CameraRecenter);
        actions.Add(MeterMode);
        return actions.ToArray();
    }
}
