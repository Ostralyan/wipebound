# Wipebound

Co-op PvE boss encounters with Warcraft-style controls: right-click to move,
hotkey abilities, an RTS camera. Godot 4.7 (.NET / C#).

This repo currently contains the networking and movement skeleton -- enough to
connect two machines, move heroes around, and hit a training dummy through a
server-authoritative ability. Bosses, real abilities, and art are not built yet.

## Running it

**In the editor.** Open the project and press F5. The lobby panel appears in the
top left: press **Host**, or type an address and press **Join**.

**Two windows at once** (the way you will test almost everything): in the editor,
**Debug > Customize Run Instances**, enable it and set the count to 2. Press F5 and
you get two game windows. Host in one, Join `127.0.0.1` in the other.

**From the command line.** Everything after `--` is passed to the game:

```
godot -- --host
godot -- --join 127.0.0.1
godot --headless -- --server          # dedicated: simulates, has no hero
godot -- --host --port 7788           # any of the above, on another port
```

## Controls

| Input | Does |
| --- | --- |
| Right-click | Move your hero there |
| `Q` | Cast the placeholder ability at the training dummy |
| `WASD` / arrows / middle-drag | Pan the camera |
| Mouse wheel | Zoom |
| `Space` | Re-lock the camera to your hero |

Edge scrolling exists but is off by default -- with two windows on one monitor it
fires constantly. Turn it on in the `RtsCamera` inspector once you play fullscreen.

## Where things are

```
src/
  Main.tscn            entry scene: arena, spawner, camera, lobby
  Net/
    NetworkManager.cs  autoload -- peer lifecycle, hero spawning, transport seam
  Player/
    Hero.tscn/.cs      click-to-move hero, split-authority synchronizers
    RtsCamera.cs       fixed-angle pan/follow camera + mouse-to-ground raycast
  World/
    Main.cs            navmesh bake, ground material, world registration
    TrainingDummy.cs   placeholder for the boss slot
  UI/
    Lobby.cs           host/join panel
```

## The three rules this scaffold exists to hold

**1. Clients send intent, never outcomes.** A client may say "I pressed Q aiming
here." It may never say "this did 4500 damage." Every damage number is computed on
the server from the server's own copy of the stats, so a cheater editing the shipped
ability data changes their own UI and nothing else. `Hero.RequestCast` is the only
`RpcMode.AnyPeer` method in the project -- that is the entire attack surface, and it
should stay small enough to audit by reading it.

**2. Authority is split, per property, not per node.** `Hero` carries two
`MultiplayerSynchronizer` children:

- `MoveSync` -- authority is the owning client. Replicates position and facing, so
  your own dodging is instant with no prediction code. In a PvE game that is the
  right design: what you see is what resolves.
- `StatsSync` -- authority is the server. Replicates health. A client cannot write it.

If those shared one synchronizer with client authority, any player could set their
own health to 999999 and the engine would replicate it faithfully. Keep them split.

Because movement is client-authoritative, the server keeps `Hero.ServerPosition`, a
speed-clamped copy of the position the client claims. Every server-side range or area
check uses that, never the raw claim.

**3. The server is a role, not a player.** `--server` runs the full simulation with
no hero of its own. Moving from "a friend hosts" to "a headless box hosts" is a
launch flag, not a refactor. Worth knowing: whoever hosts *is* the authority and can
therefore cheat undetectably. Server-authority means the other players can't.

## Next

The vertical slice this is aimed at: **a boss that casts a telegraphed circle, and
players who take damage if they are standing in it when it resolves.** Once that
loop runs across two machines, everything after it is content.

Two design notes for when you get there:

- Anchor telegraphs to a **world position snapshot**, not to the boss's live
  transform. The boss's position reaches clients a ping late, so a telegraph that
  tracks a moving boss will hit players who visibly dodged it.
- Camera height follows from your **largest AoE radius**. Roughly twice its diameter
  needs to fit on screen vertically or the mechanic is unreadable rather than hard.
  Every value on `RtsCamera` applies live, so tune it with a telegraph on screen.

## Transport

ENet works on localhost and LAN, and over the internet only with port forwarding.
`NetworkManager.CreateServerPeer` / `CreateClientPeer` are the only two methods that
know that. To let friends connect without port forwarding, swap their bodies for
`SteamMultiplayerPeer` (relay + NAT punchthrough); nothing above them changes.
