using System.Diagnostics.CodeAnalysis;

namespace OpenForecourt.Abstractions;

/// <summary>
/// A minimal success-or-error result. Used where a port models an <b>expected</b> failure
/// as a return value rather than an exception (CLAUDE.md section 6: "No exceptions for
/// expected outcomes").
/// </summary>
/// <typeparam name="TValue">The success payload type.</typeparam>
/// <typeparam name="TError">The error payload type.</typeparam>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Ok/Fail are the idiomatic construction API for a result type; the generic parameters are exactly what the caller is choosing.")]
public readonly record struct Result<TValue, TError>
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, TValue? value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed.</summary>
    public bool IsError => !IsSuccess;

    /// <summary>The success value.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the result is an error.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Result is an error; no value present.");

    /// <summary>The error value.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the result is a success.</exception>
    public TError Error => IsError
        ? _error!
        : throw new InvalidOperationException("Result is a success; no error present.");

    /// <summary>Creates a success result.</summary>
    public static Result<TValue, TError> Ok(TValue value) => new(true, value, default);

    /// <summary>Creates an error result.</summary>
    public static Result<TValue, TError> Fail(TError error) => new(false, default, error);
}
