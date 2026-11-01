using Demo.DynamicElsa;
using Demo.Tasks;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Demo.DynamicElsa.Tests;

public enum Speed { Standard, Express }
public record Envelope<T>(T Value);
public sealed class Echo<T> : IAppTask<Envelope<T>, Envelope<T>>
{
    public Task<Envelope<T>> ExecuteAsync(Envelope<T> input, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(input); }
}
public record RequiredInput(Customer Value);
public sealed class RequiredTask : IAppTask<RequiredInput, RequiredInput>
{
    public Task<RequiredInput> ExecuteAsync(RequiredInput input, CancellationToken ct) => Task.FromResult(input);
}
public record OptionalInput(string? Note, int? Count);
public sealed class OptionalTask : IAppTask<OptionalInput, OptionalInput>
{
    public Task<OptionalInput> ExecuteAsync(OptionalInput input, CancellationToken ct) => Task.FromResult(input);
}

public class BindingTests
{
    [Fact]
    public void DefaultDependencyInjectionRegistrationKeepsDemoTasks()
    {
        var services = new ServiceCollection(); services.AddSingleton<TaskCatalog>();
        using var host = services.BuildServiceProvider();
        var catalog = host.GetRequiredService<TaskCatalog>();
        Assert.Equal(2, catalog.Definitions.Count);
        Assert.Equal(typeof(Invoice), catalog.Find("Demo.Tasks.CreateInvoice", 1).OutputType);
        Assert.Equal(typeof(Shipment), catalog.Find("Demo.Tasks.PlanShipment", 1).OutputType);
    }

    private static TaskDefinition<Envelope<T>, Envelope<T>> Definition<T>() => new("Tests.Echo", "Echo", 1);
    private static async Task<ServiceProvider> Host<T>(TaskDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddElsa();
        services.AddSingleton(new TaskCatalog([definition]));
        services.AddActivityProvider<TaskActivityProvider>();
        services.AddScoped<IAppTask<Envelope<T>, Envelope<T>>, Echo<T>>();
        var host = services.BuildServiceProvider();
        await host.GetRequiredService<IActivityRegistry>().RefreshDescriptorsAsync(host.GetRequiredService<TaskActivityProvider>());
        return host;
    }

    private static void NoIncidents(RunWorkflowResult result) =>
        Assert.Empty(result.WorkflowState.Incidents);

    private static async Task RoundTrip<T>(T value)
    {
        var definition = Definition<T>();
        await using var host = await Host<T>(definition);
        await using var scope = host.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var descriptors = (await services.GetRequiredService<TaskActivityProvider>().GetDescriptorsAsync()).ToArray();
        Assert.Equal(typeof(T), descriptors.Single().Inputs.Single().Type);
        Assert.Equal(typeof(Envelope<T>), descriptors.Single().Outputs.Single().Type);
        var destination = new Variable<Envelope<T>>();
        var activity = definition.Bind(new Dictionary<string, object> { ["Value"] = new Input<T>(value) }, new Output<Envelope<T>>(destination));
        var flow = new Sequence { Variables = { destination }, Activities = { activity } };
        var runner = services.GetRequiredService<IWorkflowRunner>();
        Envelope<T>? firstObserved = null;
        flow.Activities.Add(new WriteLine(ctx => { firstObserved = destination.Get(ctx); return "first"; }));
        NoIncidents(await runner.RunAsync(flow));
        Assert.NotNull(firstObserved);
        Assert.Equivalent(value, firstObserved.Value, strict: true);
        flow.Activities.Remove(flow.Activities.Last());
        // Literal input definition round trip, using Elsa's actual serializer and provider constructor.
        var serializer = services.GetRequiredService<IActivitySerializer>();
        var restored = (Sequence)serializer.Deserialize(serializer.Serialize(flow));
        var restoredActivity = Assert.IsType<DynamicTaskActivity>(restored.Activities.Single());
        Envelope<T>? observed = null;
        restored.Activities.Add(new WriteLine(ctx =>
        {
            observed = (Envelope<T>)ctx.Get((Output<Envelope<T>>)restoredActivity.SyntheticProperties["Result"])!;
            return "Observed restored output";
        }));
        NoIncidents(await runner.RunAsync(restored));
        Assert.NotNull(observed);
        if (value is DateTime expected && observed.Value is DateTime actual)
        {
            // Elsa 3.8 literal conversion normalizes UTC DateTime to local time.
            // Check the instant explicitly rather than claiming Kind is preserved.
            Assert.Equal(expected.ToUniversalTime(), actual.ToUniversalTime());
        }
        else Assert.Equivalent(value, observed.Value, strict: true);
    }

    [Fact] public Task String() => RoundTrip("hello");
    [Fact] public Task ByteValue() => RoundTrip((byte)255);
    [Fact] public Task FloatValue() => RoundTrip(1.25f);
    [Fact] public Task TimeSpanValue() => RoundTrip(TimeSpan.FromMinutes(90));
    [Fact] public Task DateOnlyValue() => RoundTrip(new DateOnly(2026, 11, 1));
    [Fact] public Task TimeOnlyValue() => RoundTrip(new TimeOnly(12, 30, 0));
    [Fact] public Task NestedLists() => RoundTrip(new List<List<int>> { new() { 1, 2 }, new() { 3 } });
    [Fact] public Task Integer() => RoundTrip(42);
    [Fact] public Task Long() => RoundTrip(9000000000L);
    [Fact] public Task Decimal() => RoundTrip(4130.25m);
    [Fact] public Task Double() => RoundTrip(1.25d);
    [Fact] public Task Boolean() => RoundTrip(false);
    [Fact] public Task DateTimeValue() => RoundTrip(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
    [Fact] public Task DateTimeOffsetValue() => RoundTrip(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(5.5)));
    [Fact] public Task GuidValue() => RoundTrip(Guid.Parse("9d64d90a-3845-457f-b50d-8abda9ba76e5"));
    [Fact] public Task EnumValue() => RoundTrip(Speed.Express);
    [Fact] public Task Array() => RoundTrip(new[] { "one", "two" });
    [Fact] public Task List() => RoundTrip(new List<Line> { new("SKU", 2, 12.5m) });
    [Fact] public Task Dictionary() => RoundTrip(new Dictionary<string, decimal> { ["GST"] = .18m });
    [Fact] public Task NestedObject() => RoundTrip(new Customer("C1", "a@example.test", new("Pune", "411001")));
    [Fact] public Task EmptyList() => RoundTrip(new List<Line>());
    [Fact] public Task NullableValuePresent() => RoundTrip<int?>(42);

    [Fact]
    public async Task NullableConstructorFieldsAcceptNull()
    {
        var definition = new TaskDefinition<OptionalInput, OptionalInput>("Tests.Optional", "Optional", 1);
        var services = new ServiceCollection(); services.AddElsa();
        services.AddSingleton(new TaskCatalog([definition]));
        services.AddScoped<IAppTask<OptionalInput, OptionalInput>, OptionalTask>();
        services.AddActivityProvider<TaskActivityProvider>();
        await using var host = services.BuildServiceProvider();
        await host.GetRequiredService<IActivityRegistry>().RefreshDescriptorsAsync(host.GetRequiredService<TaskActivityProvider>());
        var output = new Variable<OptionalInput>();
        OptionalInput? observed = null;
        var activity = definition.Bind(new Dictionary<string, object>
        { ["Note"] = new Input<string?>((string?)null), ["Count"] = new Input<int?>((int?)null) }, new Output<OptionalInput>(output));
        NoIncidents(await host.GetRequiredService<IWorkflowRunner>().RunAsync(new Sequence
        {
            Variables = { output }, Activities = { activity, new WriteLine(ctx => { observed = output.Get(ctx); return "optional"; }) }
        }));
        Assert.Equal(new OptionalInput(null, null), observed);
        var literalFlow = new Sequence { Variables = { output }, Activities = { activity } };
        var serializer = host.GetRequiredService<IActivitySerializer>();
        var runner = host.GetRequiredService<IWorkflowRunner>();
        NoIncidents(await runner.RunAsync(literalFlow));
        var restored = (Sequence)serializer.Deserialize(serializer.Serialize(literalFlow));
        var restoredActivity = (DynamicTaskActivity)restored.Activities.Single();
        observed = new OptionalInput("sentinel", 1);
        restored.Activities.Add(new WriteLine(ctx => {
            observed = (OptionalInput)ctx.Get((Output<OptionalInput>)restoredActivity.SyntheticProperties["Result"])!;
            return "restored optional";
        }));
        NoIncidents(await runner.RunAsync(restored));
        Assert.Equal(new OptionalInput(null, null), observed);
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("variable")]
    [InlineData("workflow-input")]
    [InlineData("expression")]
    public async Task ValueSourcesAreEvaluatedAtExecution(string source)
    {
        var definition = Definition<decimal>();
        await using var host = await Host<decimal>(definition);
        await using var scope = host.CreateAsyncScope();
        var variable = new Variable<decimal>();
        var destination = new Variable<Envelope<decimal>>();
        var binding = source switch
        {
            "literal" => new Input<decimal>(4130m),
            "variable" => new Input<decimal>(variable),
            "workflow-input" => new Input<decimal>(ctx => ctx.GetInput<decimal>("total")),
            _ => new Input<decimal>(ctx => variable.Get(ctx) * 2m)
        };
        decimal observed = -1;
        var flow = new Sequence
        {
            Variables = { variable, destination },
            Activities = {
                new SetVariable<decimal>(variable, source == "expression" ? 2065m : 4130m),
                definition.Bind(new Dictionary<string, object> { ["Value"] = binding }, new Output<Envelope<decimal>>(destination)),
                new WriteLine(ctx => { observed = destination.Get(ctx)!.Value; return "source"; })
            }
        };
        NoIncidents(await scope.ServiceProvider.GetRequiredService<IWorkflowRunner>().RunAsync(flow,
            new RunWorkflowOptions { Input = new Dictionary<string, object> { ["total"] = 4130m } }));
        Assert.Equal(4130m, observed);
    }

    [Fact]
    public async Task NestedOutputFieldFeedsAnotherTaskAndItsOutputFeedsAThird()
    {
        var invoiceDefinition = new TaskDefinition<InvoiceInput, Invoice>("Tests.Invoice", "Invoice", 1);
        var echoDefinition = Definition<decimal>();
        var services = new ServiceCollection(); services.AddElsa();
        services.AddSingleton(new TaskCatalog([invoiceDefinition, echoDefinition]));
        services.AddActivityProvider<TaskActivityProvider>();
        services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
        services.AddScoped<IAppTask<Envelope<decimal>, Envelope<decimal>>, Echo<decimal>>();
        await using var host = services.BuildServiceProvider();
        await host.GetRequiredService<IActivityRegistry>().RefreshDescriptorsAsync(host.GetRequiredService<TaskActivityProvider>());
        var invoice = new Variable<Invoice>(); var second = new Variable<Envelope<decimal>>(); var third = new Variable<Envelope<decimal>>();
        decimal observed = -1;
        var flow = new Sequence
        {
            Variables = { invoice, second, third },
            Activities = {
                invoiceDefinition.Bind(new Dictionary<string, object> {
                    ["Customer"] = new Input<Customer>(new Customer("C1", "a@example.test", new("Pune", "411001"))),
                    ["Lines"] = new Input<List<Line>>(new List<Line> { new("SKU", 2, 1750m) }),
                    ["Tax"] = new Input<TaxRules>(new TaxRules(.18m, "INR"))
                }, new Output<Invoice>(invoice)),
                echoDefinition.Bind(new Dictionary<string, object> {
                    ["Value"] = new Input<decimal>(ctx => invoice.Get(ctx)!.Totals.GrandTotal)
                }, new Output<Envelope<decimal>>(second)),
                echoDefinition.Bind(new Dictionary<string, object> {
                    ["Value"] = new Input<decimal>(ctx => second.Get(ctx)!.Value * .10m)
                }, new Output<Envelope<decimal>>(third)),
                new WriteLine(ctx => { observed = third.Get(ctx)!.Value; return "chained"; })
            }
        };
        NoIncidents(await host.GetRequiredService<IWorkflowRunner>().RunAsync(flow));
        Assert.Equal(413m, observed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("wrong-input")]
    [InlineData("wrong-output")]
    public void InvalidBindingsFailBeforeExecution(string problem)
    {
        var definition = Definition<decimal>();
        var inputs = new Dictionary<string, object> { ["Value"] = new Input<decimal>(1m) };
        object output = new Output<Envelope<decimal>>();
        if (problem == "missing") inputs.Clear();
        if (problem == "unknown") inputs["Typo"] = new Input<decimal>(1m);
        if (problem == "wrong-input") inputs["Value"] = new Input<string>("bad");
        if (problem == "wrong-output") output = new Output<string>();
        Assert.Throws<ArgumentException>(() => definition.Bind(inputs, output));
    }

    [Theory]
    [InlineData("literal-null")]
    [InlineData("missing-workflow-input")]
    [InlineData("runtime-object")]
    public async Task RequiredRuntimeObjectIsPassedOrRejected(string source)
    {
        var definition = new TaskDefinition<RequiredInput, RequiredInput>("Tests.Required", "Required", 1);
        var services = new ServiceCollection(); services.AddElsa();
        services.AddSingleton(new TaskCatalog([definition]));
        services.AddActivityProvider<TaskActivityProvider>();
        services.AddScoped<IAppTask<RequiredInput, RequiredInput>, RequiredTask>();
        await using var host = services.BuildServiceProvider();
        await host.GetRequiredService<IActivityRegistry>().RefreshDescriptorsAsync(host.GetRequiredService<TaskActivityProvider>());
        var continued = false;
        var destination = new Variable<RequiredInput>();
        RequiredInput? observed = null;
        var customer = new Customer("CALLER", "runtime@example.test", new("Pune", "411001"));
        var binding = source == "literal-null" ? new Input<Customer>((Customer)null!)
            : new Input<Customer>(ctx => ctx.GetInput<Customer>("buyer")!);
        var flow = new Sequence { Variables = { destination }, Activities = {
            definition.Bind(new Dictionary<string, object> { ["Value"] = binding }, new Output<RequiredInput>(destination)),
            new WriteLine(ctx => { continued = true; observed = destination.Get(ctx); return "observed"; })
        }};
        var supplied = new Dictionary<string, object>();
        if (source == "runtime-object") supplied["buyer"] = customer;
        var result = await host.GetRequiredService<IWorkflowRunner>().RunAsync(flow, new RunWorkflowOptions { Input = supplied });
        if (source == "runtime-object")
        {
            NoIncidents(result); Assert.True(continued); Assert.Equal(customer, observed!.Value);
        }
        else
        {
            Assert.False(continued);
            Assert.Contains(result.WorkflowState.Incidents, x => x.Exception?.Message.Contains("Value must not be null") == true);
        }
    }
}
