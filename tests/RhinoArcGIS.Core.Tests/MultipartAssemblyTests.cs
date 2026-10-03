using System;
using System.Collections.Generic;
using System.Linq;
using RhinoArcGIS.Core.Adapters;
using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Identity;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class MultipartAssemblyTests
    {
        static RhinoObjectSnapshot Line(double y, string syncGuid = null, string partOf = null, int index = 0)
        {
            var us = new Dictionary<string, string>(StringComparer.Ordinal);
            if (syncGuid != null) us[GisKeys.SyncGuid] = syncGuid;
            if (partOf != null) { us[GisKeys.PartOf] = partOf; us[GisKeys.PartIndex] = index.ToString(); }
            return new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Geometry = new NeutralGeometry { Kind = NeutralGeometryKind.Polyline, Points = new List<Xyz> { new Xyz(0, y, 0), new Xyz(10, y, 0) } },
                UserStrings = us
            };
        }

        [Fact]
        public void Pieces_join_their_representative_in_part_order_whatever_order_they_are_read_in()
        {
            var rep = Line(0, syncGuid: "A");
            var assembled = MultipartAssembly.Assemble(new[] { Line(2, partOf: "a", index: 2), rep, Line(1, partOf: "A", index: 1) });

            var only = Assert.Single(assembled);
            Assert.Same(rep, only);
            Assert.Equal(new[] { 0.0, 1.0, 2.0 }, only.Geometry.Parts.Select(p => p[0].Y));
        }

        [Fact]
        public void A_piece_without_its_representative_is_offered_on_its_own_without_markers()
        {
            var assembled = MultipartAssembly.Assemble(new[] { Line(0, syncGuid: "A"), Line(1, partOf: "B", index: 1) });

            Assert.Equal(2, assembled.Count);
            Assert.False(assembled[1].UserStrings.ContainsKey(GisKeys.PartOf));
            Assert.False(assembled[1].UserStrings.ContainsKey(GisKeys.PartIndex));
        }

        [Fact]
        public void A_piece_of_another_kind_is_not_merged()
        {
            var point = new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Geometry = NeutralGeometry.Point(new Xyz(1, 1, 0)),
                UserStrings = new Dictionary<string, string>(StringComparer.Ordinal) { { GisKeys.PartOf, "A" }, { GisKeys.PartIndex, "1" } }
            };
            var assembled = MultipartAssembly.Assemble(new[] { Line(0, syncGuid: "A"), point });

            Assert.Equal(2, assembled.Count);
            Assert.Equal(2, assembled[0].Geometry.Points.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Copied_multipoint_pieces_do_not_change_the_original_feature(bool copyFirst)
        {
            RhinoObjectSnapshot Point(double x, string partOf = null) => new RhinoObjectSnapshot
            {
                RhinoGuid = Guid.NewGuid(),
                Geometry = NeutralGeometry.Point(new Xyz(x, 0, 0)),
                UserStrings = partOf == null
                    ? new Dictionary<string, string> { { GisKeys.SyncGuid, "A" } }
                    : new Dictionary<string, string> { { GisKeys.PartOf, partOf }, { GisKeys.PartIndex, "1" } }
            };
            var original = Point(0);
            var piece = Point(1, "A");
            original.UserStrings[GisKeys.RhinoObjectId] = original.RhinoGuid.ToString();
            piece.UserStrings[GisKeys.RhinoObjectId] = piece.RhinoGuid.ToString();
            var copy = Point(10);
            var pieceCopy = Point(11);
            copy.UserStrings = new Dictionary<string, string>(original.UserStrings);
            pieceCopy.UserStrings = new Dictionary<string, string>(piece.UserStrings);

            var assembled = MultipartAssembly.Assemble(copyFirst
                ? new[] { copy, pieceCopy, original, piece }
                : new[] { original, piece, copy, pieceCopy });

            Assert.Equal(3, assembled.Count);
            Assert.Equal(new[] { 0.0, 1.0 }, original.Geometry.Points.Select(p => p.X));
            Assert.Single(copy.Geometry.Points);
            Assert.Single(pieceCopy.Geometry.Points);
            Assert.DoesNotContain(GisKeys.PartOf, pieceCopy.UserStrings.Keys);
            Assert.True(SyncIdentity.IsCopy(copy.RhinoGuid, copy.UserStrings));
            Assert.True(SyncIdentity.IsCopy(pieceCopy.RhinoGuid, pieceCopy.UserStrings));
        }
    }
}
