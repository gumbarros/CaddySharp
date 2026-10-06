using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaddySharp;

internal static class CaddyServerRegistration
{
    internal const string AppContextKey = "CaddySharp.ConfigureHost";

    internal static void Configure(IHostBuilder builder)
    {
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<IHostLifetime, CaddyHostLifetime>();
            services.AddSingleton<CaddyServer>();
            services.AddSingleton<IServer>(provider => provider.GetRequiredService<CaddyServer>());
        });
    }
}
