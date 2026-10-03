using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Canonical fingerprint of the complete neutral inputs inspected by a sync review. Values are
    /// written in a fixed order with explicit lengths/counts, then streamed into SHA-256.
    /// </summary>
    public static class SyncReviewStamp
    {
        public static string Compute(uint documentSerial, string georeferenceMode, EarthAnchor anchor,
            string rhinoLedger, LayerSchema arcGisSchema,
            IEnumerable<RhinoObjectSnapshot> rhinoObjects, IEnumerable<FeatureRecord> arcGisFeatures)
        {
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(crypto, Encoding.UTF8, true))
            {
                writer.Write(1); // format version
                writer.Write(documentSerial);
                WriteString(writer, georeferenceMode);
                WriteAnchor(writer, anchor);
                WriteString(writer, rhinoLedger);
                WriteSchema(writer, arcGisSchema);

                var objects = (rhinoObjects ?? Enumerable.Empty<RhinoObjectSnapshot>())
                    .OrderBy(item => item?.RhinoGuid ?? Guid.Empty)
                    .ThenBy(item => item?.LayerName, StringComparer.Ordinal)
                    .ToList();
                writer.Write(objects.Count);
                foreach (var item in objects) WriteRhinoObject(writer, item);

                var features = (arcGisFeatures ?? Enumerable.Empty<FeatureRecord>())
                    .OrderBy(item => item?.Identity?.ArcGisObjectId ?? long.MinValue)
                    .ThenBy(item => item?.Identity?.ArcGisGlobalId?.ToString("D"), StringComparer.Ordinal)
                    .ToList();
                writer.Write(features.Count);
                foreach (var item in features) WriteFeature(writer, item);

                writer.Flush();
                crypto.FlushFinalBlock();
                return ToHex(sha.Hash);
            }
        }

        static void WriteAnchor(BinaryWriter writer, EarthAnchor anchor)
        {
            writer.Write(anchor != null);
            if (anchor == null) return;
            writer.Write(anchor.IsSet);
            writer.Write(anchor.Latitude);
            writer.Write(anchor.Longitude);
            writer.Write(anchor.Elevation);
            WriteXyz(writer, anchor.ModelBasePoint);
            writer.Write((int)anchor.ModelUnits);
            writer.Write(anchor.MetresPerModelUnit);
            writer.Write(anchor.NorthAngleDegrees);
        }

        static void WriteRhinoObject(BinaryWriter writer, RhinoObjectSnapshot item)
        {
            writer.Write(item != null);
            if (item == null) return;
            writer.Write(item.RhinoGuid.ToByteArray());
            WriteString(writer, item.LayerName);
            writer.Write(item.Descriptor != null);
            if (item.Descriptor != null)
            {
                writer.Write((int)item.Descriptor.Kind);
                writer.Write(item.Descriptor.IsEmpty);
                writer.Write(item.Descriptor.IsClosed);
                writer.Write(item.Descriptor.IsPlanar);
                writer.Write(item.Descriptor.IsSolid);
            }
            WriteGeometry(writer, item.Geometry);
            WriteDictionary(writer, item.UserStrings);
        }

        static void WriteSchema(BinaryWriter writer, LayerSchema schema)
        {
            writer.Write(schema != null);
            if (schema == null) return;
            WriteString(writer, schema.LayerName);
            WriteString(writer, schema.Source);
            WriteString(writer, schema.Crs);
            writer.Write((int)schema.GeometryType);
            writer.Write(schema.ZEnabled);
            writer.Write(schema.HasGlobalIds);
            writer.Write(schema.Editable);
            var fields = (schema.Fields ?? new List<FieldDefinition>())
                .OrderBy(field => field?.Name, StringComparer.Ordinal).ToList();
            writer.Write(fields.Count);
            foreach (var field in fields)
            {
                writer.Write(field != null);
                if (field == null) continue;
                WriteString(writer, field.Name);
                writer.Write((int)field.Type);
                writer.Write(field.Length);
                writer.Write(field.Nullable);
                writer.Write(field.Required);
                writer.Write(field.Editable);
                var domain = (field.Domain ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal).ToList();
                writer.Write(domain.Count);
                foreach (var value in domain) WriteString(writer, value);
            }
        }

        static void WriteFeature(BinaryWriter writer, FeatureRecord item)
        {
            writer.Write(item != null);
            if (item == null) return;
            writer.Write((int)item.Target);
            var identity = item.Identity;
            writer.Write(identity != null);
            if (identity != null)
            {
                WriteNullableGuid(writer, identity.ArcGisGlobalId);
                WriteNullableInt64(writer, identity.ArcGisObjectId);
                writer.Write(identity.SyncGuid.ToByteArray());
                WriteNullableGuid(writer, identity.RhinoGuid);
                WriteString(writer, identity.SourceFile);
                WriteString(writer, identity.SourceLayer);
                WriteString(writer, identity.TargetLayer);
            }
            WriteGeometry(writer, item.Geometry);
            WriteDictionary(writer, item.Attributes);
        }

        static void WriteGeometry(BinaryWriter writer, NeutralGeometry geometry)
        {
            writer.Write(geometry != null);
            if (geometry == null) return;
            writer.Write((int)geometry.Kind);
            WritePointList(writer, geometry.Points);
            WriteRunList(writer, geometry.Parts);
            WriteRunList(writer, geometry.Rings);
            writer.Write(geometry.Mesh != null);
            if (geometry.Mesh != null)
            {
                writer.Write(geometry.Mesh.IsClosed);
                WritePointList(writer, geometry.Mesh.Vertices);
                var faces = geometry.Mesh.Faces ?? new List<int[]>();
                writer.Write(faces.Count);
                foreach (var face in faces)
                {
                    writer.Write(face != null);
                    if (face == null) continue;
                    writer.Write(face.Length);
                    foreach (var index in face) writer.Write(index);
                }
            }
            WriteNullableDouble(writer, geometry.Height);
            WriteNullableDouble(writer, geometry.BaseElevation);
        }

        static void WriteRunList(BinaryWriter writer, List<List<Xyz>> runs)
        {
            runs = runs ?? new List<List<Xyz>>();
            writer.Write(runs.Count);
            foreach (var run in runs) WritePointList(writer, run);
        }

        static void WritePointList(BinaryWriter writer, List<Xyz> points)
        {
            points = points ?? new List<Xyz>();
            writer.Write(points.Count);
            foreach (var point in points) WriteXyz(writer, point);
        }

        static void WriteXyz(BinaryWriter writer, Xyz point)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
        }

        static void WriteDictionary(BinaryWriter writer, IReadOnlyDictionary<string, string> values)
        {
            var ordered = (values ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
            writer.Write(ordered.Count);
            foreach (var pair in ordered)
            {
                WriteString(writer, pair.Key);
                WriteString(writer, pair.Value);
            }
        }

        static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null) writer.Write(value);
        }

        static void WriteNullableGuid(BinaryWriter writer, Guid? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue) writer.Write(value.Value.ToByteArray());
        }

        static void WriteNullableInt64(BinaryWriter writer, long? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue) writer.Write(value.Value);
        }

        static void WriteNullableDouble(BinaryWriter writer, double? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue) writer.Write(value.Value);
        }

        static string ToHex(byte[] bytes) =>
            BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}
