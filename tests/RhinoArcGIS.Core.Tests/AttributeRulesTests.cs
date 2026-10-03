using RhinoArcGIS.Core.Attributes;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class AttributeRulesTests
    {
        // ---- Push (Rhino -> ArcGIS), spec 04 §6 ----

        [Fact]
        public void Push_rhino_owned_writes()
            => Assert.Equal(SyncAction.Write, AttributeRules.Push(FieldOwnership.RhinoOwned, true, true));

        [Fact]
        public void Push_arcgis_owned_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Push(FieldOwnership.ArcGisOwned, true, false));

        [Fact]
        public void Push_shared_unchanged_in_arcgis_writes()
            => Assert.Equal(SyncAction.Write, AttributeRules.Push(FieldOwnership.Shared, true, false));

        [Fact]
        public void Push_shared_changed_in_both_flags_conflict()
            => Assert.Equal(SyncAction.FlagConflict, AttributeRules.Push(FieldOwnership.Shared, true, true));

        [Fact]
        public void Push_shared_changed_only_in_arcgis_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Push(FieldOwnership.Shared, false, true));

        [Fact]
        public void Push_derived_recalculates()
            => Assert.Equal(SyncAction.Recalculate, AttributeRules.Push(FieldOwnership.Derived, false, false));

        [Fact]
        public void Push_locked_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Push(FieldOwnership.Locked, true, true));

        [Fact]
        public void Pull_locked_writes_the_arcgis_value()
            => Assert.Equal(SyncAction.Write, AttributeRules.Pull(FieldOwnership.Locked, true, true));

        [Theory]
        [InlineData(FieldOwnership.LocalOnlyRhino)]
        [InlineData(FieldOwnership.LocalOnlyArcGis)]
        public void Push_local_only_skips(FieldOwnership owner)
            => Assert.Equal(SyncAction.Skip, AttributeRules.Push(owner, true, true));

        // ---- Pull (ArcGIS -> Rhino), spec 04 §7 ----

        [Fact]
        public void Pull_arcgis_owned_writes()
            => Assert.Equal(SyncAction.Write, AttributeRules.Pull(FieldOwnership.ArcGisOwned, false, true));

        [Fact]
        public void Pull_rhino_owned_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Pull(FieldOwnership.RhinoOwned, false, true));

        [Fact]
        public void Pull_shared_unchanged_in_rhino_writes()
            => Assert.Equal(SyncAction.Write, AttributeRules.Pull(FieldOwnership.Shared, false, true));

        [Fact]
        public void Pull_shared_changed_in_both_flags_conflict()
            => Assert.Equal(SyncAction.FlagConflict, AttributeRules.Pull(FieldOwnership.Shared, true, true));

        [Fact]
        public void Pull_shared_changed_only_in_rhino_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Pull(FieldOwnership.Shared, true, false));

        [Fact]
        public void Pull_local_only_arcgis_skips()
            => Assert.Equal(SyncAction.Skip, AttributeRules.Pull(FieldOwnership.LocalOnlyArcGis, false, true));

        [Fact]
        public void Pull_local_only_rhino_preserves()
            => Assert.Equal(SyncAction.Preserve, AttributeRules.Pull(FieldOwnership.LocalOnlyRhino, false, true));
    }
}
