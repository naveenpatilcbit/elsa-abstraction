using Demo.Tasks;
using Demo.Elsa.Generated;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

var services = new ServiceCollection();
services.AddElsa(elsa => elsa.AddActivitiesFrom<CreateInvoiceActivity>());
services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

var customer = new Variable<Customer>();
var lines = new Variable<List<Line>>();
var tax = new Variable<TaxRules>();
var invoice = new Variable<Invoice>();
var warehouse = new Variable<Warehouse>();
var packages = new Variable<List<Package>>();
var shipment = new Variable<Shipment>();
Invoice? actualInvoice = null;
Shipment? actualShipment = null;
var workflow = new Sequence
{
    Variables = { customer, lines, tax, invoice, warehouse, packages, shipment },
    Activities =
    {
        // Built-in Elsa steps stand in for previous lookup/API steps.
        new SetVariable<Customer>(customer, new Customer("C-42", "buyer@example.test", new Address("Bengaluru", "560001"))),
        new SetVariable<List<Line>>(lines, new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 1, 500m) }),
        new SetVariable<TaxRules>(tax, new TaxRules(0.18m, "INR")),
        new CreateInvoiceActivity
        {
            Customer = new(customer), Lines = new(lines), Tax = new(tax), Result = new(invoice)
        },
        new WriteLine(context =>
        {
            actualInvoice = invoice.Get(context)!;
            return "SCENARIO 1 — invoice from three earlier Elsa steps\n" + JsonSerializer.Serialize(actualInvoice, new JsonSerializerOptions { WriteIndented = true });
        }),
        new SetVariable<Warehouse>(warehouse, new Warehouse("WH-BLR", new Address("Bengaluru", "560048"))),
        new SetVariable<List<Package>>(packages, new List<Package>
        {
            new("Box A", 2.5m, new Dimensions(40, 30, 20), new List<string> { "KEYBOARD" }),
            new("Box B", 0.5m, new Dimensions(15, 10, 8), new List<string> { "MOUSE" })
        }),
        new PlanShipmentActivity
        {
            Invoice = new(invoice),
            Origin = new(warehouse),
            Packages = new(packages),
            // Mapping lives on the activity input: earlier nested output + fixed settings.
            Rules = new(context => new DeliveryRules(10m, invoice.Get(context)!.Totals.GrandTotal > 4000m)),
            Result = new(shipment)
        },
        new WriteLine(context =>
        {
            actualShipment = shipment.Get(context)!;
            return "SCENARIO 2 — shipment using invoice + warehouse + package list\n" + JsonSerializer.Serialize(actualShipment, new JsonSerializerOptions { WriteIndented = true });
        })
    }
};
await runner.RunAsync(workflow);
Check(actualInvoice is { Totals.Subtotal: 3500m, Totals.Tax: 630m, Totals.GrandTotal: 4130m }, "Invoice totals and nested output");
Check(actualShipment is { TotalWeightKg: 3m, SignatureRequired: true, Destination.PostalCode: "560001" }, "Shipment receives prior nested output and computed rule");
Check(actualShipment!.Packages.Count == 2 && actualShipment.Packages[0].Size.Length == 40m, "Nested list and dimensions survive handover");

// Failure travels through the generated adapter and Elsa; the next activity must not run.
var invalidLines = new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 0, 500m) };
var continued = false;
var invalidWorkflow = new Sequence
{
    Activities =
    {
        new CreateInvoiceActivity
        {
            Customer = new(actualInvoice!.Customer), Lines = new(invalidLines), Tax = new(new TaxRules(0.18m, "INR"))
        },
        new WriteLine(() => { continued = true; return "ERROR: should not run"; })
    }
};
var failed = await runner.RunAsync(invalidWorkflow);
var failureJson = string.Join("; ", failed.WorkflowState.Incidents.Select(x => x.Exception?.Message));
Check(!continued && failureJson.Contains("Lines[1].Quantity"), "Elsa stops on invalid nested input and records its field path");
Console.WriteLine("REJECTED: Lines[1].Quantity must be greater than zero.");

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
Console.WriteLine("ALL 7 CHECKS PASSED");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
