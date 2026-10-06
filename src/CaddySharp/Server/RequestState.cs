using Microsoft.Extensions.Primitives;

namespace CaddySharp;

internal sealed class RequestState
{
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly MemoryStream Output = new();
    internal Task? Task;
    internal ResponseFeature? Response;
    internal int Status = 200;
    internal Dictionary<string, StringValues> Headers = new(StringComparer.OrdinalIgnoreCase);
    internal Exception? Error;
    internal byte[]? EncodedHeaders;
}