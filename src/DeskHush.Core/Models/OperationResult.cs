namespace DeskHush.Core.Models;

public sealed record OperationResult(bool Succeeded, string Message, bool RequiresElevation = false)
{
    public static OperationResult Success(string message = "OK") => new(true, message);

    public static OperationResult Failure(string message, bool requiresElevation = false) =>
        new(false, message, requiresElevation);
}
