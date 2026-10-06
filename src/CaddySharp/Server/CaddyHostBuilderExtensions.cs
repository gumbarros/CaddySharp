using Microsoft.Extensions.Hosting;

namespace CaddySharp;

public static class CaddyHostBuilderExtensions
{
    public static IHostBuilder UseCaddyServer(this IHostBuilder builder)
    {
        if (AppContext.GetData(CaddyServerRegistration.AppContextKey) is Action<IHostBuilder> configure)
            configure(builder);
        return builder;
    }
}
