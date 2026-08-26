using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using MaiImageGenerator.Web;
using MaiImageGenerator.Web.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
	?? throw new InvalidOperationException("ApiBaseUrl is required.");
var apiScope = builder.Configuration["AzureAd:DefaultAccessTokenScopes:0"]
	?? throw new InvalidOperationException("The API access scope is required.");

builder.Services.AddMsalAuthentication(options =>
{
	builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
	options.ProviderOptions.DefaultAccessTokenScopes.Add(apiScope);
});
builder.Services.AddHttpClient("Bff", client =>
{
	client.BaseAddress = new Uri(apiBaseUrl);
	client.Timeout = TimeSpan.FromMinutes(10);
}).AddHttpMessageHandler(sp => sp
	.GetRequiredService<AuthorizationMessageHandler>()
	.ConfigureHandler([apiBaseUrl], [apiScope]));
builder.Services.AddScoped(sp => new GenerationApiClient(
	sp.GetRequiredService<IHttpClientFactory>().CreateClient("Bff")));
builder.Services.AddRadzenComponents();

await builder.Build().RunAsync();
