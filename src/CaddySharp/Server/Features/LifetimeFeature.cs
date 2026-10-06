using Microsoft.AspNetCore.Http.Features;

namespace CaddySharp;

internal sealed class LifetimeFeature(CancellationTokenSource cancellation) : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted
    {
        get => cancellation.Token;
        set
        {
            
        }
    }

    public void Abort() => cancellation.Cancel();
}