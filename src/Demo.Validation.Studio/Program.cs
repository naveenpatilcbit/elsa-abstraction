using Elsa.Studio.Authentication.Abstractions.Models;
using Elsa.Studio.Authentication.UI.Extensions;
using Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm.Extensions;
using Elsa.Studio.Authentication.ElsaIdentity.HttpMessageHandlers;
using Elsa.Studio.Authentication.ElsaIdentity.UI.Extensions;
using Elsa.Studio.Contracts;
using Elsa.Studio.Core.BlazorWasm.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Localization.BlazorWasm.Extensions;
using Elsa.Studio.Localization.Models;
using Elsa.Studio.Models;
using Elsa.Studio.Shell;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Workflows.Designer.Extensions;
using Elsa.Studio.Workflows.Extensions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.RootComponents.RegisterCustomElsaStudioElements();
builder.Services.AddStudioAuthenticationMode(o => o.Provider = StudioAuthenticationProvider.ElsaIdentity);
builder.Services.AddElsaIdentity();
builder.Services.AddElsaIdentityUI();
builder.Services.AddAuthenticationUI();
builder.Services.AddCore();
builder.Services.AddShell();
builder.Services.AddRemoteBackend(new BackendApiConfig
{
    ConfigureBackendOptions = o => builder.Configuration.GetSection("Backend").Bind(o),
    ConfigureHttpClientBuilder = o => o.AuthenticationHandler = typeof(ElsaIdentityAuthenticatingApiHttpMessageHandler)
});
builder.Services.AddWorkflowsModule();
builder.Services.AddLocalizationModule(new LocalizationConfig
{
    ConfigureLocalizationOptions = o => builder.Configuration.GetSection("Localization").Bind(o)
});
var app = builder.Build();
await app.UseElsaLocalization();
await app.Services.GetRequiredService<IStartupTaskRunner>().RunStartupTasksAsync();
await app.RunAsync();
