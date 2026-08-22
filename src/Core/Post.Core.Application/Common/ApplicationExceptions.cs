namespace Post.Core.Application.Common;

public abstract class CoreApplicationException : Exception
{
    protected CoreApplicationException(string message) : base(message)
    {
    }

    protected CoreApplicationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class CoreValidationException : CoreApplicationException
{
    public CoreValidationException(string message) : base(message)
    {
    }
}

public sealed class CorePayloadTooLargeException : CoreApplicationException
{
    public CorePayloadTooLargeException(string message) : base(message)
    {
    }
}

public sealed class CoreNotFoundException : CoreApplicationException
{
    public CoreNotFoundException(string message) : base(message)
    {
    }
}

public sealed class CoreConflictException : CoreApplicationException
{
    public CoreConflictException(string message) : base(message)
    {
    }

    public CoreConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class CoreAuthorizationException : CoreApplicationException
{
    public CoreAuthorizationException(string message) : base(message)
    {
    }
}

public sealed class CoreDependencyUnavailableException : CoreApplicationException
{
    public CoreDependencyUnavailableException(string message) : base(message)
    {
    }

    public CoreDependencyUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
