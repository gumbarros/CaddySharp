using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace CaddySharp;

internal sealed class ConnectionFeature : IHttpConnectionFeature
{
    public ConnectionFeature(string remote)
    {
        var address = remote;
        var port = 0;
        if (IPEndPoint.TryParse(remote, out var endpoint))
        {
            address = endpoint.Address.ToString();
            port = endpoint.Port;
        }

        IPAddress.TryParse(address, out var ip);
        RemoteIpAddress = ip;
        RemotePort = port;
    }

    public string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");
    public IPAddress? LocalIpAddress { get; set; }
    public IPAddress? RemoteIpAddress { get; set; }
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }
}