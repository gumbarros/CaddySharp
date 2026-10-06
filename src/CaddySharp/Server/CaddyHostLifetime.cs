using Microsoft.Extensions.Hosting;

namespace CaddySharp;

internal sealed class CaddyHostLifetime(IHostApplicationLifetime lifetime) : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) { BridgeHost.Lifetime = lifetime; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}