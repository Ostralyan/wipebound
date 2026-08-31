# Wipebound

Co-op PvE boss encounters with Warcraft-style controls: right-click to move,
hotkey abilities, an RTS camera. Godot 4.7 (.NET / C#).

A boss picks a mechanic, warns you with a shape on the ground, and hurts whoever
is standing in it when the warning expires. That loop runs across the network,
server-authoritative, with a shared clock. Bosses beyond the first, real player
abilities, and art are not built yet.

## Running it

**In the editor.** Open the project and press F5. The lobby panel is top-left:
press **Host**, or type an address and press **Join**.

**Two windows at once** (how you will test almost everything): **Debug > Customize
Run Instances**, enable it, set the count to 2. Press F5, host in one, join
`127.0.0.1` in the other.

**From the command line.** Everything after `--` goes to the game:

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
| `Q` | Attack the boss (placeholder ability) |
| `WASD` / arrows / middle-drag | Pan the camera |
| Mouse wheel | Zoom |
| `Space` | Re-lock the camera to your hero |

Edge scrolling is off by default -- with two windows on one monitor it fires
constantly. Turn it on in the `RtsCamera` inspector when you play fullscreen.

## Where things are

```
src/
  Main.tscn              entry scene: arena, spawner, boss, camera, HUD
  Net/
    NetworkManager.cs    peer lifecycle, hero spawning, transport seam
    NetClock.cs          the shared timeline every peer agrees on
  Combat/
    Boss.cs              the encounter loop: decide, warn, resolve, recover
    BossAbility.cs       one mechanic, as data
    BossPhase.cs         which mechanics are live, and how much rest between them
    TelegraphArea.cs     the frozen footprint + the signed field that defines it
    TelegraphView.cs     the client-side drawing, and the shader that mirrors it
    DefaultEncounter.cs  the starting fight
    Effects/             damage, soak, stack, knockback
  Player/
    Hero.tscn/.cs        click-to-move hero, split-authority synchronizers
    RtsCamera.cs         fixed-angle pan/follow camera + mouse-to-ground raycast
  World/Main.cs          navmesh bake, ground material, world registration
  UI/                    lobby, boss frame, cast bar, net readout
```

## The rules this codebase exists to hold

### 1. Clients send intent, never outcomes

A client may say "I pressed Q aiming here." It may never say "this did 4500
damage." Every damage number is computed on the server from the server's own copy
of the data, so a cheater editing shipped ability values changes their own UI and
nothing else.

`Hero.RequestCast` is the only `RpcMode.AnyPeer` method in the project. That is
the entire attack surface, and it should stay small enough to audit by reading it.

### 2. Authority is split per property, not per node

`Hero` carries two `MultiplayerSynchronizer` children:

- **`MoveSync`** — authority is the owning client. Position and facing, so your own
  dodging is instant with no prediction code. In a PvE game that is the right
  design: what you see is what resolves.
- **`StatsSync`** — authority is the server. Health, and the spawn point. A client
  cannot write these.

Sharing one client-authoritative synchronizer would let any player set their own
health to 999999 and have the engine replicate it faithfully.

A subtlety worth knowing, because it cost an afternoon: **spawn state is sent by a
synchronizer's authority.** A client-authoritative synchronizer therefore cannot
carry a server-decided starting position — the client does not exist yet at spawn
time, the property arrives as zero, and the client publishes that zero straight
back over the real spawn point. That is why `SpawnPoint` rides on `StatsSync`.

Because movement is client-authoritative, the server keeps `Hero.ServerPosition`,
a speed-clamped copy of the claimed position, and every range and area check uses
it. When the server moves a hero itself (knockback, respawn) it holds that value
at its own destination rather than adopting the client's — otherwise a mechanic
would look like cheating, or hand a cheater a free window.

### 3. The server is a role, not a player

`--server` runs the full simulation with no hero. Moving from "a friend hosts" to
"a headless box hosts" is a launch flag, not a refactor. Worth knowing: whoever
hosts *is* the authority and can cheat undetectably. Server authority means the
other players can't.

## The encounter loop

**Decide → Warn → Resolve → Recover**, all on the server. Clients receive one
broadcast per cast and draw a picture. They hold no encounter state, because a
telegraph is *a rendering of a server decision*, not game state — if a client never
draws it, the damage still lands.

### The trailing edge

Resolving the instant a telegraph visually ends produces the worst bug in the
genre: *"I dodged that and still died."*

With one-way latency **L**, a client only starts drawing at **L**. Its circle
finishes at **L + duration**, and a player stepping out right then has that move
reach the server at **L + duration + L**. Resolving at `duration` judges them on a
position from **L** ago — from before they moved.

So the server resolves one full round trip *past* the visual end. The damage lands
about a tenth of a second late, which nobody perceives, and the same wait also
guarantees every client finished seeing the warning before it bit them. One number,
both problems. It is `MinimumResolveGrace` on the boss, raised to the worst
connected round trip when that is larger.

### Everything is absolute time

Casts are broadcast as `(castStart, castEnd)` on the shared clock, never "starting
now". A peer whose packet arrived late therefore spawns an already part-filled
telegraph that still finishes on time, instead of a full-length one that finishes
late and lies about the deadline. The HUD cast bar reads the same two numbers, so
the bar and the circle always end together.

### Frozen at cast start

`TelegraphArea` is computed once and never updated. A telegraph parented to a
moving boss renders somewhere different from where the server resolves it, because
the boss's position on a client is both a ping late and interpolated. Even a
frontal cone snapshots the boss's position and facing at cast start, then stops
listening.

### One shape, two implementations

`TelegraphArea.Field()` is negative inside, zero on the boundary, positive outside,
in metres. The shader in `TelegraphView.cs` evaluates the identical expression.
**If you edit one you must edit the other** — if the drawn edge and the tested edge
disagree, players standing on the boundary get hit by nothing and learn to distrust
every telegraph in the game.

The resolve log is how you check them against each other:

```
[resolve] Collapse at (0.0, 0.0)
[resolve]   hero 1823715600 at (10.0, 0.0) field=-3.00m -> HIT
[resolve]   Damage 42 to 1 inside
```

Stand on the rim and watch that number cross zero.

### Effects, not damage numbers

An ability owns a *list* of effects. That is the difference between a dodge-em-up
and a co-op game — damage alone only ever asks "did you move?".

| Effect | Asks players to |
| --- | --- |
| `DamageEffect` | Get out |
| `SoakEffect` | Send somebody IN, or everyone pays |
| `StackEffect` | Gather, and split it |
| `KnockbackEffect` | Mind where you are standing relative to everything else |

**Colour is a contract**, and players trust it within thirty seconds: red means get
out, blue means get in, amber means gather. A red circle that turns out to be a
soak is worse than no telegraph at all.

Knockback is worth understanding: the server cannot move a client-authoritative
hero, so it computes the destination, adopts it as the validated position, and
*asks* the owner to slide there. A modified client could refuse — in PvE that costs
the cheater a mechanic and nobody else anything, which is the price of
prediction-free dodging everywhere else.

## Authoring bosses

`DefaultEncounter.Build()` is the starting fight, in code. `BossAbility`,
`BossPhase` and every effect are `[GlobalClass]` Resources, so the same encounter
can be authored as `.tres` files in the inspector and assigned to `Boss.Phases`
instead — leave that array empty and the code default fills in.

Abilities are shared between phases on purpose: cooldowns are tracked per instance,
so a mechanic that survives a phase change keeps its timer rather than being free
to fire again immediately.

## Testing under real latency

Everything above is invisible on localhost, where L is effectively zero and even
the naive version looks perfect. On macOS, Apple's **Network Link Conditioner**
imposes 100ms and 200ms profiles system-wide. Test at 200ms — that is where the
trailing edge stops being theoretical. The HUD shows measured round trip and clock
offset in the bottom left.

## Next

- Player abilities beyond the placeholder `Q`, on the same intent-only RPC pattern.
- Threat, so `NearestPlayer` targeting can become a real tank rule.
- Adds, as a `SpawnEffect` — the loop already takes any effect you write.
- Interrupt and cancellation, so a mechanic can be stopped mid-cast.

## Transport

ENet works on localhost and LAN, and over the internet only with port forwarding.
`NetworkManager.CreateServerPeer` / `CreateClientPeer` are the only two methods
that know that. To let friends connect without port forwarding, swap their bodies
for `SteamMultiplayerPeer` (relay + NAT punchthrough); nothing above them changes.
