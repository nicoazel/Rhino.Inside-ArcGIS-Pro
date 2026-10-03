using RhinoArcGIS.Core.Change;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class ChangeDetectorTests
    {
        [Fact]
        public void Both_missing_is_unchanged()
            => Assert.Equal(FieldChangeState.Unchanged, ChangeDetector.Detect(null, null, null));

        [Fact]
        public void Missing_in_rhino_detected()
            => Assert.Equal(FieldChangeState.MissingInRhino, ChangeDetector.Detect(null, "x", null));

        [Fact]
        public void Missing_in_arcgis_detected()
            => Assert.Equal(FieldChangeState.MissingInArcGis, ChangeDetector.Detect("x", null, null));

        [Fact]
        public void Never_synced_equal_values_unchanged()
            => Assert.Equal(FieldChangeState.Unchanged, ChangeDetector.Detect("same", "same", null));

        [Fact]
        public void Never_synced_differing_values_changed_in_both()
            => Assert.Equal(FieldChangeState.ChangedInBoth, ChangeDetector.Detect("a", "b", null));

        [Fact]
        public void Changed_in_rhino_only()
        {
            string baseHash = Hashing.HashField("orig");
            Assert.Equal(FieldChangeState.ChangedInRhino, ChangeDetector.Detect("new", "orig", baseHash));
        }

        [Fact]
        public void Changed_in_arcgis_only()
        {
            string baseHash = Hashing.HashField("orig");
            Assert.Equal(FieldChangeState.ChangedInArcGis, ChangeDetector.Detect("orig", "new", baseHash));
        }

        [Fact]
        public void Changed_in_both_when_each_differs_from_base()
        {
            string baseHash = Hashing.HashField("orig");
            Assert.Equal(FieldChangeState.ChangedInBoth, ChangeDetector.Detect("r", "a", baseHash));
        }

        [Fact]
        public void Unchanged_when_both_match_base()
        {
            string baseHash = Hashing.HashField("orig");
            Assert.Equal(FieldChangeState.Unchanged, ChangeDetector.Detect("orig", "orig", baseHash));
        }
    }
}
