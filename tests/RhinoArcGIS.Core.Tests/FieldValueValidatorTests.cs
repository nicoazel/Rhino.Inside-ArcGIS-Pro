using RhinoArcGIS.Core.Attributes;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class FieldValueValidatorTests
    {
        [Theory]
        [InlineData("12", true)]
        [InlineData("-4", true)]
        [InlineData("12.5", false)]
        [InlineData("twelve", false)]
        public void Integer_validation(string value, bool ok)
            => Assert.Equal(ok, FieldValueValidator.TryValidate(value, FieldType.Integer, null, out _));

        [Theory]
        [InlineData("3.2", true)]
        [InlineData("3", true)]
        [InlineData("high", false)]
        public void Double_validation(string value, bool ok)
            => Assert.Equal(ok, FieldValueValidator.TryValidate(value, FieldType.Double, null, out _));

        [Theory]
        [InlineData("true", true)]
        [InlineData("0", true)]
        [InlineData("maybe", false)]
        public void Boolean_validation(string value, bool ok)
            => Assert.Equal(ok, FieldValueValidator.TryValidate(value, FieldType.Boolean, null, out _));

        [Fact]
        public void Guid_validation()
        {
            Assert.True(FieldValueValidator.TryValidate("2f19c4e2-0e7c-4b4b-90fd-772b77e10a3a", FieldType.Guid, null, out _));
            Assert.False(FieldValueValidator.TryValidate("nope", FieldType.Guid, null, out _));
        }

        [Fact]
        public void Date_validation()
        {
            Assert.True(FieldValueValidator.TryValidate("2026-06-17", FieldType.Date, null, out _));
            Assert.False(FieldValueValidator.TryValidate("notadate", FieldType.Date, null, out _));
        }

        [Fact]
        public void Domain_validation_overrides_type()
        {
            var domain = new[] { "draft", "approved" };
            Assert.True(FieldValueValidator.TryValidate("approved", FieldType.Text, domain, out _));
            Assert.False(FieldValueValidator.TryValidate("rejected", FieldType.Text, domain, out _));
        }

        [Fact]
        public void Null_is_permitted()
            => Assert.True(FieldValueValidator.TryValidate(null, FieldType.Integer, null, out _));

        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("-2", false)]
        public void Positive_number_profile_rule_is_enforced(string value, bool ok)
            => Assert.Equal(ok, FieldValueValidator.TryValidate(
                value, FieldType.Double, null, new[] { "positive_number" }, out _));

        [Theory]
        [InlineData("0", true)]
        [InlineData("12.5", true)]
        [InlineData("20", true)]
        [InlineData("20.1", false)]
        public void Inclusive_range_profile_rule_is_enforced(string value, bool ok)
            => Assert.Equal(ok, FieldValueValidator.TryValidate(
                value, FieldType.Double, null, new[] { "range:[0,20]" }, out _));

        [Fact]
        public void Unknown_profile_rule_is_rejected()
        {
            Assert.False(FieldValueValidator.TryValidateRule("positve_number", out _));
            Assert.False(FieldValueValidator.TryValidate(
                "2", FieldType.Double, null, new[] { "positve_number" }, out _));
        }
    }
}
