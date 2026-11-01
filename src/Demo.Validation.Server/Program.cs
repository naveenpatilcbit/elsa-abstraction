using Demo.Tasks;
using Demo.Elsa.Generated;
using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

var builder = WebApplication.CreateBuilder(args);
// Local validation only: keep this host on loopback.
builder.WebHost.UseUrls("http://localhost:5210");
builder.Services.AddElsa(elsa =>
{
    elsa.UseWorkflowManagement(m => m.UseEntityFrameworkCore(ef => ef.UseSqlite()));
    elsa.UseWorkflowRuntime(r => r.UseEntityFrameworkCore(ef => ef.UseSqlite()));
    elsa.UseIdentity(identity =>
    {
        identity.TokenOptions = options => options.SigningKey = builder.Configuration["Validation:SigningKey"]
            ?? throw new InvalidOperationException("Set Validation__SigningKey for this local server.");
        identity.UseAdminUserProvider(admin =>
        {
            admin.UserName = "validation";
            admin.Password = builder.Configuration["Validation:Password"]
                ?? throw new InvalidOperationException("Set Validation__Password for this local server.");
        });
    });
    elsa.UseDefaultAuthentication();
    elsa.UseWorkflowsApi();
    elsa.UseRealTimeWorkflows();
    elsa.UseJavaScript();
    elsa.UseHttp();
    elsa.AddActivitiesFrom<CreateInvoiceActivity>();
});
builder.Services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
builder.Services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5211").AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("x-elsa-workflow-instance-id")));
var app = builder.Build();
app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkflowsApi();
app.UseWorkflows();
app.UseWorkflowsSignalRHubs();
app.Run();
