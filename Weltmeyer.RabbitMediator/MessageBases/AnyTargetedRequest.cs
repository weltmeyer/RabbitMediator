using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;
/// <summary>
/// Represents a targeted request type that expects a response of type TResponse.
/// </summary>
/// <typeparam name="TResponse">The type of response expected from this request, which must inherit from Response.</typeparam>
/// <property name="SenderId">
///     <summary>
///     Unique identifier of the sender who originated this request. This property is included in JSON serialization.
///     </summary>
/// </property>
/// <property name="RequestId">
///     <summary>
///     Unique identifier of this specific request instance. This property is included in JSON serialization.
///     </summary>
/// </property>
public abstract class AnyTargetedRequest<TResponse> : Request<TResponse>, IAnyTargetedSentObject
    where TResponse : Response
{
}