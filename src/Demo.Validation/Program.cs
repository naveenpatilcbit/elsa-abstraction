using System.Text.Json;
using Demo.Tasks;
using Demo.Elsa.Generated;
using Demo.Generator;
using Demo.Validation;
using Demo.Validation.Generated;
using Demo.Validation.Tasks;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;

// Modes are separate process runs: checks, prepare (old deployment), resume (new deployment).
var mode = args.FirstOrDefault() ?? "checks";
var evidenceDirectory = Path.GetFullPath(args.ElementAtOrDefault(1) ?? ".validation-state");
var checks = 0;
await using var host = TestHost.Create(includeV2: mode != "prepare");
await using var scope = host.CreateAsyncScope();
var services = scope.ServiceProvider;
var runner = services.GetRequiredService<IWorkflowRunner>();
var serializer = services.GetRequiredService<IActivitySerializer>();
var stateSerializer = services.GetRequiredService<IWorkflowStateSerializer>();
var lookup = services.GetRequiredService<IActivityRegistryLookupService>();

if (mode == "prepare")
{
    // 1. Run invoice, then pause before shipment. No lambdas are saved in this definition.
    Directory.CreateDirectory(evidenceDirectory);
    var invoice = new Variable<Invoice>("invoice", default!).WithStorageDriver<WorkflowInstanceStorageDriver>();
    var shipment = new Variable<Shipment>("shipment", default!).WithStorageDriver<WorkflowInstanceStorageDriver>();
    var workflow = new Workflow(new Sequence
    {
        Variables = { invoice, shipment },
        Activities =
        {
            InvoiceActivity(new Output<Invoice>(invoice)),
            new WaitForApproval(),
            new PlanShipmentActivity
            {
                Invoice = new(invoice), Origin = new(new Warehouse("WH-BLR", new("Bengaluru", "560048"))),
                Packages = new(Packages()), Rules = new(new DeliveryRules(10, true)), Result = new(shipment)
            },
            new CheckShipment { Shipment = new(shipment) }
        }
    });
    var paused = await runner.RunAsync(workflow);
    NoIncidents(paused);
    Check(paused.WorkflowState.Bookmarks.Count == 1, "Workflow pauses at approval");
    Check(services.GetRequiredService<RunObservations>().InvoiceCalls == 1, "Invoice ran once before pause");
    File.WriteAllText(Path.Combine(evidenceDirectory, "workflow.json"), serializer.Serialize(workflow));
    File.WriteAllText(Path.Combine(evidenceDirectory, "state.json"), stateSerializer.Serialize(paused.WorkflowState));
    File.WriteAllText(Path.Combine(evidenceDirectory, "process.txt"), Environment.ProcessId.ToString());

    // 2. Save a v1-only flow. The next process will also deploy v2 with a changed input contract.
    var quote = new QuoteV1Activity { Amount = new(100m) };
    var oldFlow = new Sequence { Activities = { quote } };
    NoIncidents(await runner.RunAsync(oldFlow));
    Check(await lookup.FindAsync("Validation.Quote", 2) == null, "Old deployment has no v2 activity");
    File.WriteAllText(Path.Combine(evidenceDirectory, "quote-v1.json"), serializer.Serialize(oldFlow));
    Console.WriteLine($"PREPARED: {checks} checks; process {Environment.ProcessId} exits with saved state.");
    return;
}

if (mode == "resume")
{
    // 3. Deserialize definition and state in a genuinely new process, then resume its bookmark.
    Check(File.ReadAllText(Path.Combine(evidenceDirectory, "process.txt")) != Environment.ProcessId.ToString(), "Resume uses a different process");
    // Populate types before deserialization, just as the server does at startup.
    await lookup.FindAsync(typeof(CreateInvoiceActivity).FullName!);
    await lookup.FindAsync("Validation.Quote", 1);
    var workflow = (Workflow)serializer.Deserialize(File.ReadAllText(Path.Combine(evidenceDirectory, "workflow.json")));
    var state = stateSerializer.Deserialize(File.ReadAllText(Path.Combine(evidenceDirectory, "state.json")));
    var resumed = await runner.RunAsync(workflow, state, new RunWorkflowOptions { BookmarkId = state.Bookmarks.Single().Id });
    NoIncidents(resumed);
    Check(resumed.WorkflowState.Bookmarks.Count == 0, "Bookmark is consumed after resume");
    Check(services.GetRequiredService<RunObservations>().Shipment is not null, "Nested invoice and package data survive process restart");
    Check(services.GetRequiredService<RunObservations>().InvoiceCalls == 0, "Resume does not rerun the completed invoice task");

    // 4. Load the old v1 definition while both versions are deployed.
    var old = (Sequence)serializer.Deserialize(File.ReadAllText(Path.Combine(evidenceDirectory, "quote-v1.json")));
    Check(old.Activities.Single() is QuoteV1Activity, "Saved v1 resolves to its original adapter after v2 is added");
    var oldActivity = (QuoteV1Activity)old.Activities.Single();
    var oldQuote = await Observe(oldActivity, oldActivity.Result);
    var newer = new QuoteV2Activity { Amount = new(100m), Discount = new(15m) };
    var newQuote = await Observe(newer, newer.Result);
    Check(oldQuote == new Quote(100m, "v1") && newQuote == new Quote(85m, "v2"), "Old and new contracts keep separate behavior");
    Console.WriteLine($"RESUME/UPGRADE: {checks} checks passed.");
    return;
}

// 5. Round-trip diverse input types through Elsa's definition serializer.
var probeData = new ProbeInput("ORDER-42", 7, 12.50m, true,
    new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.FromHours(5.5)), ShippingSpeed.Express,
    null, null, new("Bengaluru", "560001"), new() { new("SKU", 2, 10m) }, new() { ["GST"] = 0.18m }, ["fragile", "gift"]);
var probe = new ProbeActivity
{
    Reference = new(probeData.Reference), Count = new(probeData.Count), Amount = new(probeData.Amount), Enabled = new(true),
    When = new(probeData.When), Speed = new(probeData.Speed), OptionalCount = new((int?)null), Note = new((string?)null),
    Address = new(probeData.Address), Lines = new(probeData.Lines), Rates = new(probeData.Rates), Tags = new(probeData.Tags)
};
var initial = await Observe(probe, probe.Result);
Check(JsonSerializer.Serialize(initial) == JsonSerializer.Serialize(probeData), "All input types arrive unchanged");
var probeFlow = new Sequence { Activities = { probe } };
var probeJson = serializer.Serialize(probeFlow);
var loadedProbe = (ProbeActivity)((Sequence)serializer.Deserialize(probeJson)).Activities.Single();
var roundTrip = await Observe(loadedProbe, loadedProbe.Result);
Check(JsonSerializer.Serialize(roundTrip) == JsonSerializer.Serialize(probeData), "Dates, enum, nulls, dictionary, array and nested list survive save/load");

// 6. Reuse one generated adapter twice and read nested output in an input expression.
var first = new Variable<Invoice>();
var second = new Variable<Invoice>();
Invoice? secondResult = null;
var reuseFlow = new Sequence
{
    Variables = { first, second },
    Activities =
    {
        InvoiceActivity(new Output<Invoice>(first)),
        new CreateInvoiceActivity
        {
            Customer = new(ctx => first.Get(ctx)!.Customer),
            Lines = new(ctx => new List<Line> { new("DERIVED", 1, first.Get(ctx)!.Totals.GrandTotal) }),
            Tax = new(new TaxRules(0m, "INR")), Result = new(second)
        },
        new WriteLine(ctx => { secondResult = second.Get(ctx); return "Reused adapter finished."; })
    }
};
NoIncidents(await runner.RunAsync(reuseFlow));
Check(secondResult?.Totals.GrandTotal == 4130m && secondResult.Lines[0].Sku == "DERIVED", "Repeated activity reads prior nested output and builds a new list");

// A workflow caller can supply typed data; an input expression reads it by name.
var incomingInvoice = new Variable<Invoice>();
Invoice? incomingResult = null;
var incoming = InvoiceActivity(new Output<Invoice>(incomingInvoice));
incoming.Customer = new(ctx => ctx.GetInput<Customer>("buyer"));
NoIncidents(await runner.RunAsync(new Sequence
{
    Variables = { incomingInvoice },
    Activities = { incoming, new WriteLine(ctx => { incomingResult = incomingInvoice.Get(ctx); return "Workflow input received."; }) }
}, new RunWorkflowOptions { Input = new Dictionary<string, object> { ["buyer"] = new Customer("FROM-CALLER", "caller@example.test", new("Pune", "411001")) } }));
Check(incomingResult?.Customer.Id == "FROM-CALLER", "Task input can come from workflow input");

// 7. Missing bindings and business validation must produce named errors in Elsa incidents.
var missing = InvoiceActivity(); missing.Customer = null!;
var missingRun = await runner.RunAsync(missing);
Check(Messages(missingRun).Contains("Missing input: Customer"), "Missing binding names the field");
var invalid = InvoiceActivity(); invalid.Lines = new(new List<Line> { new("BAD", 0, 5m) });
Check(Messages(await runner.RunAsync(invalid)).Contains("Lines[0].Quantity"), "Invalid nested value names its path");

// 8. Two tasks in a scope share one dependency. Concurrent scopes must not share it.
var scopeA = new ScopeActivity { RequestId = new("A") };
var scopeB = new ScopeActivity { RequestId = new("B") };
var a = await Observe(scopeA, scopeA.Result);
var b = await Observe(scopeB, scopeB.Result);
Check(a.ScopeId == b.ScopeId && a.RequestId != b.RequestId, "Scoped dependency is reused without mixing task inputs");
var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
{
    await using var child = host.CreateAsyncScope();
    var variable = new Variable<ScopeOutput>();
    var activity = new ScopeActivity { RequestId = new("request-" + i), Result = new(variable) };
    ScopeOutput? output = null;
    var sequence = new Sequence { Variables = { variable }, Activities = { activity, new WriteLine(ctx => { output = variable.Get(ctx); return ""; }) } };
    NoIncidents(await child.ServiceProvider.GetRequiredService<IWorkflowRunner>().RunAsync(sequence));
    return output!;
}));
Check(concurrent.Select(x => x.ScopeId).Distinct().Count() == 20 && concurrent.Select(x => x.RequestId).Distinct().Count() == 20,
    "20 concurrent workflow runs have separate scopes and inputs");

// Two branches in one flow also use separate output locations.
var left = new Variable<ScopeOutput>();
var right = new Variable<ScopeOutput>();
ScopeOutput? leftValue = null, rightValue = null;
NoIncidents(await runner.RunAsync(new Sequence
{
    Variables = { left, right },
    Activities =
    {
        new Fork
        {
            JoinMode = ForkJoinMode.WaitAll,
            Branches =
            {
                new ScopeActivity { RequestId = new("left"), Result = new(left) },
                new ScopeActivity { RequestId = new("right"), Result = new(right) }
            }
        },
        new WriteLine(ctx => { leftValue = left.Get(ctx); rightValue = right.Get(ctx); return "Both branches finished."; })
    }
}));
Check(leftValue?.RequestId == "left" && rightValue?.RequestId == "right", "Fork branches keep separate task outputs");

// 9. Simulate lost response, then explicitly retry the same business request.
var payment = new PaymentActivity { RequestId = new("PAY-1") };
Check(Messages(await runner.RunAsync(payment)).Contains("Simulated response loss"), "Post-write failure reaches Elsa");
var retry = new PaymentActivity { RequestId = new("PAY-1") };
var receipt = await Observe(retry, retry.Result);
Check(receipt.Receipt == "receipt-PAY-1" && services.GetRequiredService<FakeLedger>().Writes == 1,
    "Explicit retry with the same key does not duplicate the fake write");

// 10. Generator rejects ambiguous contracts instead of emitting broken adapters.
ExpectGeneratorError(typeof(AmbiguousTask), "exactly one public constructor");
ExpectGeneratorError(typeof(MultipleContractsTask), "exactly one IAppTask");
ExpectGeneratorError(typeof(ReservedTask), "conflicts with an activity property");
var source = AdapterGenerator.Generate(typeof(ProbeTask).Assembly.GetTypes(), "Demo.Validation.Generated");
Check(source.Contains("Input<global::System.String?> @Note"), "Generator preserves nullable reference input annotations");
Check(source == AdapterGenerator.Generate(typeof(ProbeTask).Assembly.GetTypes().Reverse(), "Demo.Validation.Generated"), "Generation is deterministic");
Console.WriteLine($"RUNTIME/GENERATOR: {checks} checks passed.");

// Helpers keep the test cases above readable.
async Task<T> Observe<T>(IActivity activity, Output<T> output)
{
    T? value = default;
    var variable = new Variable<T>();
    activity.GetType().GetProperty("Result")!.SetValue(activity, new Output<T>(variable));
    var sequence = new Sequence { Variables = { variable }, Activities = { activity, new WriteLine(ctx => { value = variable.Get(ctx); return ""; }) } };
    NoIncidents(await runner.RunAsync(sequence));
    return value!;
}
CreateInvoiceActivity InvoiceActivity(Output<Invoice>? output = null) => new()
{
    Customer = new(new Customer("C-42", "buyer@example.test", new("Bengaluru", "560001"))),
    Lines = new(new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 1, 500m) }),
    Tax = new(new TaxRules(0.18m, "INR")), Result = output ?? new()
};
List<Package> Packages() => [new("Box A", 2.5m, new(40, 30, 20), ["KEYBOARD"]), new("Box B", 0.5m, new(15, 10, 8), ["MOUSE"])];
string Messages(RunWorkflowResult result) => string.Join("; ", result.WorkflowState.Incidents.Select(x => x.Exception?.Message + "\n" + x.Exception?.StackTrace));
void NoIncidents(RunWorkflowResult result) { if (result.WorkflowState.Incidents.Count > 0) throw new Exception(Messages(result)); }
void Check(bool passed, string description)
{
    if (!passed) throw new Exception("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
void ExpectGeneratorError(Type type, string text)
{
    try { AdapterGenerator.Generate([type]); throw new Exception("Generator accepted an invalid contract."); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains(text), "Generator rejects " + type.Name); }
}

// Invalid contracts live in this test assembly only; they are never deployed as tasks.
public class AmbiguousInput { public AmbiguousInput() {} public AmbiguousInput(int value) {} }
public class AmbiguousTask : IAppTask<AmbiguousInput, int> { public Task<int> ExecuteAsync(AmbiguousInput i, CancellationToken c) => Task.FromResult(0); }
public class MultipleContractsTask : IAppTask<QuoteV1Input, int>, IAppTask<QuoteV2Input, int>
{
    public Task<int> ExecuteAsync(QuoteV1Input i, CancellationToken c) => Task.FromResult(0);
    public Task<int> ExecuteAsync(QuoteV2Input i, CancellationToken c) => Task.FromResult(0);
}
public record ReservedInput(string Result);
public class ReservedTask : IAppTask<ReservedInput, int> { public Task<int> ExecuteAsync(ReservedInput i, CancellationToken c) => Task.FromResult(0); }
