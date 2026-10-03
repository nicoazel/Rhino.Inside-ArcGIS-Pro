using System.Collections.Generic;
using RhinoArcGIS.Core.Identity;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public sealed class LayerSourceBindingTests
    {
        [Fact]
        public void Saved_source_survives_reused_name_and_follows_rename()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", @"D:\new.gdb\Parcels"),
                new KeyValuePair<string, string>("Parcels (2)", @"D:\old.gdb\Parcels")
            };

            var result = LayerSourceBinding.Resolve("Parcels", @"D:\old.gdb\Parcels", layers);

            Assert.Equal(LayerSourceBinding.Status.Resolved, result.Status);
            Assert.Equal("Parcels (2)", result.Name);
            Assert.Equal(@"D:\old.gdb\Parcels", result.Source);
        }

        [Fact]
        public void Changed_source_with_same_name_is_missing_instead_of_redirected()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", @"D:\new.gdb\Parcels")
            };

            var result = LayerSourceBinding.Resolve("Parcels", @"D:\old.gdb\Parcels", layers);

            Assert.Equal(LayerSourceBinding.Status.Missing, result.Status);
        }

        [Fact]
        public void Repeated_map_entries_for_same_source_are_ambiguous_layer_targets()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", @"D:\old.gdb\Parcels"),
                new KeyValuePair<string, string>("Parcels (2)", @"D:\old.gdb\Parcels")
            };

            var result = LayerSourceBinding.Resolve("Parcels", @"D:\old.gdb\Parcels", layers);

            Assert.Equal(LayerSourceBinding.Status.Ambiguous, result.Status);
        }

        [Fact]
        public void Legacy_name_only_link_rejects_multiple_sources()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", @"D:\one.gdb\Parcels"),
                new KeyValuePair<string, string>("Parcels", @"D:\two.gdb\Parcels")
            };

            var result = LayerSourceBinding.Resolve("Parcels", null, layers);

            Assert.Equal(LayerSourceBinding.Status.Ambiguous, result.Status);
        }

        [Fact]
        public void Legacy_name_only_link_rejects_multiple_layer_objects_even_for_same_source()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", @"D:\old.gdb\Parcels"),
                new KeyValuePair<string, string>("Parcels", @"D:\old.gdb\Parcels")
            };

            var result = LayerSourceBinding.Resolve("Parcels", null, layers);

            Assert.Equal(LayerSourceBinding.Status.Ambiguous, result.Status);
        }

        [Fact]
        public void A_layer_with_unreadable_source_cannot_initialize_a_saved_binding()
        {
            var layers = new[]
            {
                new KeyValuePair<string, string>("Parcels", null)
            };

            var result = LayerSourceBinding.Resolve("Parcels", null, layers);

            Assert.Equal(LayerSourceBinding.Status.Missing, result.Status);
        }
    }
}
