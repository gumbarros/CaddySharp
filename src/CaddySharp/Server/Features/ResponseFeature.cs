using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CaddySharp;

internal sealed class ResponseFeature(RequestState state, long maxBody) : IHttpResponseFeature, IHttpResponseBodyFeature
{
    private readonly List<(Func<object, Task>, object)> _starting = [];
    private readonly List<(Func<object, Task>, object)> _completed = [];

    public int StatusCode
    {
        get => state.Status;
        set => state.Status = value;
    }

    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

    public Stream Body
    {
        get => Stream;
        set => throw new NotSupportedException();
    }

    public bool HasStarted { get; private set; }

    public Stream Stream { get; } = new LimitedStream(state.Output, maxBody);
    private System.IO.Pipelines.PipeWriter? _writer;
    public System.IO.Pipelines.PipeWriter Writer => _writer ??= System.IO.Pipelines.PipeWriter.Create(Stream);

    public void OnStarting(Func<object, Task> callback, object callbackState) =>
        _starting.Add((callback, callbackState));

    public void OnCompleted(Func<object, Task> callback, object callbackState) =>
        _completed.Add((callback, callbackState));

    public void DisableBuffering()
    {
    }

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("sendfile");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (HasStarted) return;
        for (var i = _starting.Count - 1; i >= 0; i--)
            await _starting[i].Item1(_starting[i].Item2);
        HasStarted = true;
    }

    public async Task CompleteAsync()
    {
        await StartAsync();
        if (_writer != null) 
            await _writer.FlushAsync();
        state.Headers = Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task RunCompletedAsync()
    {
        for (var i = _completed.Count - 1; i >= 0; i--) 
            await _completed[i].Item1(_completed[i].Item2);
    }
}