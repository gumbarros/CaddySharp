using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaddySharp;

internal static class CaddyServerRegistration
{
    internal const string AppContextKey = "CaddySharp.ConfigureHost";

    internal static void Configure(IHostBuilder builder)
    {
        var host = BridgeHost.Current;
        if (host is null) return;
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(host);
            services.AddSingleton<IHostLifetime, CaddyHostLifetime>();
            services.AddSingleton(new CaddyServer(host));
            services.AddSingleton<IServer>(provider => provider.GetRequiredService<CaddyServer>());
        });
    }
}
