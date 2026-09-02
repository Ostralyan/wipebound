#!/usr/bin/env python3
"""Reconstruct the whole world at one instant, from a combat log alone.

This exists to answer a question that is easy to assert and hard to be sure of:
is the log actually enough to replay the fight? It is a reference reader, so it
also documents the format by using it -- if a website can do what this does, it
can draw a frame.

    tools/replay-frame.py <run.json.gz> [seconds]
"""
import gzip, json, math, sys

EVENT = {0: "Damage", 1: "Heal", 2: "CastStart", 3: "CastResolve", 4: "Judged",
         5: "AuraApplied", 6: "AuraRemoved", 7: "Interrupt", 8: "Dispel",
         9: "Death", 10: "Spawn"}
SHAPE = {0: "circle", 1: "cone", 2: "rectangle", 3: "ring"}


def frame(doc, at_ms):
    tracks, absent = doc["tracks"], doc["tracks"]["absent"]
    stride, interval = len(tracks["stride"]), tracks["interval_ms"]
    index = min(at_ms // interval, tracks["samples"] - 1)
    names = {a["id"]: a for a in doc["actors"]}

    actors = []
    for raw_id, lane in tracks["lanes"].items():
        base = index * stride
        if base + stride > len(lane) or lane[base] == absent:
            continue
        x, z, facing, health = lane[base:base + stride]
        who = names.get(int(raw_id), {})
        actors.append({
            "name": who.get("name", raw_id),
            "kind": who.get("kind", "?"),
            "x": x / 100, "z": z / 100,
            "facing": facing / 10,
            "health": health / 10,
        })

    # Anything drawn on the ground whose window contains this instant.
    ground = [
        {"kind": kind, "ability": item.get("ability") or item.get("name"),
         "shape": SHAPE.get(item["area"]["shape"], "?"),
         "x": item["area"]["cx"], "z": item["area"]["cz"],
         "radius": item["area"]["radius"],
         "progress": (at_ms - item["from_ms"]) / max(1, item["until_ms"] - item["from_ms"])}
        for kind, group in (("telegraph", doc["telegraphs"]), ("hazard", doc["hazards"]))
        for item in group if item["from_ms"] <= at_ms <= item["until_ms"]
    ]

    # Projectiles are a formula, not samples: the same one the client flies.
    flying = []
    for shot in doc["projectiles"]:
        if not shot["from_ms"] <= at_ms <= shot["until_ms"]:
            continue
        travelled = (at_ms - shot["from_ms"]) / 1000 * shot["speed_cms"] / 100
        flying.append({
            "ability": shot["ability"],
            "x": shot["x_cm"] / 100 + shot["dx"] * travelled,
            "z": shot["z_cm"] / 100 + shot["dz"] * travelled,
        })

    # Auras are a fold over the event stream up to this instant.
    auras = {}
    for t, kind, source, target, ability, _amount, a, b in doc["events"]:
        if t > at_ms:
            break
        if kind == 5:
            auras.setdefault(target, {})[ability] = (a, t + b)
        elif kind == 6:
            auras.get(target, {}).pop(ability, None)

    return actors, ground, flying, auras, names


def main():
    doc = json.loads(gzip.open(sys.argv[1]).read())
    at_ms = int(float(sys.argv[2]) * 1000) if len(sys.argv) > 2 else doc["duration_ms"] // 2
    actors, ground, flying, auras, names = frame(doc, at_ms)

    print(f"=== {doc['boss']} at t={at_ms / 1000:.1f}s of {doc['duration_ms'] / 1000:.1f}s ===")
    for actor in sorted(actors, key=lambda a: a["kind"]):
        held = auras.get(next((i for i, n in names.items() if n["name"] == actor["name"]), None), {})
        buffs = " ".join(f"{doc['abilities'][i]}x{s}" for i, (s, _) in held.items()) if held else "-"
        print(f"  {actor['kind']:<7} {actor['name']:<16} ({actor['x']:>7.1f},{actor['z']:>7.1f}) "
              f"facing {actor['facing']:>5.1f}  hp {actor['health']:>5.1f}%  {buffs}")

    print(f"  -- {len(ground)} on the ground, {len(flying)} in the air --")
    for item in ground:
        print(f"  {item['kind']:<9} {item['ability']:<16} {item['shape']:<9} "
              f"at ({item['x']:>7.1f},{item['z']:>7.1f}) r={item['radius']:.1f} "
              f"{item['progress'] * 100:>5.1f}% wound up")
    for shot in flying[:4]:
        print(f"  projectile {shot['ability']:<14} at ({shot['x']:>7.1f},{shot['z']:>7.1f})")


main()
