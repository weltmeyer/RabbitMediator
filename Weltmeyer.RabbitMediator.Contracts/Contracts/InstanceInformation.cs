using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.Contracts.Contracts;

public record InstanceInformation
{
    internal InstanceInformation()
    {
    }

    [JsonConstructor]
    internal InstanceInformation(string instanceId, string instanceScope)
    {
        this.InstanceId = instanceId;
        this.InstanceScope = instanceScope;
    }

    [JsonInclude] public string InstanceId { get; init; }

    [JsonInclude] public string InstanceScope { get; init; }

    public virtual bool Equals(InstanceInformation? other)
    {
        return other != null && InstanceId == other.InstanceId && InstanceScope == other.InstanceScope;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(InstanceId, InstanceScope);
    }

    public static readonly InstanceInformation Empty = new(string.Empty, string.Empty);

    public override string ToString()
    {
        return $"{InstanceId}:{InstanceScope}";
    }
}