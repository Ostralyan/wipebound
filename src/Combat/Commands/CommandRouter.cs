using Godot;
using System.Collections.Generic;
using Wipebound.Net;
using Wipebound.Player;

namespace Wipebound.Combat.Commands;

/// <summary>
/// The single door from clients into the server's simulation.
///
/// Every client request in the game arrives at Submit. That is deliberate: the
/// project's security property is "clients send intent, never outcomes", and it
/// is only auditable if the number of places a client can reach stays small. One
/// RpcMode.AnyPeer method means the audit is reading this file.
///
/// It is also the only sensible place for the things that would otherwise be
/// copied into every verb: who really sent it, whether they are sending too
/// fast, and a log line for what happened.
/// </summary>
public partial class CommandRouter : Node
{
    public static CommandRouter Instance { get; private set; }

    /// Sustained rate a single peer may submit at.
    [Export] public float CommandsPerSecond { get; set; } = 12f;

    /// How much a peer may bank while idle, so normal bursty play is not punished.
    [Export] public float BurstAllowance { get; set; } = 20f;

    [Export] public bool LogAccepted { get; set; } = true;

    private readonly Dictionary<int, float> _tokens = new();
    private readonly Dictionary<int, double> _lastRefill = new();

    private static bool IsServer => NetworkManager.Instance.IsServer;

    public override void _Ready()
    {
        Instance = this;
        Multiplayer.PeerDisconnected += id =>
        {
            _tokens.Remove((int)id);
            _lastRefill.Remove((int)id);
        };
    }

    /// <summary>Client-side entry point. Everything a player does funnels through here.</summary>
    public static void Send(ClientCommandType type, Godot.Collections.Dictionary payload)
        => Instance?.RpcId(NetworkManager.ServerPeerId, MethodName.Submit, (int)type, payload);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void Submit(int rawType, Godot.Collections.Dictionary payload)
    {
        if (!IsServer) return;

        // The transport says who sent this. The payload cannot lie about it.
        // Zero means it was called locally, i.e. by the host itself.
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender == 0) sender = Multiplayer.GetUniqueId();

        double now = NetClock.Instance.ServerTime;

        if (!AllowRate(sender, now))
        {
            GD.PushWarning($"[cmd] peer {sender} rate limited");
            return;
        }

        ClientCommand command = ClientCommand.Create((ClientCommandType)rawType);
        if (command is null)
        {
            GD.PushWarning($"[cmd] peer {sender} sent unknown command type {rawType}");
            return;
        }

        if (!command.Read(payload))
        {
            GD.PushWarning($"[cmd] peer {sender} sent a malformed {(ClientCommandType)rawType}");
            return;
        }

        // Resolved from the SENDER, never from the payload. This is what makes
        // "act as somebody else's hero" inexpressible rather than merely rejected.
        Hero hero = HeroFor(sender);
        if (hero is null) return;

        var context = new CommandContext { Hero = hero, PeerId = sender, Now = now };

        if (!command.Validate(context, out string reason))
        {
            GD.Print($"[cmd] peer {sender} {command.Describe()} REJECTED: {reason}");
            if (command is CastAbilityCommand cast) cast.Reject(context);
            return;
        }

        command.Execute(context);
        if (LogAccepted) GD.Print($"[cmd] peer {sender} {command.Describe()} ok");
    }

    private Hero HeroFor(int peerId)
    {
        foreach (Node node in GetTree().GetNodesInGroup(Hero.GroupName))
            if (node is Hero hero && hero.PeerId == peerId)
                return hero;
        return null;
    }

    /// <summary>Token bucket. Cooldowns already stop the damage; this stops the CPU burn.</summary>
    private bool AllowRate(int peerId, double now)
    {
        float tokens = _tokens.TryGetValue(peerId, out float stored) ? stored : BurstAllowance;
        double last = _lastRefill.TryGetValue(peerId, out double when) ? when : now;

        tokens = Mathf.Min(BurstAllowance, tokens + (float)(now - last) * CommandsPerSecond);
        _lastRefill[peerId] = now;

        if (tokens < 1f)
        {
            _tokens[peerId] = tokens;
            return false;
        }

        _tokens[peerId] = tokens - 1f;
        return true;
    }
}
