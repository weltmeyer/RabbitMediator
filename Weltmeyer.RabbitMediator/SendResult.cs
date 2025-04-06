using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator;

public class SendResult
{
    internal SendResult(){}
    
    public bool Success { get; internal set; }
    
    /// <summary>
    /// Happens only on targeted messages, when we cant route the message to the targed
    /// </summary>
    public bool SendFailure { get; internal set; }
    
    public ExceptionData? ExceptionData { get; internal set; }
    
    public bool TimedOut { get; internal set; }
    
}