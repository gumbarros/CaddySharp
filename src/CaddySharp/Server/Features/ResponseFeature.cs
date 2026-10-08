using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CaddySharp;

internal sealed class ResponseFeature(RequestState state, long maxBody) : IHttpResponseFeature, IHttpResponseBodyFeature
{
    private readonly List<(Func<object, Task>, object)> _starting = [];
    private readonly List<(Func<object, Task>, object)> _completed = [];
    private readonly Stream _stream = new ResponseStream(state, maxBody);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private PipeWriter? _writer;

    public int StatusCode { get => state.Status; set => state.Status = value; }
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get => Stream; set => throw new NotSupportedException(); }
    public bool HasStarted => state.Started;
    public Stream Stream => _stream;
    public PipeWriter Writer => _writer ??= PipeWriter.Create(Stream, new StreamPipeWriterOptions(leaveOpen: true));

    public void OnStarting(Func<object, Task> callback, object callbackState) => _starting.Add((callback, callbackState));
    public void OnCompleted(Func<object, Task> callback, object callbackState) => _completed.Add((callback, callbackState));
    public void DisableBuffering() { }
    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        SendFileCoreAsync(path, offset, count, cancellationToken);

    private async Task SendFileCoreAsync(string path, long offset, long? count, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        file.Seek(offset, SeekOrigin.Begin);
        if (count is long length)
        {
            var buffer = new byte[32768];
            while (length > 0)
            {
                int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken);
                if (n == 0) break;
                await Stream.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                length -= n;
            }
        }
        else await file.CopyToAsync(Stream, cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (state.Started) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (state.Started) return;
            for (int i = _starting.Count - 1; i >= 0; i--)
                await _starting[i].Item1(_starting[i].Item2);
            state.Headers = Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            state.Started = true;
        }
        finally { _startLock.Release(); }
    }

    public async Task CompleteAsync()
    {
        if (_writer != null) await _writer.FlushAsync(state.Cancellation.Token);
        await StartAsync();
        await state.Output.Writer.CompleteAsync();
        state.Finished = true;
    }

    public async Task RunCompletedAsync()
    {
        for (int i = _completed.Count - 1; i >= 0; i--)
            await _completed[i].Item1(_completed[i].Item2);
    }

    private sealed class ResponseStream(RequestState state, long maxBody) : Stream
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cancellation.Token);
            await _writeLock.WaitAsync(linked.Token);
            try
            {
                if (maxBody > 0 && state.ResponseBytes + buffer.Length > maxBody)
                    throw new InvalidOperationException("response body limit exceeded");
                await state.Response!.StartAsync(linked.Token);
                while (!buffer.IsEmpty)
                {
                    var chunk = buffer[..Math.Min(buffer.Length, 32768)];
                    await state.Output.Writer.WriteAsync(chunk, linked.Token);
                    state.ResponseBytes += chunk.Length;
                    buffer = buffer[chunk.Length..];
                }
            }
            finally { _writeLock.Release(); }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
