using Microsoft.Extensions.Hosting;

namespace CaddySharp;

internal sealed class CaddyHostLifetime(IHostApplicationLifetime lifetime, BridgeHost host) : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) { host.Lifetime = lifetime; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}