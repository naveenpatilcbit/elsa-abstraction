using System.Text.Json;
using System.Text.Json.Serialization;
using Demo.Tasks;

namespace Demo.TaskRunner;

// Engine-independent dispatcher. Only explicit, versioned registrations can run.
public sealed class TaskRegistry
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> handlers = new(StringComparer.Ordinal);

    public void Register<TInput, TOutput>(string name, IAppTask<TInput, TOutput> task)
        where TInput : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(task);
        if (!handlers.TryAdd(name, async (json, ct) =>
        {
            TInput input;
            try
            {
                input = json.Deserialize<TInput>(JsonOptions)
                    ?? throw new JsonException("Input must not be null.");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"{name}: invalid input at {ex.Path ?? "$"}: {ex.Message}", ex);
            }
            var output = await task.ExecuteAsync(input, ct);
            return JsonSerializer.SerializeToElement(output, JsonOptions);
        })) throw new InvalidOperationException($"Task '{name}' is already registered.");
    }

    public Task<JsonElement> RunAsync(string name, JsonElement input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!handlers.TryGetValue(name, out var handler))
            throw new ArgumentException($"Unknown task '{name}'.");
        return handler(input, ct);
    }
}
