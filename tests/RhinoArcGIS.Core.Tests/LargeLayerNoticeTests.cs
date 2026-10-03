using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class LargeLayerNoticeTests
    {
        [Fact]
        public void Small_layers_get_no_notice()
        {
            Assert.Null(LargeLayerNotice.ForPull(LargeLayerNotice.Threshold - 1));
            Assert.Null(LargeLayerNotice.ForSync(5000, apply: true));
        }

        [Fact]
        public void A_large_pull_says_how_many_and_roughly_how_long()
        {
            var notice = LargeLayerNotice.ForPull(100000);
            Assert.Contains("100,000 features", notice);
            Assert.Contains("about 2 min", notice);
        }

        [Fact]
        public void A_large_apply_warns_that_many_changes_take_longer()
        {
            var notice = LargeLayerNotice.ForSync(100000, apply: true);
            Assert.Contains("Applying takes about 20 s", notice);
            Assert.Contains("longer if many objects changed", notice);
        }

        [Theory]
        [InlineData(3, "about 5 s")]
        [InlineData(59, "about 60 s")]
        [InlineData(61, "about 1.5 min")]
        [InlineData(120, "about 2 min")]
        public void Durations_round_up(double seconds, string expected) =>
            Assert.Equal(expected, LargeLayerNotice.About(seconds));
    }
}
