namespace FreeSpaceWatcher.Service.Pipe;

/// <summary>Thrown at start when another process already serves the service pipe.</summary>
public sealed class PipeNameInUseException : IOException
{
    /// <summary>Initializes a new instance with a default message.</summary>
    public PipeNameInUseException()
        : base("The service pipe is already in use by another process.") { }

    /// <summary>Initializes a new instance with a message.</summary>
    /// <param name="message">What happened.</param>
    public PipeNameInUseException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance with a message and the failure that caused it.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The pipe creation failure.</param>
    public PipeNameInUseException(string message, Exception innerException)
        : base(message, innerException) { }
}
