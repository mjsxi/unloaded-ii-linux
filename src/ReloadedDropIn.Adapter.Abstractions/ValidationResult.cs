namespace ReloadedDropIn.Adapter.Abstractions;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ValidationMessage(ValidationSeverity Severity, string Check, string Message);

public sealed record ValidationResult
{
    public IReadOnlyList<ValidationMessage> Messages { get; init; } = [];

    public bool IsValid => Messages.All(m => m.Severity != ValidationSeverity.Error);

    public static ValidationResult Success { get; } = new();

    public static ValidationResult From(IEnumerable<ValidationMessage> messages) =>
        new() { Messages = [.. messages] };
}
