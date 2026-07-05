using System;
using System.Collections.Generic; // Added for EqualityComparer
using EmailDB.Format.V3; // VerificationError (v3 corruption-handling taxonomy)

namespace EmailDB.Format; // Updated namespace

/// <summary>
/// Represents the result of an operation, indicating success or failure.
/// </summary>
/// <typeparam name="T">The type of the value returned on success.</typeparam>
public class Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T Value { get; }
    public string Error { get; }

    /// <summary>
    /// The structured v3 corruption-handling error (EmailDB_FileFormat_Spec.md
    /// Section 13) when this failure was a verification failure, else
    /// <see langword="null"/>. Its <see cref="Format.V3.VerificationError.Message"/>
    /// equals <see cref="Error"/>; callers branch on
    /// <see cref="Format.V3.VerificationError.Kind"/> to tell corruption from
    /// wrong-key/tamper from integrity failures without string-matching.
    /// </summary>
    public VerificationError? VerificationError { get; }

    private Result(bool isSuccess, T value, string error, VerificationError? verificationError = null)
    {
        if (isSuccess && error != null)
            throw new InvalidOperationException("Successful result cannot have an error message.");
        if (!isSuccess && error == null)
            throw new InvalidOperationException("Failed result must have an error message.");
        // Allow null value for successful results of reference types or nullable value types
        // Check for non-default value only on failure
        if (!isSuccess && value != null && !EqualityComparer<T>.Default.Equals(value, default(T)))
             throw new InvalidOperationException("Failed result cannot have a non-default value.");


        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        VerificationError = verificationError;
    }

    public static Result<T> Success(T value)
    {
        return new Result<T>(true, value, null);
    }

    public static Result<T> Failure(string error)
    {
        // Use default(T) for the value in case of failure
        return new Result<T>(false, default(T), error ?? "Unknown error");
    }

    /// <summary>
    /// Fails carrying a structured v3 verification error (spec Section 13): the
    /// <see cref="Error"/> string is the error's message and
    /// <see cref="VerificationError"/> is the typed error, so a caller can either
    /// read the message or branch on the failure kind.
    /// </summary>
    public static Result<T> Failure(VerificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(false, default(T), error.Message, error);
    }

    // Implicit conversion from T to Result<T> for convenience (optional, can be removed if causing issues)
    // public static implicit operator Result<T>(T value) => Success(value);
}

// Non-generic version for operations without a return value
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    /// <summary>
    /// The structured v3 corruption-handling error (spec Section 13) when this
    /// failure was a verification failure, else <see langword="null"/>.
    /// </summary>
    public VerificationError? VerificationError { get; }

    private Result(bool isSuccess, string error, VerificationError? verificationError = null)
    {
         if (isSuccess && error != null)
            throw new InvalidOperationException("Successful result cannot have an error message.");
        if (!isSuccess && error == null)
            throw new InvalidOperationException("Failed result must have an error message.");

        IsSuccess = isSuccess;
        Error = error;
        VerificationError = verificationError;
    }

     public static Result Success()
    {
        return new Result(true, null);
    }

    public static Result Failure(string error)
    {
        return new Result(false, error ?? "Unknown error");
    }

    /// <summary>
    /// Fails carrying a structured v3 verification error (spec Section 13).
    /// </summary>
    public static Result Failure(VerificationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(false, error.Message, error);
    }
}
