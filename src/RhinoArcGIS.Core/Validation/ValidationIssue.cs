namespace RhinoArcGIS.Core.Validation
{
    public enum ValidationSeverity
    {
        Info = 0,
        Warning,
        Error
    }

    /// <summary>A single validation finding (spec 03 §4–§6).</summary>
    public sealed class ValidationIssue
    {
        public string Code { get; }
        public string Message { get; }
        public ValidationSeverity Severity { get; }

        public ValidationIssue(string code, string message, ValidationSeverity severity)
        {
            Code = code;
            Message = message;
            Severity = severity;
        }

        public static ValidationIssue Error(string code, string message) =>
            new ValidationIssue(code, message, ValidationSeverity.Error);

        public static ValidationIssue Warning(string code, string message) =>
            new ValidationIssue(code, message, ValidationSeverity.Warning);

        public static ValidationIssue Info(string code, string message) =>
            new ValidationIssue(code, message, ValidationSeverity.Info);

        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }
}
