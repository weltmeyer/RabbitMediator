using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;


namespace Weltmeyer.RabbitMediator;

internal class JsonSerializerHelper
{
    private JsonSerializerOptions? _options;
    private Type[] _knownTypes = [];

    private JsonSerializerOptions RebuildOptions(Type[] newKnownTypes)
    {
        return new JsonSerializerOptions
        {
            TypeInfoResolver = new SentObjectTypeResolver(newKnownTypes),
        };
    }


    private readonly SemaphoreSlim _typeSemaphore = new(1, 1);

    public async Task AddTypeIfMissing(Type newType)
    {
        if (_knownTypes.Contains(newType))
            return;
        await _typeSemaphore.WaitAsync();
        try
        {
            if (_knownTypes.Contains(newType))
                return;
            var newKnownTypes = _knownTypes.Concat([newType]).ToArray();
            _options = RebuildOptions(newKnownTypes);
            _options.MakeReadOnly(true);
            _knownTypes = newKnownTypes;
        }
        finally
        {
            _typeSemaphore.Release();
        }
    }

    /// <summary>
    /// Serializes the given object and calls action with the utf8 encoded byte data 
    /// </summary>
    /// <param name="objectToSerialize"></param>
    /// <param name="action"></param>
    /// <typeparam name="T"></typeparam>
    public async Task Serialize<T>(T objectToSerialize, Func<ReadOnlyMemory<byte>, Task> action)
        where T : class
    {
        
        using var stream = new MemoryStream();
        if (objectToSerialize is ISentObject sentObject)
        {
            //this looks strange but is needed for the polymorphic $type setting in the json
            await AddTypeIfMissing(objectToSerialize.GetType());
            try
            {
                /*   if (_options.TryGetTypeInfo(typeof(ISentObject), out var typeInfo))
                   {
                       if (typeInfo.PolymorphismOptions?.DerivedTypes.All(dt =>
                               dt.DerivedType != objectToSerialize.GetType()) ?? true)
                       {
                           Debugger.Break();
                       }
                   }
                   else
                   {
                       Debugger.Break();
                   }*/

                await JsonSerializer.SerializeAsync(stream, sentObject, _options);
            }
            catch (NotSupportedException e)
            {
                try
                {
                    //retry non async.. i cant find the reason why this fails in async SOMETIMES
                    JsonSerializer.Serialize(stream, sentObject, _options);
                }
                catch (Exception)
                {
                    if (Debugger.IsAttached)
                        Debugger.Break();
                    throw;
                }

                if (Debugger.IsAttached)
                    Debugger.Break();
                throw;
            }
        }
        else
        {
            await JsonSerializer.SerializeAsync(stream, objectToSerialize, _options);
        }

        using var buffer = MemoryPool<byte>.Shared.Rent((int)stream.Length);
        stream.Position = 0;
        _ = await stream.ReadAsync(buffer.Memory);
        await action(buffer.Memory.Slice(0, (int)stream.Length));
    }

    public Task<T> Deserialize<T>(ReadOnlyMemory<byte> input)
    {
        var jsonUtfReader = new Utf8JsonReader(input.Span);

        var obj = JsonSerializer.Deserialize<T>(ref jsonUtfReader, _options)!;
        return Task.FromResult(obj);
    }
}