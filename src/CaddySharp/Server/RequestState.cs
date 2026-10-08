using Microsoft.Extensions.Primitives;
using System.IO.Pipelines;

namespace CaddySharp;

internal sealed class RequestState
{
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly Pipe Input = new(new PipeOptions(pauseWriterThreshold: 65536, resumeWriterThreshold: 32768));
    internal readonly Pipe Output = new(new PipeOptions(pauseWriterThreshold: 65536, resumeWriterThreshold: 32768));
    internal Task? Task;
    internal ResponseFeature? Response;
    internal int Status = 200;
    internal Dictionary<string, StringValues> Headers = new(StringComparer.OrdinalIgnoreCase);
    internal Exception? Error;
    internal byte[]? EncodedHeaders;
    internal volatile bool Started;
    internal volatile bool Finished;
    internal long ResponseBytes;
}
