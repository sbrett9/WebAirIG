using System.Globalization;
using Radzen;
using WebAirIG.Consumer.Components;
using WebAirIG.Consumer.Options;
using WebAirIG.Consumer.Services;
using WebAirIG.Consumer.Services.Detection;
using WebAirIG.Consumer.ViewModels;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
// Serve framework/package static assets (blazor.web.js, Radzen theme + script) when running the build output in any
// environment, not just Development. No effect on published output, where the assets are copied into wwwroot.
builder.WebHost.UseStaticWebAssets();

builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection(ConsumerOptions.Section));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// Pipeline services (singletons): receive -> decode -> detect/track/evaluate, record, preview.
builder.Services.AddSingleton<StreamReceiverService>();
builder.Services.AddSingleton<IStreamReceiverService>(sp => sp.GetRequiredService<StreamReceiverService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamReceiverService>());
builder.Services.AddSingleton<CameraPipelineService>();
builder.Services.AddSingleton<ICameraPipelineService>(sp => sp.GetRequiredService<CameraPipelineService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraPipelineService>());
builder.Services.AddSingleton<IEvaluationService, EvaluationService>();
builder.Services.AddSingleton<DetectorService>();
builder.Services.AddSingleton<IDetectorService>(sp => sp.GetRequiredService<DetectorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DetectorService>());
builder.Services.AddSingleton<ClassCatalogService>();
builder.Services.AddSingleton<IDatasetRecorderService, DatasetRecorderService>();
builder.Services.AddSingleton<PreviewRenderer>();

// ViewModels: one per Blazor circuit.
builder.Services.AddScoped<WebAirIG.Web.Mvvm.ShellViewModel>();
builder.Services.AddScoped<LiveViewModel>();
builder.Services.AddScoped<DetectorViewModel>();
builder.Services.AddScoped<RecorderViewModel>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// The recorder must subscribe to decoded frames even before anyone opens its page.
app.Services.GetRequiredService<IDatasetRecorderService>();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapPreviewEndpoints();
app.MapConsumerApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
