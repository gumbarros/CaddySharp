using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace CaddySharp;

public sealed class CaddyServer : IServer
{
    private readonly BridgeHost _host;
    public CaddyServer() : this(BridgeHost.Current ?? throw new InvalidOperationException("No CaddySharp app is starting.")) { }

    internal CaddyServer(BridgeHost host) => _host = host;

    private Func<FeatureCollection, Task>? _process;
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public void Dispose()
    {
    }

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull
    {
        _process = async features =>
        {
            var context = application.CreateContext(features);
            Exception? error = null;
            try
            {
                await application.ProcessRequestAsync(context);
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                application.DisposeContext(context, error);
            }
        };
        _host.Started(this);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal Task Process(FeatureCollection features) =>
        (_process ?? throw new InvalidOperationException("ASP.NET not started"))(features);
}