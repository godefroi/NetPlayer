using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using SongServer;
using SongServer.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.Configure<PlayerClientOptions>(builder.Configuration.GetSection(PlayerClientOptions.SectionName));

builder.Services.PostConfigure<PlayerClientOptions>(options =>
{
	if (options.ServerUri.ToString() == "about:blank") {
		options.ServerUri = new Uri(builder.HostEnvironment.BaseAddress);
	}
});

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddHttpClient<PlayerClient>((sp, hc) => hc.BaseAddress = sp.GetRequiredService<IOptions<PlayerClientOptions>>().Value.ServerUri);

builder.Services.AddSingleton<HubConnectionService>();

await builder.Build().RunAsync();
