"""Generate synthetic large datasets for the scale benchmark (tools/benchmark.ps1).

Requires pyshp==2.3.1 (see tests/data/README.md). All coordinates and attributes are invented.

  python tools/generate-benchmark-data.py <out-dir> [count ...]

For every count it writes two shapefiles into <out-dir>:

  Bench_Streets_<count>  PolylineZ street network: a jittered grid of road segments, each a
                         gently curved run of 4-12 vertices, with ~5% two-part features.
  Bench_Parcels_<count>  PolygonZ parcels: a grid of lots with 5-9 vertex rings, ~3% with a
                         courtyard hole and ~2% with a detached second ring.

Both carry five attributes (text, integer, double, date-like text) so attribute hashing and
field decisions cost what they would on real data. Coordinates are Web Mercator metres near the
origin the e2e fixtures use, so the same test host project shows them.
"""
import math
import random
import sys
from pathlib import Path

import shapefile

WKT = ('PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",GEOGCS["GCS_WGS_1984",'
       'DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],'
       'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],'
       'PROJECTION["Mercator_Auxiliary_Sphere"],PARAMETER["False_Easting",0.0],'
       'PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",0.0],'
       'PARAMETER["Standard_Parallel_1",0.0],PARAMETER["Auxiliary_Sphere_Type",0.0],'
       'UNIT["Meter",1.0],AUTHORITY["EPSG","3857"]]')

ORIGIN = (1000.0, 1000.0)
KINDS = ["residential", "collector", "arterial", "service", "alley"]
ZONES = ["R1", "R2", "R3", "C1", "C2", "M1", "OS"]


def fields(writer):
    writer.field("name", "C", 60)
    writer.field("kind", "C", 20)
    writer.field("rank", "N", 10, 0)
    writer.field("value", "F", 18, 4)
    writer.field("updated", "C", 24)


def finish(base, count, kind):
    base.with_suffix(".prj").write_text(WKT, encoding="ascii")
    base.with_suffix(".cpg").write_text("UTF-8", encoding="ascii")
    with shapefile.Reader(str(base)) as reader:
        assert len(reader) == count, (base, len(reader), count)
        assert reader.shapeType == kind
    print("{}: {} features".format(base.name, count))


def curved_run(rng, x0, y0, x1, y1, z):
    n = rng.randint(4, 12)
    bend = rng.uniform(-6.0, 6.0)
    nx, ny = -(y1 - y0), (x1 - x0)
    length = math.hypot(nx, ny) or 1.0
    nx, ny = nx / length, ny / length
    pts = []
    for i in range(n):
        t = i / (n - 1)
        s = math.sin(math.pi * t) * bend
        pts.append((x0 + (x1 - x0) * t + nx * s, y0 + (y1 - y0) * t + ny * s, z))
    return pts


def streets(out, count, seed=7):
    rng = random.Random(seed)
    base = out / "Bench_Streets_{}".format(count)
    # Two segments per grid node (east and north) -> side = sqrt(count / 2).
    side = max(1, int(math.ceil(math.sqrt(count / 2.0))))
    spacing = 80.0
    written = 0
    with shapefile.Writer(str(base), shapeType=shapefile.POLYLINEZ) as w:
        fields(w)
        for gy in range(side + 1):
            for gx in range(side + 1):
                for east in (True, False):
                    if written >= count:
                        break
                    x0 = ORIGIN[0] + gx * spacing + rng.uniform(-4, 4)
                    y0 = ORIGIN[1] + gy * spacing + rng.uniform(-4, 4)
                    x1, y1 = (x0 + spacing, y0) if east else (x0, y0 + spacing)
                    z = 10.0 + rng.uniform(0, 2)
                    parts = [curved_run(rng, x0, y0, x1, y1, z)]
                    if rng.random() < 0.05:
                        # A second, disjoint piece (a split median or a dog-leg).
                        ox, oy = (0.0, 6.0) if east else (6.0, 0.0)
                        parts.append(curved_run(rng, x0 + ox, y0 + oy, x1 + ox, y1 + oy, z))
                    w.linez(parts)
                    w.record("Street {}".format(written + 1), KINDS[written % len(KINDS)],
                             written + 1, round(rng.uniform(5, 40), 4),
                             "2026-0{}-1{}T00:00:00".format(1 + written % 9, written % 10))
                    written += 1
                if written >= count:
                    break
            if written >= count:
                break
    finish(base, count, shapefile.POLYLINEZ)


def ring(rng, cx, cy, w, h, z, hole=False):
    # A lot outline with a few extra vertices along its edges (5-9 distinct points).
    corners = [(cx - w / 2, cy - h / 2), (cx - w / 2, cy + h / 2), (cx + w / 2, cy + h / 2), (cx + w / 2, cy - h / 2)]
    pts = []
    for i, (ax, ay) in enumerate(corners):
        bx, by = corners[(i + 1) % 4]
        pts.append((ax, ay, z))
        for _ in range(rng.randint(0, 1) + (1 if i == 0 else 0)):
            t = rng.uniform(0.2, 0.8)
            pts.append((ax + (bx - ax) * t, ay + (by - ay) * t, z))
    pts.append(pts[0])
    # Shapefile outer rings are clockwise; the corners above already run clockwise.
    return list(reversed(pts)) if hole else pts


def parcels(out, count, seed=11):
    rng = random.Random(seed)
    base = out / "Bench_Parcels_{}".format(count)
    side = max(1, int(math.ceil(math.sqrt(count))))
    pitch = 30.0
    written = 0
    with shapefile.Writer(str(base), shapeType=shapefile.POLYGONZ) as w:
        fields(w)
        for gy in range(side):
            for gx in range(side):
                if written >= count:
                    break
                cx = ORIGIN[0] + gx * pitch
                cy = ORIGIN[1] + gy * pitch
                lw, lh = rng.uniform(20, 26), rng.uniform(20, 26)
                z = 10.0
                rings = [ring(rng, cx, cy, lw, lh, z)]
                r = rng.random()
                if r < 0.03:
                    rings.append(ring(rng, cx, cy, lw / 3, lh / 3, z, hole=True))
                elif r < 0.05:
                    # A detached strip (garage lot) just inside the pitch gap.
                    rings.append(ring(rng, cx + lw / 2 + 2.0, cy, 1.5, lh / 2, z))
                w.polyz(rings)
                w.record("Parcel {}".format(written + 1), ZONES[written % len(ZONES)],
                         written + 1, round(lw * lh, 4),
                         "2026-0{}-2{}T00:00:00".format(1 + written % 9, written % 8))
                written += 1
            if written >= count:
                break
    finish(base, count, shapefile.POLYGONZ)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    out = Path(argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    counts = [int(c) for c in argv[2:]] or [1000, 10000, 100000]
    for count in counts:
        streets(out, count)
        parcels(out, count)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
