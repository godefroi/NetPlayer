using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using SongServer;
using SongServer.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.Configure<PlayerClientOptions>(builder.Configuration.GetSection(PlayerClientOptions.SectionName));

//builder.Services.AddHttpClient(nameof(PlayerClient), (sp, hc) => hc.BaseAddress = sp.GetRequiredService<IOptions<PlayerClientOptions>>().Value.ServerUri);
//builder.Services.AddHttpClient<PlayerClient>();

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddHttpClient<PlayerClient>(hc => hc.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
