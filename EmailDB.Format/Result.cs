using System;
using System.Collections.Generic; // Added for EqualityComparer

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

    private Result(bool isSuccess, T value, string error)
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

    // Implicit conversion from T to Result<T> for convenience (optional, can be removed if causing issues)
    // public static implicit operator Result<T>(T value) => Success(value);
}

// Non-generic version for operations without a return value
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    private Result(bool isSuccess, string error)
    {
         if (isSuccess && error != null)
            throw new InvalidOperationException("Successful result cannot have an error message.");
        if (!isSuccess && error == null)
            throw new InvalidOperationException("Failed result must have an error message.");

        IsSuccess = isSuccess;
        Error = error;
    }

     public static Result Success()
    {
        return new Result(true, null);
    }

    public static Result Failure(string error)
    {
        return new Result(false, error ?? "Unknown error");
    }
}