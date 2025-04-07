using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.Contracts;

public record InstanceInformation
{
    internal InstanceInformation()
    {
    }

    [JsonConstructor]
    internal InstanceInformation(Guid instanceId, Guid instanceScope)
    {
        this.InstanceId = instanceId;
        this.InstanceScope = instanceScope;
    }

    [JsonInclude] public Guid InstanceId { get; init; }

    [JsonInclude] public Guid InstanceScope { get; init; }

    public virtual bool Equals(InstanceInformation? other)
    {
        return other != null && InstanceId == other.InstanceId && InstanceScope == other.InstanceScope;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(InstanceId, InstanceScope);
    }

    public static readonly InstanceInformation Empty = new(Guid.Empty, Guid.Empty);
}