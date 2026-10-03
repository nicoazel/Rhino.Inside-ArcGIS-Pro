using System.Collections.Generic;
using System.Linq;

namespace RhinoArcGIS.Core.Validation
{
    /// <summary>Accumulates validation issues and reports whether the operation is blocked.</summary>
    public sealed class ValidationResult
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

        public IReadOnlyList<ValidationIssue> Issues => _issues;

        /// <summary>An operation is blocked if any issue is an error.</summary>
        public bool IsBlocking => _issues.Any(i => i.Severity == ValidationSeverity.Error);

        public bool HasWarnings => _issues.Any(i => i.Severity == ValidationSeverity.Warning);

        public ValidationResult Add(ValidationIssue issue)
        {
            if (issue != null) _issues.Add(issue);
            return this;
        }

        public ValidationResult AddRange(IEnumerable<ValidationIssue> issues)
        {
            if (issues != null)
                foreach (var i in issues) Add(i);
            return this;
        }
    }
}
