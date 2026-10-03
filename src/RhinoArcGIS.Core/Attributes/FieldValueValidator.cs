using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoArcGIS.Core.Attributes
{
    /// <summary>
    /// Validates a Rhino user-string value against a target ArcGIS field type and optional coded
    /// value domain (spec 04 §8–§9). Pure and unit-tested; used before writing on push.
    /// </summary>
    public static class FieldValueValidator
    {
        public static bool TryValidate(string value, FieldType type, IReadOnlyList<string> domain, out string error)
            => TryValidate(value, type, domain, validators: null, out error);

        /// <summary>
        /// Validates the target type/domain plus profile-authored named rules. Supported rules are
        /// <c>non_empty</c>, <c>positive_number</c>, <c>non_negative_number</c>, and an inclusive
        /// <c>range:[minimum,maximum]</c>.
        /// </summary>
        public static bool TryValidate(string value, FieldType type, IReadOnlyList<string> domain,
                                       IReadOnlyList<string> validators, out string error)
        {
            error = null;

            // Domain takes precedence over the type parser: a coded value may legitimately be
            // represented as a string even when its ArcGIS storage type is numeric.
            if (domain != null && domain.Count > 0)
            {
                if (value == null || !domain.Contains(value))
                {
                    error = $"'{value}' is not in the allowed domain.";
                    return false;
                }
            }
            else if (value != null)
            {
                if (!TryValidateType(value, type, out error)) return false;
            }

            foreach (var validator in validators ?? Array.Empty<string>())
                if (!TryApplyRule(value, validator, out error)) return false;

            return true; // nullability/requiredness is enforced by the live ArcGIS schema.
        }

        /// <summary>Validates a rule's name and arguments without requiring a field value.</summary>
        public static bool TryValidateRule(string validator, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(validator)) return true;
            var rule = validator.Trim();
            if (rule.Equals("non_empty", StringComparison.OrdinalIgnoreCase) ||
                rule.Equals("positive_number", StringComparison.OrdinalIgnoreCase) ||
                rule.Equals("non_negative_number", StringComparison.OrdinalIgnoreCase))
                return true;
            if (TryParseRange(rule, out _, out _)) return true;
            error = $"Unknown or malformed validator '{validator}'.";
            return false;
        }

        static bool TryValidateType(string value, FieldType type, out string error)
        {
            error = null;
            switch (type)
            {
                case FieldType.Text: return true;
                case FieldType.Integer:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
                    error = $"'{value}' is not a valid integer.";
                    return false;
                case FieldType.Double:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
                    error = $"'{value}' is not a valid number.";
                    return false;
                case FieldType.Boolean:
                    if (IsBoolean(value)) return true;
                    error = $"'{value}' is not a valid boolean.";
                    return false;
                case FieldType.Date:
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)) return true;
                    error = $"'{value}' is not a valid date.";
                    return false;
                case FieldType.Guid:
                    if (Guid.TryParse(value, out _)) return true;
                    error = $"'{value}' is not a valid GUID.";
                    return false;
                default: return true;
            }
        }

        static bool TryApplyRule(string value, string validator, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(validator)) return true;
            var rule = validator.Trim();

            if (rule.Equals("non_empty", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value)) return true;
                error = "Value must not be empty.";
                return false;
            }

            if (value == null) return true;
            if (rule.Equals("positive_number", StringComparison.OrdinalIgnoreCase) ||
                rule.Equals("non_negative_number", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    (rule.StartsWith("non_negative", StringComparison.OrdinalIgnoreCase) ? number >= 0 : number > 0))
                    return true;
                error = rule.StartsWith("non_negative", StringComparison.OrdinalIgnoreCase)
                    ? $"'{value}' must be a non-negative number."
                    : $"'{value}' must be a positive number.";
                return false;
            }

            if (TryParseRange(rule, out var minimum, out var maximum))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    number >= minimum && number <= maximum)
                    return true;
                error = $"'{value}' must be between {minimum} and {maximum}.";
                return false;
            }

            error = $"Unknown or malformed validator '{validator}'.";
            return false;
        }

        static bool TryParseRange(string rule, out double minimum, out double maximum)
        {
            minimum = maximum = 0;
            const string prefix = "range:[";
            if (rule == null || !rule.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !rule.EndsWith("]"))
                return false;
            var values = rule.Substring(prefix.Length, rule.Length - prefix.Length - 1).Split(',');
            return values.Length == 2
                && double.TryParse(values[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minimum)
                && double.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maximum)
                && minimum <= maximum;
        }

        private static bool IsBoolean(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "false":
                case "1":
                case "0":
                case "yes":
                case "no":
                    return true;
                default:
                    return false;
            }
        }
    }
}
