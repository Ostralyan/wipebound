using Godot;
using System.Collections.Generic;

namespace Wipebound.Net;

/// <summary>
/// Where heroes start, as slots rather than a running count.
///
/// This used to index off the number of heroes currently alive in the session,
/// which is only correct while nobody ever leaves. If a middle player
/// disconnected, the next to join was handed a slot somebody was already
/// standing in.
/// </summary>
public static class SpawnRing
{
    public const int Slots = NetworkManager.MaxPlayers;
    public const float Radius = 10f;

    /// <summary>Lowest slot nobody currently holds.</summary>
    public static int NextFreeIndex(IReadOnlySet<int> occupied)
    {
        for (int slot = 0; slot < Slots; slot++)
            if (!occupied.Contains(slot)) return slot;

        return 0;   // over capacity; doubling up beats refusing to spawn
    }

    public static Vector3 PointFor(int slot)
    {
        float angle = Mathf.Tau * slot / Slots;
        return new Vector3(Mathf.Cos(angle) * Radius, 0f, Mathf.Sin(angle) * Radius);
    }
}
