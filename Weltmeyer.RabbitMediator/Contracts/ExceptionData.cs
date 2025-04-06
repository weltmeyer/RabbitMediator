using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.Contracts;

public class ExceptionData
{
    public static ExceptionData FromException(Exception ex)
    {
        return new ExceptionData
        {
            InnerException = ex.InnerException != null
                ? FromException(ex.InnerException)
                : null,
            Source = ex.Source,
            ErrorMessage = ex.Message,
            TypeFullName = ex.GetType().FullName,
            Stacktrace = ex.StackTrace
        };
        
    }

    [JsonInclude] public string ErrorMessage { get; internal init; } = null!;

    [JsonInclude] public string? Stacktrace { get; internal init; }

    [JsonInclude] public ExceptionData? InnerException { get; internal init; }

    [JsonInclude] public string? TypeFullName { get; internal init; }

    [JsonInclude] public string? Source { get; internal init; }
}