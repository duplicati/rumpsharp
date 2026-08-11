namespace RumpSharp;

/// <summary>Thrown when macOS refuses to accept or deliver a notification.</summary>
public sealed class NotificationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Description of the failure, usually straight from macOS.</param>
    public NotificationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">The underlying failure.</param>
    public NotificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
