using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using Newtonsoft.Json.Linq;

namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Test-bridge commands for scripted demonstrations and multi-view stability runs: map
    /// set-up, symbology, extra maps and scenes over the same data, and exported ArcGIS layouts
    /// as evidence. Like the rest of the bridge, inert unless RHINOINSIDE_TESTBRIDGE is set, and
    /// meant only for disposable test projects.
    /// </summary>
    internal static class TestBridgeDemo
    {
        internal static bool TryExecute(string command, JObject request, out object result)
        {
            switch (command)
            {
                case "mapsetup": result = MapSetup(request); return true;
                case "zoomtolayers": result = ZoomToLayers(request); return true;
                case "symbolize": result = Symbolize(request); return true;
                case "createview": result = CreateView(request); return true;
                case "activateview": result = ActivateView((string)request["name"]); return true;
                case "views": result = Views(); return true;
                case "exportlayout": result = ExportLayout(request); return true;
                case "exportview": result = ExportView(request); return true;
                case "projectpoints": result = ProjectPoints(request); return true;
                case "projectlayer": result = ProjectLayer(request); return true;
                case "georef": result = Georef(request); return true;
                case "featurecoords": result = FeatureCoords(request); return true;
                case "geodesy": result = Geodesy(request); return true;
                default: result = null; return false;
            }
        }

        static FeatureLayer Layer(string name) =>
            RhinoArcGIS.ArcGIS.ActiveMap.FindLayer(name)
            ?? throw new ArgumentException($"No feature layer named '{name}'.");

        static CIMColor Color(string hex, double alpha = 100)
        {
            hex = (hex ?? "#000000").TrimStart('#');
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return ColorFactory.Instance.CreateRGBColor(r, g, b, alpha);
        }

        /// <summary>Spatial reference and basemap of the active map.</summary>
        static object MapSetup(JObject request)
        {
            var wkid = request["wkid"]?.Value<int>() ?? 0;
            var basemap = (string)request["basemap"];
            return QueuedTask.Run(() =>
            {
                var map = RhinoArcGIS.ArcGIS.ActiveMap.Current ?? throw new InvalidOperationException("No map.");
                if (wkid > 0) map.SetSpatialReference(SpatialReferenceBuilder.CreateSpatialReference(wkid));
                string basemapResult = null;
                if (!string.IsNullOrWhiteSpace(basemap))
                {
                    try
                    {
                        map.SetBasemapLayers((Basemap)Enum.Parse(typeof(Basemap), basemap, true));
                        basemapResult = basemap;
                    }
                    catch (Exception ex) { basemapResult = "failed: " + ex.Message; }
                }
                return new { map = map.Name, wkid = map.SpatialReference?.Wkid, basemap = basemapResult };
            }).Result;
        }

        static object ZoomToLayers(JObject request)
        {
            var names = request["layers"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var ok = QueuedTask.Run(() =>
            {
                var view = MapView.Active;
                if (view == null) return false;
                var layers = names.Select(n => RhinoArcGIS.ArcGIS.ActiveMap.FindLayer(n)).Where(l => l != null).Cast<Layer>().ToList();
                return layers.Count > 0 && view.ZoomTo(layers, false, TimeSpan.Zero, true);
            }).Result;
            return new { zoomed = ok };
        }

        /// <summary>A simple fill/line symbol and optional Arcade label on one layer.</summary>
        static object Symbolize(JObject request)
        {
            var name = (string)request["arcgisLayer"];
            var fill = (string)request["fill"];
            var outline = (string)request["outline"] ?? "#333333";
            var width = request["width"]?.Value<double>() ?? 1.0;
            var alpha = request["alpha"]?.Value<double>() ?? 100;
            var labelField = (string)request["labelField"];
            return QueuedTask.Run(() =>
            {
                var layer = Layer(name);
                CIMSymbol symbol;
                var line = SymbolFactory.Instance.ConstructStroke(Color(outline), width, SimpleLineStyle.Solid);
                switch (layer.ShapeType)
                {
                    case esriGeometryType.esriGeometryPolyline:
                        symbol = SymbolFactory.Instance.ConstructLineSymbol(Color(outline), width, SimpleLineStyle.Solid);
                        break;
                    case esriGeometryType.esriGeometryPoint:
                    case esriGeometryType.esriGeometryMultipoint:
                        symbol = SymbolFactory.Instance.ConstructPointSymbol(Color(fill ?? outline), width * 4);
                        break;
                    default:
                        symbol = fill == null
                            ? SymbolFactory.Instance.ConstructPolygonSymbol(Color("#FFFFFF", 0), SimpleFillStyle.Null, line)
                            : SymbolFactory.Instance.ConstructPolygonSymbol(Color(fill, alpha), SimpleFillStyle.Solid, line);
                        break;
                }
                if (layer.GetRenderer() is CIMSimpleRenderer simple)
                {
                    simple.Symbol = symbol.MakeSymbolReference();
                    layer.SetRenderer(simple);
                }
                else
                {
                    layer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
                }

                if (!string.IsNullOrWhiteSpace(labelField))
                {
                    var definition = layer.GetDefinition() as CIMFeatureLayer;
                    if (definition?.LabelClasses?.Length > 0)
                    {
                        var label = definition.LabelClasses[0];
                        label.ExpressionEngine = LabelExpressionEngine.Arcade;
                        label.Expression = "$feature." + labelField;
                        definition.LabelClasses[0] = label;
                        layer.SetDefinition(definition);
                    }
                    layer.SetLabelVisibility(true);
                }
                return new { layer = layer.Name, symbolized = true, labels = labelField };
            }).Result;
        }

        /// <summary>
        /// A second map or a local scene holding the same data as named layers of the current map,
        /// opened in its own pane -- the "several views of one project" case.
        /// </summary>
        static object CreateView(JObject request)
        {
            var name = (string)request["name"];
            var scene = string.Equals((string)request["type"], "scene", StringComparison.OrdinalIgnoreCase);
            var layerNames = request["layers"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var map = QueuedTask.Run(() =>
            {
                var sources = layerNames.Select(n => Layer(n)).ToList();
                var created = MapFactory.Instance.CreateMap(name,
                    scene ? MapType.Scene : MapType.Map,
                    scene ? MapViewingMode.SceneLocal : MapViewingMode.Map, Basemap.None);
                var sr = sources.FirstOrDefault()?.GetSpatialReference();
                if (sr != null) created.SetSpatialReference(sr);
                foreach (var source in sources)
                {
                    using var fc = source.GetFeatureClass();
                    var uri = fc.GetPath();
                    var layer = LayerFactory.Instance.CreateLayer(uri, created) as FeatureLayer;
                    layer?.SetName(source.Name);
                }
                return created;
            }).Result;
            var pane = TestBridgeUi(() => FrameworkApplication.Panes.CreateMapPaneAsync(map)).GetAwaiter().GetResult();
            System.Threading.Thread.Sleep(1500);
            return new { name, type = scene ? "scene" : "map", opened = pane != null };
        }

        /// <summary>Activates the pane showing a map or scene, opening one if none is open.</summary>
        static object ActivateView(string name)
        {
            var activated = TestBridgeUi(() =>
            {
                foreach (var pane in FrameworkApplication.Panes.OfType<IMapPane>())
                {
                    if (!string.Equals(pane.MapView?.Map?.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    (pane as ArcGIS.Desktop.Framework.Contracts.Pane)?.Activate();
                    return true;
                }
                return false;
            });
            if (!activated)
            {
                var item = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No map or scene named '{name}'.");
                var map = QueuedTask.Run(() => item.GetMap()).Result;
                TestBridgeUi(() => FrameworkApplication.Panes.CreateMapPaneAsync(map)).GetAwaiter().GetResult();
            }
            for (var i = 0; i < 40; i++)
            {
                var active = TestBridgeUi(() => MapView.Active?.Map?.Name);
                if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
                {
                    System.Threading.Thread.Sleep(500);
                    return new { active, is3D = TestBridgeUi(() => MapView.Active?.ViewingMode != MapViewingMode.Map) };
                }
                System.Threading.Thread.Sleep(250);
            }
            return new { active = TestBridgeUi(() => MapView.Active?.Map?.Name), is3D = false };
        }

        static object Views() => new
        {
            active = TestBridgeUi(() => MapView.Active?.Map?.Name),
            maps = Project.Current.GetItems<MapProjectItem>().Select(i => i.Name).ToList()
        };

        /// <summary>
        /// Builds (once) and exports an ArcGIS Pro layout of the named map: map frame zoomed to
        /// the given layers, title, subtitle, notes, legend, north arrow and scale bar. Each call
        /// updates the texts and the frame extent, then writes a PDF and a PNG.
        /// </summary>
        static object ExportLayout(JObject request)
        {
            var mapName = (string)request["map"] ?? "Map";
            var pdf = (string)request["path"];
            var title = (string)request["title"] ?? string.Empty;
            var subtitle = (string)request["subtitle"] ?? string.Empty;
            var notes = (string)request["notes"] ?? string.Empty;
            var layerNames = request["layers"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var layoutName = (string)request["layout"] ?? "Rhino.Inside Central Park demo";

            return QueuedTask.Run(() =>
            {
                var mapItem = Project.Current.GetItems<MapProjectItem>().First(i => i.Name == mapName);
                var map = mapItem.GetMap();
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>().FirstOrDefault(i => i.Name == layoutName);
                Layout layout;
                MapFrame frame;
                if (layoutItem == null)
                {
                    layout = LayoutFactory.Instance.CreateLayout(17, 11, LinearUnit.Inches, false, 0.25);
                    layout.SetName(layoutName);
                    frame = ElementFactory.Instance.CreateMapFrameElement(layout,
                        EnvelopeBuilderEx.CreateEnvelope(0.4, 0.4, 12.0, 10.6), map, "Map Frame", false, null) as MapFrame;

                    var titleSymbol = SymbolFactory.Instance.ConstructTextSymbol(Color("#1B2A3A"), 18, "Segoe UI", "Semibold");
                    ElementFactory.Instance.CreateTextGraphicElement(layout, TextType.RectangleParagraph,
                        EnvelopeBuilderEx.CreateEnvelope(12.3, 10.0, 16.7, 10.7), titleSymbol, title, "Title", false, null);
                    var subSymbol = SymbolFactory.Instance.ConstructTextSymbol(Color("#3A4A5A"), 12, "Segoe UI", "Regular");
                    ElementFactory.Instance.CreateTextGraphicElement(layout, TextType.RectangleParagraph,
                        EnvelopeBuilderEx.CreateEnvelope(12.3, 8.4, 16.6, 10.0), subSymbol, subtitle, "Subtitle", false, null);
                    var noteSymbol = SymbolFactory.Instance.ConstructTextSymbol(Color("#222222"), 9, "Consolas", "Regular");
                    ElementFactory.Instance.CreateTextGraphicElement(layout, TextType.RectangleParagraph,
                        EnvelopeBuilderEx.CreateEnvelope(12.3, 3.9, 16.6, 8.2), noteSymbol, notes, "Notes", false, null);

                    ElementFactory.Instance.CreateMapSurroundElement(layout,
                        EnvelopeBuilderEx.CreateEnvelope(12.3, 0.5, 16.6, 3.7),
                        new LegendInfo { MapFrameName = frame.Name }, "Legend", false, null);
                    var style = Project.Current.GetItems<StyleProjectItem>().FirstOrDefault(s => s.Name == "ArcGIS 2D");
                    var arrow = style?.SearchNorthArrows("ArcGIS North 1").FirstOrDefault();
                    if (arrow != null)
                        ElementFactory.Instance.CreateMapSurroundElement(layout,
                            EnvelopeBuilderEx.CreateEnvelope(11.1, 9.4, 11.8, 10.4),
                            new NorthArrowInfo { MapFrameName = frame.Name, NorthArrowStyleItem = arrow }, "North Arrow", false, null);
                    var bar = style?.SearchScaleBars("Alternating Scale Bar 1").FirstOrDefault();
                    if (bar != null)
                        ElementFactory.Instance.CreateMapSurroundElement(layout,
                            EnvelopeBuilderEx.CreateEnvelope(0.6, 0.55, 4.6, 0.95),
                            new ScaleBarInfo { MapFrameName = frame.Name, ScaleBarStyleItem = bar }, "Scale Bar", false, null);
                }
                else
                {
                    layout = layoutItem.GetLayout();
                    frame = layout.FindElement("Map Frame") as MapFrame;
                    if (frame != null && frame.Map?.URI != map.URI) frame.SetMap(map);
                    SetText(layout, "Title", title);
                    SetText(layout, "Subtitle", subtitle);
                    SetText(layout, "Notes", notes);
                }

                // Frame the named layers, with a margin so the park around them shows.
                Envelope extent = null;
                foreach (var name in layerNames)
                {
                    var layer = map.GetLayersAsFlattenedList().OfType<FeatureLayer>().FirstOrDefault(l => l.Name == name);
                    var e = layer?.QueryExtent();
                    if (e == null || e.IsEmpty) continue;
                    extent = extent == null ? e : extent.Union(e);
                }
                if (frame != null && extent != null) frame.SetCamera(extent.Expand(1.35, 1.35, true));

                map.SetSelection(null);   // edits leave their features selected; keep the evidence clean
                Directory.CreateDirectory(Path.GetDirectoryName(pdf));
                layout.Export(new PDFFormat { OutputFileName = pdf, Resolution = 200 });
                var png = Path.ChangeExtension(pdf, ".png");
                layout.Export(new PNGFormat { OutputFileName = png, Resolution = 110 });
                return new { pdf, png, layout = layoutName };
            }).Result;
        }

        /// <summary>
        /// Exports the active map or scene view to a PNG, framed on the given layers; in a scene
        /// the camera is tilted for an oblique view of the masses.
        /// </summary>
        static object ExportView(JObject request)
        {
            var path = (string)request["path"];
            var width = request["width"]?.Value<int>() ?? 1600;
            var height = request["height"]?.Value<int>() ?? 1000;
            var names = request["layers"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var pitch = request["pitch"]?.Value<double>();
            var heading = request["heading"]?.Value<double>();
            var exported = QueuedTask.Run(() =>
            {
                var view = MapView.Active;
                if (view == null) return false;
                var layers = names.Select(n => view.Map.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == n)).Where(l => l != null).Cast<Layer>().ToList();
                view.Map.SetSelection(null);
                if (layers.Count > 0) view.ZoomTo(layers, false, TimeSpan.Zero, true);
                if ((pitch.HasValue || heading.HasValue) && layers.Count > 0)
                {
                    // An oblique view of the layers: stand back from their centre along the reverse of
                    // the heading, high enough to look down at the pitch, far enough to see it all.
                    Envelope extent = null;
                    foreach (var layer in layers.OfType<FeatureLayer>())
                    {
                        var e = layer.QueryExtent();
                        if (e != null && !e.IsEmpty) extent = extent == null ? e : extent.Union(e);
                    }
                    if (extent != null)
                    {
                        double headingDeg = heading ?? 0, pitchDeg = pitch ?? -45;
                        double reach = Math.Max(extent.Width, extent.Height) * 1.1;
                        double down = Math.Abs(pitchDeg) * Math.PI / 180, back = (headingDeg + 180) * Math.PI / 180;
                        var camera = view.Camera;
                        camera.X = extent.Center.X + reach * Math.Cos(down) * Math.Sin(back);
                        camera.Y = extent.Center.Y + reach * Math.Cos(down) * Math.Cos(back);
                        camera.Z = reach * Math.Sin(down);
                        camera.Heading = headingDeg;
                        camera.Pitch = pitchDeg;
                        view.ZoomTo(camera, TimeSpan.Zero);
                    }
                }
                System.Threading.Thread.Sleep(2500); // let tiles and 3D content draw
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                view.Export(new PNGFormat { OutputFileName = path, Width = width, Height = height, Resolution = 96 });
                return true;
            }).Result;
            return new { path, exported };
        }

        /// <summary>
        /// Projects [x, y] points between two spatial references (by WKID), through the datum
        /// transformation ArcGIS selects for them (<c>datum = false</c> uses plain Project, which
        /// in Pro applies its own default transformation).
        /// </summary>
        static object ProjectPoints(JObject request)
        {
            var from = request["from"].Value<int>();
            var to = request["to"].Value<int>();
            var withDatum = request["datum"]?.Value<bool>() ?? true;
            var points = request["points"].Select(p => new[] { p[0].Value<double>(), p[1].Value<double>() }).ToList();
            return QueuedTask.Run(() =>
            {
                var source = SpatialReferenceBuilder.CreateSpatialReference(from);
                var target = SpatialReferenceBuilder.CreateSpatialReference(to);
                var first = points.Count == 0 ? null
                    : GeometryEngine.Instance.Project(MapPointBuilderEx.CreateMapPoint(points[0][0], points[0][1], source), SpatialReferences.WGS84) as MapPoint;
                var datum = RhinoArcGIS.ArcGIS.DatumTransforms.Between(source, target, first?.X ?? double.NaN, first?.Y ?? double.NaN);
                return new
                {
                    transformation = withDatum ? datum.Description : "ArcGIS default (plain Project)",
                    points = points.Select(p =>
                    {
                        var point = MapPointBuilderEx.CreateMapPoint(p[0], p[1], source);
                        var projected = (MapPoint)(withDatum ? datum.Project(point, target) : GeometryEngine.Instance.Project(point, target));
                        return new[] { projected.X, projected.Y };
                    }).ToList()
                };
            }).Result;
        }

        /// <summary>
        /// Copies a layer's feature class into the default geodatabase in another coordinate system
        /// (Project tool) and adds it to the active map under the given name.
        /// </summary>
        static object ProjectLayer(JObject request)
        {
            var source = Layer((string)request["arcgisLayer"]);
            var wkid = request["wkid"].Value<int>();
            var name = (string)request["name"];
            var gdb = Project.Current.DefaultGeodatabasePath;
            var input = QueuedTask.Run(() => { using var fc = source.GetFeatureClass(); return fc.GetPath().LocalPath; }).Result;
            var output = Path.Combine(gdb, name);
            var sr = QueuedTask.Run(() => SpatialReferenceBuilder.CreateSpatialReference(wkid)).Result;
            // The Project tool applies a datum transformation only when told which; give it the one
            // ArcGIS selects for this data's area, so the copy really is on the target datum.
            var transform = QueuedTask.Run(() =>
            {
                var centre = GeometryEngine.Instance.Project(source.QueryExtent().Center, SpatialReferences.WGS84) as MapPoint;
                var choice = RhinoArcGIS.ArcGIS.DatumTransforms.Between(source.GetSpatialReference(), sr, centre.X, centre.Y);
                return choice.Transformation == null ? null : choice.Description.Replace(" (reversed)", string.Empty).Replace(" + ", ";");
            }).Result;
            var gp = ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.ExecuteToolAsync("management.Project",
                ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing.MakeValueArray(input, output, sr, transform),
                null, null, null, ArcGIS.Desktop.Core.Geoprocessing.GPExecuteToolFlags.None).GetAwaiter().GetResult();
            if (gp.IsFailed)
                throw new InvalidOperationException("Project failed: " + string.Join("; ", gp.Messages.Select(m => m.Text)));
            var added = QueuedTask.Run(() =>
            {
                var map = RhinoArcGIS.ArcGIS.ActiveMap.Current;
                var layer = LayerFactory.Instance.CreateLayer(new Uri(output), map) as FeatureLayer;
                layer?.SetName(name);
                layer?.SetVisibility(false);
                return new { name = layer?.Name, wkid = layer?.GetSpatialReference()?.Wkid };
            }).Result;
            return new { added.name, added.wkid, transformation = transform, messages = gp.Messages.Select(m => m.Text).ToList() };
        }

        /// <summary>Vertices (x, y, z in the layer's CRS) of the features whose field equals a value.</summary>
        static object FeatureCoords(JObject request)
        {
            var layerName = (string)request["arcgisLayer"];
            var field = (string)request["field"];
            var value = (string)request["value"];
            return QueuedTask.Run(() =>
            {
                var layer = Layer(layerName);
                var filter = new ArcGIS.Core.Data.QueryFilter { WhereClause = $"{field} = '{value.Replace("'", "''")}'" };
                var features = new List<object>();
                using (var cursor = layer.Search(filter))
                    while (cursor.MoveNext())
                        using (var feature = (ArcGIS.Core.Data.Feature)cursor.Current)
                        {
                            var shape = feature.GetShape();
                            IEnumerable<MapPoint> points = shape switch
                            {
                                MapPoint p => new[] { p },
                                Multipart m => m.Points,
                                Multipatch mp => mp.Points,
                                _ => Enumerable.Empty<MapPoint>()
                            };
                            features.Add(new
                            {
                                objectId = feature.GetObjectID(),
                                points = points.Select(p => new[] { p.X, p.Y, p.HasZ ? p.Z : 0.0 }).ToList()
                            });
                        }
                return new { wkid = layer.GetSpatialReference()?.Wkid, features };
            }).Result;
        }

        /// <summary>
        /// ArcGIS's own geodesy on WGS84, for checking placement: "move" walks a start point by
        /// (distance m, azimuth degrees) pairs along geodesics; "distance" measures point pairs.
        /// </summary>
        static object Geodesy(JObject request)
        {
            var op = (string)request["op"];
            return QueuedTask.Run(() =>
            {
                var wgs84 = SpatialReferences.WGS84;
                MapPoint P(JToken t) => MapPointBuilderEx.CreateMapPoint(t[0].Value<double>(), t[1].Value<double>(), wgs84);
                if (op == "move")
                {
                    var start = P(request["from"]);
                    return (object)new
                    {
                        points = request["moves"].Select(m =>
                        {
                            var moved = GeometryEngine.Instance.GeodeticMove(new[] { start }, wgs84, m[0].Value<double>(),
                                LinearUnit.Meters, m[1].Value<double>() * Math.PI / 180.0, GeodeticCurveType.Geodesic)[0];
                            return new[] { moved.X, moved.Y };
                        }).ToList()
                    };
                }
                if (op == "inverse")
                {
                    // Raw GeodeticDistanceAndAzimuth output, to pin down its units against known bearings.
                    return (object)new
                    {
                        results = request["pairs"].Select(pair =>
                        {
                            double d = GeometryEngine.Instance.GeodeticDistanceAndAzimuth(P(pair[0]), P(pair[1]),
                                GeodeticCurveType.Geodesic, LinearUnit.Meters, out double az12, out double az21);
                            return new[] { d, az12, az21 };
                        }).ToList()
                    };
                }
                return new
                {
                    metres = request["pairs"].Select(pair =>
                        GeometryEngine.Instance.GeodesicDistance(P(pair[0]), P(pair[1]), LinearUnit.Meters)).ToList()
                };
            }).Result;
        }

        /// <summary>The document's georeference mode and anchor, and for a layer the datum transformation its frame uses.</summary>
        static object Georef(JObject request)
        {
            var info = TestBridgeUi(() =>
            {
                var anchor = RhinoHost.GetEarthAnchor();
                var mode = RhinoHost.GetDocumentStrings(RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey);
                mode.TryGetValue(RhinoArcGIS.Core.Spatial.GeoReferenceFactory.ModeKey, out var m);
                return new { mode = m, anchor };
            });
            string datum = null;
            var layer = (string)request["arcgisLayer"];
            if (!string.IsNullOrWhiteSpace(layer) && info.anchor?.IsSet == true)
                datum = new RhinoArcGIS.ArcGIS.ArcGISAdapter { CrsLayer = layer }
                    .GetLocalFrame(info.anchor.Latitude, info.anchor.Longitude, 0)?.DatumTransformation + " (per-vertex)";
            return new { info.mode, info.anchor?.IsSet, info.anchor?.Latitude, info.anchor?.Longitude, datum };
        }

        static void SetText(Layout layout, string name, string text)
        {
            if (layout.FindElement(name) is TextElement element)
            {
                var props = element.TextProperties;
                props.Text = text;
                element.SetTextProperties(props);
            }
        }

        static T TestBridgeUi<T>(Func<T> func)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            return dispatcher == null || dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
        }
    }
}
