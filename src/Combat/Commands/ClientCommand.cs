using Godot;
using Wipebound.Player;

namespace Wipebound.Combat.Commands;

public enum ClientCommandType
{
    CastAbility = 0,
}

/// <summary>Everything a command is allowed to act on. Resolved by the router.</summary>
public sealed class CommandContext
{
    /// <summary>
    /// The hero belonging to the peer that actually sent this, looked up from the
    /// transport's sender id. It is never read out of the payload, which is the
    /// whole reason "cast as somebody else's hero" is not expressible.
    /// </summary>
    public required Hero Hero { get; init; }

    public required int PeerId { get; init; }
    public required double Now { get; init; }
}

/// <summary>
/// One thing a client may ask the server to do.
///
/// This is the Command pattern used for the reason it actually pays off here, and
/// not for the reason it is usually sold. There is no undo. What it buys is that
/// every client request in the game arrives through ONE RpcMode.AnyPeer method,
/// so the attack surface stays a single function you can audit by reading it no
/// matter how many verbs the game grows -- and rate limiting, sender resolution
/// and the audit log get written once instead of copied per verb.
///
/// Read, Validate and Execute are separate on purpose. Read touches untrusted
/// bytes and may fail; Validate decides whether it is allowed and says why;
/// Execute assumes both already passed.
/// </summary>
public abstract class ClientCommand
{
    /// <summary>Parse untrusted input. Return false on anything malformed.</summary>
    public abstract bool Read(Godot.Collections.Dictionary payload);

    /// <summary>Server-side gate. Never trust the client to have checked any of this.</summary>
    public abstract bool Validate(CommandContext context, out string reason);

    public abstract void Execute(CommandContext context);

    public abstract string Describe();

    public static ClientCommand Create(ClientCommandType type) => type switch
    {
        ClientCommandType.CastAbility => new CastAbilityCommand(),
        _ => null,
    };
}
