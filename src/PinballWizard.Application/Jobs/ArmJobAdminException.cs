namespace PinballWizard.Application.Jobs;

public sealed class ArmJobAdminException : Exception
{
    public bool IsNotFound { get; }

    public ArmJobAdminException(string message, Exception? inner = null, bool isNotFound = false)
        : base(message, inner)
    {
        IsNotFound = isNotFound;
    }
}
