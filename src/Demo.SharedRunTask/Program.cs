using System.Text.Json;
using Demo.Tasks;
using Demo.TaskRunner;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Microsoft.Extensions.DependencyInjection;
using RunTask = Demo.SharedRunTask.RunTask;

var services = new ServiceCollection();
services.AddElsa(elsa => elsa.AddActivity<RunTask>());
services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();
services.AddScoped(sp =>
{
    var registry = new TaskRegistry();
    registry.Register("CreateInvoice.v1", sp.GetRequiredService<IAppTask<InvoiceInput, Invoice>>());
    registry.Register("PlanShipment.v1", sp.GetRequiredService<IAppTask<ShipmentInput, Shipment>>());
    return registry;
});
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
var registry = scope.ServiceProvider.GetRequiredService<TaskRegistry>();
var checks = 0;

var customer = new Variable<Customer>();
var lines = new Variable<List<Line>>();
var tax = new Variable<TaxRules>();
var warehouse = new Variable<Warehouse>();
var packages = new Variable<List<Package>>();
// Elsa sees JSON outputs, not Invoice or Shipment.
var invoice = new Variable<JsonElement>();
var shipment = new Variable<JsonElement>();
JsonElement invoiceResult = default, shipmentResult = default;
var workflow = new Sequence
{
    Variables = { customer, lines, tax, warehouse, packages, invoice, shipment },
    Activities =
    {
        new SetVariable<Customer>(customer, new Customer("C-42", "buyer@example.test", new Address("Bengaluru", "560001"))),
        new SetVariable<List<Line>>(lines, new List<Line> { new("KEYBOARD", 2, 1500m), new("MOUSE", 1, 500m) }),
        new SetVariable<TaxRules>(tax, new TaxRules(0.18m, "INR")),
        new RunTask
        {
            TaskName = new("CreateInvoice.v1"),
            // All three sources are mapped HERE, inside the activity's Payload input.
            Payload = new(context => Json(new
            {
                customer = customer.Get(context),
                lines = lines.Get(context),
                tax = tax.Get(context)
            })),
            Result = new(invoice)
        },
        new WriteLine(context =>
        {
            invoiceResult = invoice.Get(context);
            return "SCENARIO 1: RunTask(CreateInvoice.v1)\n" + Pretty(invoiceResult);
        }),
        new SetVariable<Warehouse>(warehouse, new Warehouse("WH-BLR", new Address("Bengaluru", "560048"))),
        new SetVariable<List<Package>>(packages, new List<Package>
        {
            new("Box A", 2.5m, new Dimensions(40, 30, 20), new List<string> { "KEYBOARD" }),
            new("Box B", 0.5m, new Dimensions(15, 10, 8), new List<string> { "MOUSE" })
        }),
        new RunTask
        {
            TaskName = new("PlanShipment.v1"),
            Payload = new(context => Json(new
            {
                invoice = invoice.Get(context), // Full JSON object from the previous RunTask.
                origin = warehouse.Get(context),
                packages = packages.Get(context),
                rules = new
                {
                    maxWeightKg = 10m,
                    signatureRequired = invoice.Get(context).GetProperty("totals").GetProperty("grandTotal").GetDecimal() > 4000m
                }
            })),
            Result = new(shipment)
        },
        new WriteLine(context =>
        {
            shipmentResult = shipment.Get(context);
            return "SCENARIO 2: RunTask(PlanShipment.v1)\n" + Pretty(shipmentResult);
        })
    }
};
var success = await runner.RunAsync(workflow);
Check(!success.WorkflowState.Incidents.Any(), "Both tasks run through the same activity without incidents");
var typedInvoice = invoiceResult.Deserialize<Invoice>(TaskRegistry.JsonOptions)!;
var typedShipment = shipmentResult.Deserialize<Shipment>(TaskRegistry.JsonOptions)!;
Check(typedInvoice.Totals.GrandTotal == 4130m, "Invoice total is 4130");
Check(typedShipment.TotalWeightKg == 3m && typedShipment.SignatureRequired, "Previous JSON output drives shipment rules");
Check(typedShipment.Destination.PostalCode == "560001" && typedShipment.Packages[0].Size.Length == 40m,
    "Nested address, package list, and dimensions survive JSON handover");

// Every failure is exercised THROUGH ELSA, including stopping the next activity.
var validPayload = Json(new InvoiceInput(typedInvoice.Customer, typedInvoice.Lines, new(0.18m, "INR")));
await Reject("Unknown task", "Missing.v1", validPayload, "Unknown task");
await Reject("Missing field", "CreateInvoice.v1", Json(new { customer = typedInvoice.Customer, lines = typedInvoice.Lines }), "tax");
await Reject("Wrong nested type", "CreateInvoice.v1", Json(new
{
    customer = typedInvoice.Customer,
    lines = new[] { new { sku = "MOUSE", quantity = "many", unitPrice = 500m } },
    tax = new TaxRules(0.18m, "INR")
}), "$.lines[0].quantity");
await Reject("Invalid business value", "CreateInvoice.v1", Json(new InvoiceInput(typedInvoice.Customer,
    new() { new("MOUSE", 0, 500m) }, new(0.18m, "INR"))), "Lines[0].Quantity");
await Reject("Null nested object", "CreateInvoice.v1", Json(new
{
    customer = new { id = "C-42", email = "buyer@example.test", billingAddress = (object?)null },
    lines = typedInvoice.Lines, tax = new TaxRules(0.18m, "INR")
}), "billingAddress");
await Reject("Unexpected field", "CreateInvoice.v1", Json(new
{
    customer = typedInvoice.Customer, lines = typedInvoice.Lines, tax = new TaxRules(0.18m, "INR"), typo = true
}), "typo");

try
{
    registry.Register("CreateInvoice.v1", new CreateInvoiceTask());
    throw new Exception("Duplicate registration should fail.");
}
catch (InvalidOperationException) { Check(true, "Duplicate task names rejected"); }
using var cts = new CancellationTokenSource();
cts.Cancel();
try
{
    await registry.RunAsync("CreateInvoice.v1", validPayload, cts.Token);
    throw new Exception("Cancellation should stop execution.");
}
catch (OperationCanceledException) { Check(true, "Cancelled call rejected before dispatch"); }
Console.WriteLine($"ALL {checks} CHECKS PASSED");

async Task Reject(string label, string taskName, JsonElement payload, string expected)
{
    var continued = false;
    var result = await runner.RunAsync(new Sequence
    {
        Activities =
        {
            new RunTask { TaskName = new(taskName), Payload = new(payload) },
            new WriteLine(() => { continued = true; return "ERROR: should not run"; })
        }
    });
    var errors = string.Join("; ", result.WorkflowState.Incidents.Select(x => x.Exception?.Message));
    Check(!continued && errors.Contains(expected, StringComparison.OrdinalIgnoreCase), label + ": flow stops with a useful error");
    Console.WriteLine("  " + errors.Split('\n')[0]);
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, TaskRegistry.JsonOptions);
static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
