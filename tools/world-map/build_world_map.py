#!/usr/bin/env python3
"""
Builds the country outlines of the background world map (frontend/portfolio-dashboard/src/components/world-map-geo.ts).

Source: Natural Earth 1:50m admin-0 countries (public domain) as TopoJSON from the world-atlas package (ISC licence),
pinned below – detailed enough for the map's zoom (coastlines, Hong Kong and Singapore). Countries are keyed by ISO 3166-1
numeric code (the `id` of world-atlas); a few disputed areas without a code are kept as land under their name.
Projection: equirectangular, x = longitude + 180, y = TOP - latitude (degrees), Antarctica left out. Rings are simplified
(Douglas-Peucker, TOLERANCE degrees), made continuous across the antimeridian (see `unwrap`), rounded to 0.02° and islands below MIN_AREA dropped unless they are a country's
largest ring – the browser places exchange cities and camera frames with the same formula. Standard library only.

Usage: python3 tools/world-map/build_world_map.py
"""
import json
import os
import subprocess

URL = "https://cdn.jsdelivr.net/npm/world-atlas@2.0.2/countries-50m.json"
TOLERANCE = 0.07
MIN_AREA = 0.05  # square degrees
TOP, BOTTOM = 84.0, -58.0
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "frontend", "portfolio-dashboard", "src", "components", "world-map-geo.ts")


def simplify(points, tolerance):
    """Douglas-Peucker on a closed ring (first and last point kept)."""
    if len(points) < 4:
        return points
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        a, b = stack.pop()
        (ax, ay), (bx, by) = points[a], points[b]
        dx, dy = bx - ax, by - ay
        norm = (dx * dx + dy * dy) ** 0.5
        best, index = -1.0, -1
        for i in range(a + 1, b):
            px, py = points[i]
            d = abs(dy * px - dx * py + bx * ay - by * ax) / norm if norm else ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5
            if d > best:
                best, index = d, i
        if best > tolerance:
            keep[index] = True
            stack.extend([(a, index), (index, b)])
    return [p for p, k in zip(points, keep) if k]


def unwrap(points):
    """
    A ring that crosses the antimeridian (Russia's Chukotka, Fiji, …) jumps from +180° to −180°; drawn as is, that jump
    cuts a line across the whole map and the fill leaves a hole. Longitudes are made continuous instead (the ring may then
    reach beyond ±180°), and such a ring is drawn a second time shifted by 360° so the overflowing part appears at the
    opposite edge (the view box clips the rest).
    """
    out, offset, previous = [], 0.0, None
    for lon, lat in points:
        if previous is not None:
            if lon + offset - previous > 180:
                offset -= 360
            elif lon + offset - previous < -180:
                offset += 360
        previous = lon + offset
        out.append((previous, lat))
    rings = [out]
    if max(x for x, _ in out) > 180:
        rings.append([(x - 360, y) for x, y in out])
    if min(x for x, _ in out) < -180:
        rings.append([(x + 360, y) for x, y in out])
    return rings


def area(points):
    return abs(sum(x0 * y1 - x1 * y0 for (x0, y0), (x1, y1) in zip(points, points[1:] + points[:1]))) / 2


def main():
    topo = json.loads(subprocess.run(["curl", "-sSf", "--max-time", "60", URL], check=True, capture_output=True).stdout)
    sx, sy = topo["transform"]["scale"]
    tx, ty = topo["transform"]["translate"]
    arcs = []
    for arc in topo["arcs"]:  # quantized, delta-encoded
        x = y = 0
        points = []
        for dx, dy in arc:
            x += dx
            y += dy
            points.append((x * sx + tx, y * sy + ty))
        arcs.append(points)

    def ring(indices):
        points = []
        for i in indices:
            arc = arcs[i] if i >= 0 else list(reversed(arcs[~i]))
            points.extend(arc if not points else arc[1:])
        return points

    countries = {}
    for geometry in topo["objects"]["countries"]["geometries"]:
        if geometry.get("type") not in ("Polygon", "MultiPolygon"):
            continue
        key = geometry.get("id") or geometry["properties"]["name"]
        polygons = geometry["arcs"] if geometry["type"] == "MultiPolygon" else [geometry["arcs"]]
        rings = [ring(indices) for polygon in polygons for indices in polygon]
        rings = [r for r in rings if max(lat for _, lat in r) >= BOTTOM]  # Antarctica left out
        if not rings:
            continue
        largest = max(rings, key=area)
        parts = []
        for coordinates in rings:
            if coordinates is not largest and area(coordinates) < MIN_AREA:
                continue
            for shifted in unwrap(simplify(coordinates, TOLERANCE)):
                path, last = [], None
                for lon, lat in shifted:
                    p = (round((lon + 180) * 50) / 50, round((TOP - lat) * 50) / 50)
                    if p != last:
                        path.append(f"{p[0]:g} {p[1]:g}")
                        last = p
                if len(path) >= 3:
                    parts.append("M" + "L".join(path) + "Z")
        if parts:
            countries[key] = "".join(parts)

    with open(OUT, "w", encoding="utf-8") as f:
        f.write("// Generated by tools/world-map/build_world_map.py – do not edit.\n")
        f.write("// Natural Earth 1:50m countries (public domain) via world-atlas@2.0.2 (ISC); equirectangular, degrees;\n")
        f.write("// keys are ISO 3166-1 numeric codes (names for areas without one).\n")
        f.write(f"export const MAP_TOP = {TOP:g}\nexport const MAP_HEIGHT = {TOP - BOTTOM:g}\n")
        f.write("export const COUNTRY_PATHS: Record<string, string> = {\n")
        for key in sorted(countries):
            f.write(f"  {json.dumps(key)}: '{countries[key]}',\n")
        f.write("}\n")
    print(f"{OUT}: {len(countries)} countries, {os.path.getsize(OUT) // 1024} KB")


if __name__ == "__main__":
    main()
