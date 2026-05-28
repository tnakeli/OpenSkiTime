namespace OpenSkiTime.Application.Common;

/// <summary>
/// Lightweight result type for use-case returns. Avoids using exceptions for
/// expected validation/conflict outcomes, which keeps tests cheap and keeps
/// the UI free of try/catch ceremony for routine errors.
/// </summary>
public readonly record struct Result
{
    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public static Result Success() => new() { Succeeded = true };

    public static Result Failure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new Result { Succeeded = false, ErrorMessage = message };
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Result<T> is an idiomatic factory pattern; ergonomics > rule.")]
public readonly record struct Result<T>
{
    public bool Succeeded { get; init; }

    public T? Value { get; init; }

    public string? ErrorMessage { get; init; }

    public static Result<T> Success(T value)
        => new() { Succeeded = true, Value = value };

    public static Result<T> Failure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new Result<T> { Succeeded = false, ErrorMessage = message };
    }
}
