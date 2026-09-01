using Godot;
using Wipebound.Combat;
using Wipebound.Net;

namespace Wipebound.UI;

/// <summary>Minimal host/join panel. Replace wholesale when you build a real lobby.</summary>
public partial class Lobby : PanelContainer
{
    private LineEdit _address;
    private Label _status;
    private Button _hostButton;
    private Button _joinButton;
    private Button _leaveButton;
    private OptionButton _classPicker;

    /// <summary>
    /// What each class is FOR, in the one place a player reads before choosing.
    ///
    /// The Verdant's line is not flavour: it cannot heal itself, so someone who
    /// picks it without knowing that will conclude the game is broken rather
    /// than that the class has a dependency.
    /// </summary>
    private static string Describe(HeroClass hero) => hero switch
    {
        HeroClass.Warden => "Warden -- shields the group, interrupts, shoves",
        HeroClass.Ember => "Ember -- ranged damage, leaves ground behind",
        HeroClass.Verdant => "Verdant -- healer, and cannot heal itself",
        _ => hero.ToString(),
    };

    public override void _Ready()
    {
        _address = GetNode<LineEdit>("Margin/Rows/Address");
        _status = GetNode<Label>("Margin/Rows/Status");
        _hostButton = GetNode<Button>("Margin/Rows/HostButton");
        _joinButton = GetNode<Button>("Margin/Rows/JoinButton");
        _leaveButton = GetNode<Button>("Margin/Rows/LeaveButton");

        // Hosting is offered again, labelled for what it costs.
        //
        // Removing this button was the wrong mechanism for the right policy. What
        // protects the ladder is that a run record carries the authority it was
        // played under and the backend refuses anything not marked dedicated --
        // and that guard holds whether or not this button exists. Hiding it
        // protected nothing and left the game with no way to start from the UI.
        _hostButton.Text = "Host (practice, not ranked)";
        _hostButton.Pressed += () => NetworkManager.Instance.Host();
        _joinButton.Pressed += () => NetworkManager.Instance.Join(_address.Text.StripEdges());
        _leaveButton.Pressed += () => NetworkManager.Instance.Leave();

        // Built in code rather than in the scene, so the list cannot drift out of
        // step with the HeroClass enum the kits are actually keyed on.
        _classPicker = new OptionButton { Name = "ClassPicker" };
        foreach (HeroClass hero in System.Enum.GetValues<HeroClass>())
            _classPicker.AddItem(Describe(hero), (int)hero);

        _classPicker.Select(NetworkManager.Instance.PreferredClassId);
        _classPicker.ItemSelected += id => NetworkManager.Instance.SetPreferredClass((int)id);

        Node rows = _hostButton.GetParent();
        rows.AddChild(_classPicker);
        rows.MoveChild(_classPicker, _hostButton.GetIndex());

        NetworkManager.Instance.StatusChanged += message => _status.Text = message;
        NetworkManager.Instance.ModeChanged += RefreshButtons;

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool inSession = NetworkManager.Instance.InSession;
        _hostButton.Visible = !inSession;
        _classPicker.Visible = !inSession;
        _joinButton.Visible = !inSession;
        _address.Visible = !inSession;
        _leaveButton.Visible = inSession;
    }
}
