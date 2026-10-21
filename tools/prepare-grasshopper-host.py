"""Make a disposable project from an existing acceptance project; never edits the source."""
import pathlib
import sys
import arcpy

source, output = map(pathlib.Path, sys.argv[1:3])
output.parent.mkdir(parents=True, exist_ok=True)
gdb = output.parent / "Grasshopper.gdb"
if not gdb.exists():
    arcpy.management.CreateFileGDB(str(output.parent), gdb.name)
project = arcpy.mp.ArcGISProject(str(source))
project.defaultGeodatabase = str(gdb)
for map_ in project.listMaps():
    for layer in list(map_.listLayers()):
        try:
            map_.removeLayer(layer)
        except RuntimeError:
            pass  # a group removal already removed its children
project.saveACopy(str(output))
print(output)
