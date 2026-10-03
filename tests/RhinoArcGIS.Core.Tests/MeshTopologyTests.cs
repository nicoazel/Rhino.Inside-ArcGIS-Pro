using RhinoArcGIS.Core.Geometry;
using RhinoArcGIS.Core.Spatial;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class MeshTopologyTests
    {
        [Fact]
        public void Coincident_patch_coordinates_are_welded_to_shared_vertices()
        {
            var source = new NeutralMesh
            {
                Vertices =
                {
                    new Xyz(0, 0, 0), new Xyz(1, 0, 0), new Xyz(1, 1, 0),
                    new Xyz(0, 0, 0), new Xyz(1, 1, 0), new Xyz(0, 1, 0)
                },
                Faces = { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }
            };

            NeutralMesh clean = MeshTopology.Clean(source);

            Assert.Equal(4, clean.Vertices.Count);
            Assert.Equal(2, clean.Faces.Count);
            Assert.Equal(clean.Faces[0][0], clean.Faces[1][0]);
            Assert.Equal(clean.Faces[0][2], clean.Faces[1][1]);
        }

        [Fact]
        public void Near_duplicates_are_welded_and_degenerate_faces_are_removed()
        {
            var source = new NeutralMesh
            {
                Vertices =
                {
                    new Xyz(0, 0, 0), new Xyz(0.000000001, 0, 0),
                    new Xyz(1, 0, 0), new Xyz(0, 1, 2), new Xyz(99, 99, 99)
                },
                Faces = { new[] { 0, 1, 2 }, new[] { 1, 2, 3 }, new[] { 0, 99, 3 } }
            };

            NeutralMesh clean = MeshTopology.Clean(source);

            Assert.Single(clean.Faces);
            Assert.Equal(3, clean.Vertices.Count);
            Assert.Equal(2, clean.Vertices[2].Z);
        }
    }
}
