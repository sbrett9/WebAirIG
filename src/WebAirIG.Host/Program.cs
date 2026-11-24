using System.Globalization;
using System.Text.Json.Serialization;
using Radzen;
using WebAirIG.Host.Api;
using WebAirIG.Host.Components;
using WebAirIG.Host.Options;
using WebAirIG.Host.Services;
using WebAirIG.Host.ViewModels;

// SVG coordinates and numeric inputs must render with '.' decimals regardless of OS locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
// Serve framework/package static assets (blazor.web.js, Radzen theme + script) when running the build output in any
// environment, not just Development. No effect on published output, where the assets are copied into wwwroot.
builder.WebHost.UseStaticWebAssets();

builder.Services.Configure<WebAirIgOptions>(builder.Configuration.GetSection(WebAirIgOptions.Section));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Domain services (singletons shared by every browser session).
builder.Services.AddSingleton<ISharedConfigService, SharedConfigService>();
builder.Services.AddSingleton<IScenarioService, ScenarioService>();
builder.Services.AddSingleton<ISensorControlService, SensorControlService>();
builder.Services.AddSingleton<ISimulationEngine, SimulationEngine>();
builder.Services.AddSingleton<CigiHostService>();
builder.Services.AddSingleton<IIgLinkStatus>(sp => sp.GetRequiredService<CigiHostService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CigiHostService>());
builder.Services.AddSingleton<DisPublisherService>();
builder.Services.AddSingleton<IDisStatus>(sp => sp.GetRequiredService<DisPublisherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DisPublisherService>());

// ViewModels: one instance per Blazor circuit (browser tab).
builder.Services.AddScoped<WebAirIG.Web.Mvvm.ShellViewModel>();
builder.Services.AddScoped<ControlViewModel>();
builder.Services.AddScoped<ScenarioViewModel>();
builder.Services.AddScoped<StatusViewModel>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHostApi();

app.Run();
