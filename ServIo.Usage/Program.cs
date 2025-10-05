using PowNet.Configuration; // added for PowNetConfiguration paths

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional:true, reloadOnChange:true);

builder.Services.AddSingleton<ServIo.DynamicActionDescriptor>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorChangeProvider>(sp => sp.GetRequiredService<ServIo.DynamicActionDescriptor>());

builder.Services.AddControllers();

var app = builder.Build();

// initialize plugin manager references
ServIo.PluginManager.AppPartManager = app.Services.GetRequiredService<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager>();
ServIo.PluginManager.AppActionDescriptor = app.Services.GetRequiredService<ServIo.DynamicActionDescriptor>();

// Reusable dynamic server script compilation & load
ServIo.DynamicServerBootstrap.EnsureDynamicServerScriptsLoaded();

app.MapGet("/", () => "ServIo Usage Dynamic Runtime Demo");
app.MapControllers();

app.Run();
