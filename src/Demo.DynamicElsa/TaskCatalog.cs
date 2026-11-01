using System.Reflection;
using Demo.Tasks;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;

namespace Demo.DynamicElsa;

// 1. Catalog: the allowed tasks and the versions saved flows can use.
// Explicit stable names and versions; only allow tasks deployed in this catalog.
public sealed class TaskCatalog
{
    public TaskCatalog() : this(
    [
        new TaskDefinition<InvoiceInput, Invoice>("Demo.Tasks.CreateInvoice", "Create invoice", 1),
        new TaskDefinition<ShipmentInput, Shipment>("Demo.Tasks.PlanShipment", "Plan shipment", 1)
    ]) { }

    public TaskCatalog(IReadOnlyList<TaskDefinition> definitions) => Definitions = definitions.ToArray();

    public IReadOnlyList<TaskDefinition> Definitions { get; }

    // Require an exact version so an old flow does not silently use new behavior.
    public TaskDefinition Find(string type, int version) =>
        Definitions.SingleOrDefault(x => x.TypeName == type && x.Version == version)
        ?? throw new InvalidOperationException($"Unknown task: {type} v{version}.");
}

// 2. Task description: read the input shape and build activity bindings.
public abstract class TaskDefinition(string typeName, string displayName, int version, Type inputType, Type outputType)
{
    public string TypeName { get; } = typeName;
    public string DisplayName { get; } = displayName;
    public int Version { get; } = version;
    public Type OutputType { get; } = outputType;
    // This demo deliberately supports one public input constructor, avoiding ambiguous choices.
    protected ConstructorInfo InputConstructor { get; } = inputType.GetConstructors().Single();
    // Turn each constructor parameter into a field reader with the same C# type.
    public IReadOnlyList<TaskField> Fields { get; } = inputType.GetConstructors().Single().GetParameters()
        .Select(p => (TaskField)Activator.CreateInstance(typeof(TaskField<>).MakeGenericType(p.ParameterType), p.Name!, AllowsNull(p))!).ToArray();

    private static bool AllowsNull(ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null ||
        (!parameter.ParameterType.IsValueType &&
         new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable);

    // Used while building the flow. Store Input<T> bindings, not evaluated values.
    public DynamicTaskActivity Bind(IReadOnlyDictionary<string, object> inputs, object output)
    {
        // Catch misspelled field names before creating the activity.
        foreach (var name in inputs.Keys)
            if (!Fields.Any(f => f.Name == name)) throw new ArgumentException($"Unknown input: {name}.");

        // SyntheticProperties holds fields that have no fixed property on the shared class.
        var activity = new DynamicTaskActivity { Type = TypeName, Version = Version };
        foreach (var field in Fields)
        {
            if (!inputs.TryGetValue(field.Name, out var value)) throw new ArgumentException($"Missing input: {field.Name}.");
            field.ValidateBinding(value);
            activity.SyntheticProperties[field.Name] = value;
        }

        // The destination must accept the task's real output type.
        if (output.GetType() != typeof(Output<>).MakeGenericType(OutputType))
            throw new ArgumentException("Output type does not match the task result.");
        activity.SyntheticProperties["Result"] = output;
        return activity;
    }
    public abstract ValueTask ExecuteAsync(DynamicTaskActivity activity, ActivityExecutionContext context);
}

// 3. Execution: turn evaluated Elsa inputs into a normal task call.
public sealed class TaskDefinition<TInput, TOutput>(string typeName, string displayName, int version)
    : TaskDefinition(typeName, displayName, version, typeof(TInput), typeof(TOutput))
{
    public override async ValueTask ExecuteAsync(DynamicTaskActivity activity, ActivityExecutionContext context)
    {
        // Read values in constructor order, keeping nested objects and lists intact.
        var values = Fields.Select(f => f.Read(activity, context)).ToArray();
        var input = (TInput)InputConstructor.Invoke(values);

        // Resolve the business implementation and pass cancellation through.
        var task = context.GetRequiredService<IAppTask<TInput, TOutput>>();
        var result = await task.ExecuteAsync(input, context.CancellationToken);

        // Send the result to Elsa when the flow has an output binding.
        if (activity.SyntheticProperties.TryGetValue("Result", out var output))
            context.Set((Output<TOutput>)output, result);
    }
}

// 4. Field readers: a shared description with a typed reader for each field.
public abstract class TaskField(string name, Type type)
{
    public string Name { get; } = name;
    public Type Type { get; } = type;
    public abstract object? Read(DynamicTaskActivity activity, ActivityExecutionContext context);
    public abstract void ValidateBinding(object value);
}

public sealed class TaskField<T>(string name, bool allowsNull) : TaskField(name, typeof(T))
{
    // Build-time check: for example, Customer needs Input<Customer>, not Input<string>.
    public override void ValidateBinding(object value)
    {
        if (value is not Input<T>) throw new ArgumentException($"{Name} requires Input<{typeof(T).Name}>.");
    }

    // Runtime check: the binding must exist, have the right type, and yield a value.
    public override object? Read(DynamicTaskActivity activity, ActivityExecutionContext context)
    {
        if (!activity.SyntheticProperties.TryGetValue(Name, out var value) || value is not Input<T> input)
            throw new ArgumentException($"Missing or incorrectly typed input: {Name}.");
        var result = context.Get(input);
        if (result is null && !allowsNull) throw new ArgumentException($"{Name} must not be null.");
        return result;
    }
}
