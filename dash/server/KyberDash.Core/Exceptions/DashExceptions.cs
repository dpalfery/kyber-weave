namespace KyberDash.Core.Exceptions;

/// <summary>Base domain/infrastructure exception for KyberDash failures.</summary>
public class DashException : Exception
{
    /// <summary>Initializes a new <see cref="DashException"/>.</summary>
    public DashException()
    {
    }

    /// <summary>Initializes a new <see cref="DashException"/> with a diagnostic message.</summary>
    public DashException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="DashException"/> with a message and inner cause.</summary>
    public DashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an external-call circuit breaker is open and fast-failing requests.</summary>
public class CircuitBreakerOpenException : DashException
{
    /// <summary>Initializes a new <see cref="CircuitBreakerOpenException"/>.</summary>
    public CircuitBreakerOpenException()
        : base("The external dependency circuit breaker is open.")
    {
    }

    /// <summary>Initializes a new <see cref="CircuitBreakerOpenException"/> with a message.</summary>
    public CircuitBreakerOpenException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="CircuitBreakerOpenException"/> with a message and inner cause.</summary>
    public CircuitBreakerOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a <c>sqlite3</c> CLI execution fails or times out.</summary>
public class SqliteExecutionException : DashException
{
    /// <summary>Process exit code returned by <c>sqlite3</c>, or <c>-1</c> on launch/timeout failure.</summary>
    public int ExitCode { get; }

    /// <summary>Captured standard error output from <c>sqlite3</c>.</summary>
    public string StandardError { get; }

    /// <summary>Initializes a new <see cref="SqliteExecutionException"/>.</summary>
    public SqliteExecutionException()
    {
        ExitCode = -1;
        StandardError = string.Empty;
    }

    /// <summary>Initializes a new <see cref="SqliteExecutionException"/> with a message.</summary>
    public SqliteExecutionException(string message)
        : base(message)
    {
        ExitCode = -1;
        StandardError = string.Empty;
    }

    /// <summary>Initializes a new <see cref="SqliteExecutionException"/> with exit details.</summary>
    public SqliteExecutionException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>Initializes a new <see cref="SqliteExecutionException"/> with an inner cause.</summary>
    public SqliteExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
        ExitCode = -1;
        StandardError = string.Empty;
    }
}

/// <summary>Thrown when an incoming OTLP request payload exceeds the maximum permitted body size (HTTP 413).</summary>
public class OtlpPayloadTooLargeException : DashException
{
    /// <summary>Initializes a new <see cref="OtlpPayloadTooLargeException"/>.</summary>
    public OtlpPayloadTooLargeException()
    {
    }

    /// <summary>Initializes a new <see cref="OtlpPayloadTooLargeException"/> with a message.</summary>
    public OtlpPayloadTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="OtlpPayloadTooLargeException"/> with a message and inner cause.</summary>
    public OtlpPayloadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an OTLP JSON or protobuf payload is malformed or violates the expected OTLP schema.</summary>
public class OtlpDecodeException : DashException
{
    /// <summary>Initializes a new <see cref="OtlpDecodeException"/>.</summary>
    public OtlpDecodeException()
    {
    }

    /// <summary>Initializes a new <see cref="OtlpDecodeException"/> with a message.</summary>
    public OtlpDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="OtlpDecodeException"/> with a message and inner cause.</summary>
    public OtlpDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when domain or request validation rejects invalid parameters.</summary>
public class DashValidationException : DashException
{
    /// <summary>Machine-readable error code for structured HTTP/CLI error responses.</summary>
    public string Code { get; }

    /// <summary>Initializes a new <see cref="DashValidationException"/>.</summary>
    public DashValidationException()
    {
        Code = "VALIDATION_ERROR";
    }

    /// <summary>Initializes a new <see cref="DashValidationException"/> with a message.</summary>
    public DashValidationException(string message)
        : base(message)
    {
        Code = "VALIDATION_ERROR";
    }

    /// <summary>Initializes a new <see cref="DashValidationException"/> with a machine-readable code and message.</summary>
    public DashValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Initializes a new <see cref="DashValidationException"/> with a message and inner cause.</summary>
    public DashValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "VALIDATION_ERROR";
    }
}

/// <summary>Thrown when a child process produces more output than its configured byte budget.</summary>
public class OutputLimitExceededException : InvalidOperationException
{
    /// <summary>Initializes a new <see cref="OutputLimitExceededException"/>.</summary>
    public OutputLimitExceededException()
    {
    }

    /// <summary>Initializes a new <see cref="OutputLimitExceededException"/> with a message.</summary>
    public OutputLimitExceededException(string? message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="OutputLimitExceededException"/> with a message and inner cause.</summary>
    public OutputLimitExceededException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when downloading, verifying, or installing the KyberDash tray binary fails.</summary>
public class TrayInstallException : DashException
{
    /// <summary>Installation step at which the failure occurred.</summary>
    public string Step { get; } = "install";

    /// <summary>Initializes a new <see cref="TrayInstallException"/>.</summary>
    public TrayInstallException()
    {
    }

    /// <summary>Initializes a new <see cref="TrayInstallException"/> with a message.</summary>
    public TrayInstallException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="TrayInstallException"/> with a message and inner cause.</summary>
    public TrayInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new <see cref="TrayInstallException"/> with a failing step name and message.</summary>
    public TrayInstallException(string step, string message)
        : base(message)
    {
        Step = step;
    }

    /// <summary>Initializes a new <see cref="TrayInstallException"/> with a failing step name, message, and inner cause.</summary>
    public TrayInstallException(string step, string message, Exception? innerException)
        : base(message, innerException!)
    {
        Step = step;
    }
}

/// <summary>
/// Thrown when the store read a stored row it cannot parse: a wrong column count, an
/// unrecognized wire token or row tag, invalid hex, or a non-numeric scalar. It names one
/// session's corrupt stored data, so a per-session read may isolate it, unlike a programming
/// error. It derives from <see cref="InvalidOperationException"/>, which the store threw for
/// these faults before, so every caller that catches that type still catches this one.
/// </summary>
public class StoredDataException : InvalidOperationException
{
    /// <summary>Initializes a new <see cref="StoredDataException"/>.</summary>
    public StoredDataException()
        : base("The store returned a row it cannot parse.")
    {
    }

    /// <summary>Initializes a new <see cref="StoredDataException"/> with a diagnostic message.</summary>
    public StoredDataException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new <see cref="StoredDataException"/> with a message and inner cause.</summary>
    public StoredDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
