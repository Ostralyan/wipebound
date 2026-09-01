using Godot;
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

        NetworkManager.Instance.StatusChanged += message => _status.Text = message;
        NetworkManager.Instance.ModeChanged += RefreshButtons;

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool inSession = NetworkManager.Instance.InSession;
        _hostButton.Visible = !inSession;
        _joinButton.Visible = !inSession;
        _address.Visible = !inSession;
        _leaveButton.Visible = inSession;
    }
}
