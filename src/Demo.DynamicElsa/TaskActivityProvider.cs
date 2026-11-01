using Elsa.Workflows;
using Elsa.Workflows.Models;

namespace Demo.DynamicElsa;

// Elsa asks this provider for descriptions; it does not create per-task C# classes.
public sealed class TaskActivityProvider(TaskCatalog catalog) : IActivityProvider
{
    // 1. Return the full list of task descriptions whenever Elsa refreshes the registry.
    public ValueTask<IEnumerable<ActivityDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(catalog.Definitions.Select(Describe).ToArray());
    }

    // 2. Describe one task: identity, construction, inputs, and output.
    private ActivityDescriptor Describe(TaskDefinition task) => new()
    {
        // Stable identity is used by saved flows; display details are for the picker.
        TypeName = task.TypeName,
        Namespace = "Demo.Tasks",
        Name = task.TypeName.Split('.').Last(),
        DisplayName = task.DisplayName,
        Version = task.Version,
        Category = "Business tasks",
        Description = "Runs a plain C# task through one shared activity.",
        Kind = ActivityKind.Action,
        IsBrowsable = true,

        // Loading a saved task always creates the same shared activity class.
        Constructor = context =>
        {
            // Elsa's factory restores synthetic input/output bindings when reading saved JSON.
            var result = context.CreateActivity(typeof(DynamicTaskActivity));
            var activity = result.Activity;
            activity.Type = task.TypeName;
            activity.Version = task.Version;
            return result;
        },

        // Each record field becomes a separate typed Elsa input.
        // "Synthetic" means it lives in metadata and SyntheticProperties, not a C# property.
        Inputs = task.Fields.Select((field, index) => new InputDescriptor
        {
            Name = field.Name, DisplayName = field.Name, Type = field.Type, Order = index,
            IsWrapped = true, IsSynthetic = true, IsBrowsable = true, IsSerializable = true,
            // Elsa evaluates bindings before execution. This UI hint is only a placeholder,
            // not a finished editor for nested objects and lists.
            AutoEvaluate = true, UIHint = "SingleLine", DefaultSyntax = "Literal",
            ValueGetter = activity => activity.SyntheticProperties.TryGetValue(field.Name, out var value) ? value : null,
            ValueSetter = (activity, value) => activity.SyntheticProperties[field.Name] = value!
        }).ToList(),

        // One typed result can hold many nested fields for later activities to use.
        Outputs = [new OutputDescriptor
        {
            Name = "Result", DisplayName = "Result", Type = task.OutputType,
            IsSynthetic = true, IsBrowsable = true, IsSerializable = true,
            ValueGetter = activity => activity.SyntheticProperties.TryGetValue("Result", out var value) ? value : null,
            ValueSetter = (activity, value) => activity.SyntheticProperties["Result"] = value!
        }]
    };
}
