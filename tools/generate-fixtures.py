"""Generate synthetic E2E fixtures. Requires pyshp==2.3.1 (see tests/data/README.md).

All coordinates and attributes are invented. No source GIS data is read.
Only the six explicitly named fixtures are overwritten in tests/data/SHP.
"""
from pathlib import Path
import hashlib
import json
import shapefile

ROOT = Path(__file__).resolve().parents[1] / "tests" / "data" / "SHP"
WKT = ('PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere",GEOGCS["GCS_WGS_1984",'
       'DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],'
       'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],'
       'PROJECTION["Mercator_Auxiliary_Sphere"],PARAMETER["False_Easting",0.0],'
       'PARAMETER["False_Northing",0.0],PARAMETER["Central_Meridian",0.0],'
       'PARAMETER["Standard_Parallel_1",0.0],PARAMETER["Auxiliary_Sphere_Type",0.0],'
       'UNIT["Meter",1.0],AUTHORITY["EPSG","3857"]]')

def ring(x, y, width=20, height=15, z=10, hole=False):
    points = [(x,y,z),(x,y+height,z),(x+width,y+height,z),(x+width,y,z),(x,y,z)]
    return list(reversed(points)) if hole else points

def write(name, kind, shapes, method, lengths=None, areas=None):
    base = ROOT / name
    ROOT.mkdir(parents=True, exist_ok=True)
    # Spatial indices from older geometry must not survive replacement.
    for suffix in (".sbn", ".sbx", ".geojson"):
        old = base.with_suffix(suffix)
        if old.exists():
            old.unlink()
    with shapefile.Writer(str(base), shapeType=kind) as writer:
        writer.field("note", "C", 80)
        writer.field("rank", "N", 8, 0)
        if lengths is not None:
            writer.field("Shape_Leng", "F", 18, 6)
        if areas is not None:
            writer.field("Shape_Area", "F", 18, 6)
        for index, shape in enumerate(shapes):
            getattr(writer, method)(*shape)
            values = ["Synthetic feature {}".format(index + 1), index + 1]
            if lengths is not None:
                values.append(lengths[index])
            if areas is not None:
                values.append(areas[index])
            writer.record(*values)
    # DBF writers normally stamp today's date. Pin the synthetic header so source snapshots
    # regenerated on different days remain byte-for-byte comparable.
    with base.with_suffix(".dbf").open("r+b") as dbf:
        dbf.seek(1)
        dbf.write(bytes((126, 1, 1)))  # 2026-01-01; fixture metadata, not a survey date.
    base.with_suffix(".prj").write_text(WKT, encoding="ascii")
    base.with_suffix(".cpg").write_text("UTF-8", encoding="ascii")
    with shapefile.Reader(str(base)) as reader:
        assert len(reader) == len(shapes)
        assert reader.shapeType == kind
    print("{}: {} synthetic features".format(name, len(shapes)))

write("Point_Multi_Mixed", shapefile.POINTZ,
      [(1000+i*30, 1000+i*15, 10+i) for i in range(6)], "pointz")
write("Polyline_MultipartMix_Streets", shapefile.POLYLINEZ,
      [([[(1000,1050,10),(1020,1050,10),(1040,1050,10)]],),
       ([[(1000,1075,11),(1040,1075,11)],[(1060,1075,11),(1080,1075,11)]],),
       ([[(1000,1100,12),(1040,1100,12)]],)], "linez", [40,60,40])
write("Polygons_Single_Buildings", shapefile.POLYGONZ,
      [([ring(1000+i*30,1150)],) for i in range(3)], "polyz", [70]*3, [300]*3)
write("Polygon_MixedMultiPart_Parcels", shapefile.POLYGONZ,
      [([ring(1000,1200),ring(1040,1200)],), ([ring(1080,1200)],)],
      "polyz", [140,70], [600,300])
write("Boundary_Multipart_Polygon", shapefile.POLYGONZ,
      [([ring(1000,1250,40,40),ring(1010,1260,10,10,hole=True),ring(1060,1250)],)],
      "polyz", [270], [1800])

vertices = [(1000,1320,0),(1020,1320,0),(1020,1340,0),(1000,1340,0),
            (1000,1320,12),(1020,1320,12),(1020,1340,12),(1000,1340,12)]
faces = [(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),
         (1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,0,4),(3,4,7)]
triangles = [[vertices[i] for i in face] for face in faces]
write("Multipatch_Single_Building", shapefile.MULTIPATCH,
      [(triangles, [shapefile.TRIANGLE_FAN]*12)], "multipatch")

fixture_names = ("Boundary_Multipart_Polygon", "Multipatch_Single_Building", "Point_Multi_Mixed",
                 "Polygon_MixedMultiPart_Parcels", "Polygons_Single_Buildings", "Polyline_MultipartMix_Streets")
fixture_paths = sorted(ROOT / (name + suffix) for name in fixture_names
                       for suffix in (".cpg", ".dbf", ".prj", ".shp", ".shx"))
manifest = {
    "schemaVersion": 1,
    "provenance": "Invented coordinates and attributes; no external GIS data is read.",
    "license": "MIT",
    "generator": "tools/generate-fixtures.py",
    "generatorSha256": hashlib.sha256(Path(__file__).read_text(encoding="utf-8").encode("utf-8")).hexdigest(),
    "pyshpVersion": shapefile.__version__,
    "dbfHeaderDate": "2026-01-01",
    "files": [{"path": "SHP/" + file.name, "sha256": hashlib.sha256(file.read_bytes()).hexdigest()}
              for file in fixture_paths]
}
(ROOT.parent / "SYNTHETIC.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
