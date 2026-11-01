using Demo.Tasks;
using Demo.DynamicElsa;
using Elsa.Workflows.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

// 1. Register Elsa and connect each task contract to its business code.
var checks = 0;
var services = new ServiceCollection();
services.AddElsa();
services.AddSingleton<TaskCatalog>();
services.AddActivityProvider<TaskActivityProvider>();
services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();

// 2. Load task descriptions into Elsa and show the available inputs.
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
var catalog = scope.ServiceProvider.GetRequiredService<TaskCatalog>();
var registry = scope.ServiceProvider.GetRequiredService<IActivityRegistry>();
await registry.RefreshDescriptorsAsync(scope.ServiceProvider.GetRequiredService<TaskActivityProvider>());
var invoiceDefinition = catalog.Find("Demo.Tasks.CreateInvoice", 1);
var shipmentDefinition = catalog.Find("Demo.Tasks.PlanShipment", 1);
Console.WriteLine("ACTIVITIES PROVIDED AT RUNTIME — one shared activity class, no generated adapters");
foreach (var descriptor in registry.ListByProvider(typeof(TaskActivityProvider)))
    Console.WriteLine($"{descriptor.DisplayName} v{descriptor.Version}: {string.Join(", ", descriptor.Inputs.Select(x => x.Name))} → {descriptor.Outputs.Single().Type.Name}");
Check(registry.Find(invoiceDefinition.TypeName, 1)!.Inputs.Count == 3 && registry.Find(shipmentDefinition.TypeName, 1)!.Inputs.Count == 4, "Distinct task descriptors have distinct typed inputs");

// 3. Create typed storage shared by the steps in this flow.
// These variables hold whole objects, including nested fields and lists.
var customer = new Variable<Customer>();
var lines = new Variable<List<Line>>();
var tax = new Variable<TaxRules>();
var invoice = new Variable<Invoice>();
var warehouse = new Variable<Warehouse>();
var packages = new Variable<List<Package>>();
var shipment = new Variable<Shipment>();
Invoice? actualInvoice = null;
Shipment? actualShipment = null;

// 4. Build the flow: earlier Elsa steps → invoice task → shipment task.
var workflow = new Sequence
{
    Variables = { customer, lines, tax, invoice, warehouse, packages, shipment },
    Activities =
    {
        // Built-in Elsa steps stand in for previous lookup/API steps.
        new SetVariable<Customer>(customer, new Customer("C-42", "buyer@example.test", new Address("Bengaluru", "560001"))),
        new SetVariable<List<Line>>(lines, new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 1, 500m) }),
        new SetVariable<TaxRules>(tax, new TaxRules(0.18m, "INR")),
        // Map each invoice input to an earlier variable; save the result in invoice.
        invoiceDefinition.Bind(new Dictionary<string, object>
        {
            ["Customer"] = new Input<Customer>(customer),
            ["Lines"] = new Input<List<Line>>(lines),
            ["Tax"] = new Input<TaxRules>(tax)
        }, new Output<Invoice>(invoice)),
        new WriteLine(context =>
        {
            actualInvoice = invoice.Get(context)!;
            return "SCENARIO 1 — invoice from three earlier Elsa steps\n" + JsonSerializer.Serialize(actualInvoice, new JsonSerializerOptions { WriteIndented = true });
        }),
        // Prepare the shipment sources. The invoice is already available above.
        new SetVariable<Warehouse>(warehouse, new Warehouse("WH-BLR", new Address("Bengaluru", "560048"))),
        new SetVariable<List<Package>>(packages, new List<Package>
        {
            new("Box A", 2.5m, new Dimensions(40, 30, 20), new List<string> { "KEYBOARD" }),
            new("Box B", 0.5m, new Dimensions(15, 10, 8), new List<string> { "MOUSE" })
        }),
        // Pass whole objects, then build Rules from a fixed limit and the invoice total.
        // This expression belongs to the input, so no extra mapping step is needed.
        shipmentDefinition.Bind(new Dictionary<string, object>
        {
            ["Invoice"] = new Input<Invoice>(invoice),
            ["Origin"] = new Input<Warehouse>(warehouse),
            ["Packages"] = new Input<List<Package>>(packages),
            ["Rules"] = new Input<DeliveryRules>(context => new DeliveryRules(10m, invoice.Get(context)!.Totals.GrandTotal > 4000m))
        }, new Output<Shipment>(shipment)),
        new WriteLine(context =>
        {
            actualShipment = shipment.Get(context)!;
            return "SCENARIO 2 — shipment using invoice + warehouse + package list\n" + JsonSerializer.Serialize(actualShipment, new JsonSerializerOptions { WriteIndented = true });
        })
    }
};
// 5. Run both tasks and check their results.
await runner.RunAsync(workflow);
Check(actualInvoice is { Totals.Subtotal: 3500m, Totals.Tax: 630m, Totals.GrandTotal: 4130m }, "Invoice totals and nested output");
Check(actualShipment is { TotalWeightKg: 3m, SignatureRequired: true, Destination.PostalCode: "560001" }, "Shipment receives prior nested output and computed rule");
Check(actualShipment!.Packages.Count == 2 && actualShipment.Packages[0].Size.Length == 40m, "Nested list and dimensions survive handover");

// 6. Check that invalid nested data stops the flow.
// Failure travels through the shared activity and Elsa; the next activity must not run.
var invalidLines = new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 0, 500m) };
var continued = false;
var invalidWorkflow = new Sequence
{
    Activities =
    {
        invoiceDefinition.Bind(new Dictionary<string, object>
        {
            ["Customer"] = new Input<Customer>(actualInvoice!.Customer),
            ["Lines"] = new Input<List<Line>>(invalidLines),
            ["Tax"] = new Input<TaxRules>(new TaxRules(0.18m, "INR"))
        }, new Output<Invoice>()),
        new WriteLine(() => { continued = true; return "ERROR: should not run"; })
    }
};
var failed = await runner.RunAsync(invalidWorkflow);
var failureJson = string.Join("; ", failed.WorkflowState.Incidents.Select(x => x.Exception?.Message));
Check(!continued && failureJson.Contains("Lines[1].Quantity"), "Elsa stops on invalid nested input and records its field path");
Console.WriteLine("REJECTED: Lines[1].Quantity must be greater than zero.");

// 7. Check business tasks on their own: results, invalid SKU, and cancellation.
// The same business task runs without an Elsa context.
var direct = await new CreateInvoiceTask().ExecuteAsync(new(actualInvoice.Customer, actualInvoice.Lines, new(0.18m, "INR")), CancellationToken.None);
Check(direct.Totals == actualInvoice.Totals, "Same task runs outside Elsa");
try
{
    await new PlanShipmentTask().ExecuteAsync(new(actualInvoice, new("WH-BLR", new("Bengaluru", "560048")),
        new() { new("Bad box", 1m, new(1, 1, 1), new() { "UNKNOWN" }) }, new(10m, false)), CancellationToken.None);
    throw new Exception("Unknown SKU should fail.");
}
catch (ArgumentException ex) { Check(ex.Message.Contains("Packages[0].Skus"), "Unknown nested SKU is rejected"); }
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await new CreateInvoiceTask().ExecuteAsync(new(actualInvoice.Customer, actualInvoice.Lines, new(0.18m, "INR")), cancelled.Token);
    throw new Exception("Cancelled task should not run.");
}
catch (OperationCanceledException) { Check(true, "Cancellation reaches the independent task"); }

// 8. Save a flow definition, reload it, and run it again in this process.
// Use fixed inputs here; this does not test saving C# lambda expressions.
// Exercise the actual Elsa serializer and descriptor constructor, not plain System.Text.Json.
var serializer = scope.ServiceProvider.GetRequiredService<IActivitySerializer>();
var savedInvoice = new Variable<Invoice>();
var literalActivity = invoiceDefinition.Bind(new Dictionary<string, object>
{
    ["Customer"] = new Input<Customer>(actualInvoice.Customer),
    ["Lines"] = new Input<List<Line>>(actualInvoice.Lines),
    ["Tax"] = new Input<TaxRules>(new TaxRules(0.18m, "INR"))
}, new Output<Invoice>(savedInvoice));
var literalSequence = new Sequence { Variables = { savedInvoice }, Activities = { literalActivity } };
await runner.RunAsync(literalSequence); // Elsa assigns activity and memory IDs before saving.
var json = serializer.Serialize(literalSequence);
var restoredSequence = (Sequence)serializer.Deserialize(json);
var restored = restoredSequence.Activities.Single();
Check(restored is DynamicTaskActivity && restored.Type == invoiceDefinition.TypeName && restored.Version == 1
    && restored.SyntheticProperties.Count == 4, "Elsa JSON reload restores task identity and synthetic bindings");
Invoice? restoredInvoice = null;
restoredSequence.Activities.Add(new WriteLine(context =>
{
    restoredInvoice = savedInvoice.Get(context);
    return "Reloaded activity executed.";
}));
var reloadRun = await runner.RunAsync(restoredSequence);
if (reloadRun.WorkflowState.Incidents.Count > 0)
    throw new Exception(string.Join("; ", reloadRun.WorkflowState.Incidents.Select(x => x.Exception?.Message)));
Check(restoredInvoice?.Totals.GrandTotal == 4130m && restoredInvoice.Customer.BillingAddress.PostalCode == "560001",
    "Reloaded activity executes with nested objects and lists");

// 9. Reject missing inputs, wrong input types, and unknown task versions.
ExpectArgumentError(() => invoiceDefinition.Bind(new Dictionary<string, object>(), new Output<Invoice>()), "Missing input: Customer");
ExpectArgumentError(() => invoiceDefinition.Bind(new Dictionary<string, object>
{
    ["Customer"] = new Input<string>("wrong type")
}, new Output<Invoice>()), "Customer requires");
try { catalog.Find("Demo.Tasks.CreateInvoice", 99); throw new Exception("Unknown version should fail."); }
catch (InvalidOperationException ex) { Check(ex.Message.Contains("Unknown task"), "Unknown task version is rejected"); }
Console.WriteLine($"ALL {checks} CHECKS PASSED");

// Helpers: fail the demo immediately if a check does not behave as expected.
void ExpectArgumentError(Action action, string expected)
{
    try { action(); throw new Exception("Invalid binding should fail."); }
    catch (ArgumentException ex) { Check(ex.Message.Contains(expected), "Binding rejected: " + expected); }
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
