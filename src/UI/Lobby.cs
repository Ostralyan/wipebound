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

        // No Host button. Whoever hosts IS the authority and can forge anything, so
        // a player-hosted session can never produce a rankable run. Local testing
        // uses --host, which still works and says so in the log.
        _hostButton.Visible = false;
        _joinButton.Pressed += () => NetworkManager.Instance.Join(_address.Text.StripEdges());
        _leaveButton.Pressed += () => NetworkManager.Instance.Leave();

        NetworkManager.Instance.StatusChanged += message => _status.Text = message;
        NetworkManager.Instance.ModeChanged += RefreshButtons;

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool inSession = NetworkManager.Instance.InSession;
        _hostButton.Visible = false;
        _joinButton.Visible = !inSession;
        _address.Visible = !inSession;
        _leaveButton.Visible = inSession;
    }
}
