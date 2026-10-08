using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CaddySharp;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AbiBytes
{
    public byte* Data;
    public int Length;
}

[StructLayout(LayoutKind.Sequential)]
public struct AbiHeader
{
    public AbiBytes Name;
    public AbiBytes Value;
}

public static unsafe class Exports
{
    private static string S(AbiBytes value) =>
        value.Length == 0 ? "" : Encoding.UTF8.GetString(value.Data, value.Length);

    private static RequestState State(nint handle) => (RequestState)GCHandle.FromIntPtr(handle).Target!;

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_probe")]
    public static int Probe() => 0x43534801;

    [DNNE.C99DeclCode(
        "typedef struct { unsigned char* data; int32_t length; } cs_bytes; typedef struct { cs_bytes name; cs_bytes value; } cs_header;")]
    [UnmanagedCallersOnly(EntryPoint = "caddysharp_init")]
    public static nint Init([DNNE.C99Type("cs_bytes")] AbiBytes assembly, [DNNE.C99Type("cs_bytes")] AbiBytes root,
        [DNNE.C99Type("cs_bytes")] AbiBytes environment, [DNNE.C99Type("cs_header*")] AbiHeader* config, int count)
    {
        try
        {
            var values = new Dictionary<string, string>();
            for (int i = 0; i < count; i++) values.Add(S(config[i].Name), S(config[i].Value));
            return BridgeHost.Init(S(assembly), S(root), S(environment), values);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_start")]
    public static nint Start(nint app, [DNNE.C99Type("cs_bytes")] AbiBytes method, [DNNE.C99Type("cs_bytes")] AbiBytes scheme,
        [DNNE.C99Type("cs_bytes")] AbiBytes host, [DNNE.C99Type("cs_bytes")] AbiBytes path,
        [DNNE.C99Type("cs_bytes")] AbiBytes rawTarget, [DNNE.C99Type("cs_bytes")] AbiBytes query,
        [DNNE.C99Type("cs_bytes")] AbiBytes remote,
        [DNNE.C99Type("cs_header*")] AbiHeader* headers, int count, long maxResponse)
    {
        try
        {
            var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var key = S(headers[i].Name);
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                list.Add(S(headers[i].Value));
            }

            var state = BridgeHost.Get(app).Start(S(method), S(scheme), S(host), S(path), S(rawTarget), S(query), S(remote),
                values, maxResponse);
            return GCHandle.ToIntPtr(GCHandle.Alloc(state));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_wait")]
    public static int Wait(nint handle)
    {
        try
        {
            State(handle).Task!.GetAwaiter().GetResult();
            return State(handle).Error is null ? 0 : -1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
    }

    // Completion is intentionally exposed separately from Wait. The Go adapter
    // can poll this short, non-blocking ABI call while the managed request runs
    // instead of parking one native thread in GetResult for every request.
    [UnmanagedCallersOnly(EntryPoint = "caddysharp_is_completed")]
    public static int IsCompleted(nint handle)
    {
        try
        {
            return State(handle).Task?.IsCompleted == true ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_cancel")]
    public static void Cancel(nint handle)
    {
        try
        {
            State(handle).Cancellation.Cancel();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_request_write")]
    public static int RequestWrite(nint handle, byte* source, int length)
    {
        try
        {
            var state = State(handle);
            var bytes = new byte[length];
            Marshal.Copy((nint)source, bytes, 0, length);
            state.Input.Writer.WriteAsync(bytes, state.Cancellation.Token).AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_request_end")]
    public static void RequestEnd(nint handle)
    {
        try { State(handle).Input.Writer.Complete(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
    }

    // 0: pending; 1: headers available; 2: request finished without a response.
    [UnmanagedCallersOnly(EntryPoint = "caddysharp_response_state")]
    public static int ResponseState(nint handle)
    {
        var state = State(handle);
        return state.Started ? 1 : state.Task?.IsCompleted == true ? 2 : 0;
    }

    // 0: no chunk yet; -1: end of stream; positive: copied bytes.
    [UnmanagedCallersOnly(EntryPoint = "caddysharp_response_read")]
    public static int ResponseRead(nint handle, byte* target, int capacity)
    {
        try
        {
            var reader = State(handle).Output.Reader;
            if (!reader.TryRead(out var result)) return 0;
            var count = (int)Math.Min(result.Buffer.Length, capacity);
            if (count > 0)
            {
                result.Buffer.Slice(0, count).CopyTo(new Span<byte>(target, count));
                reader.AdvanceTo(result.Buffer.GetPosition(count));
                return count;
            }
            reader.AdvanceTo(result.Buffer.End);
            return result.IsCompleted ? -1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -2;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_status")]
    public static int Status(nint handle) => State(handle).Status;

    // Additive ABI v1 export. A null destination queries the exact byte count.
    // The completed response is immutable until Complete/Free; no managed pointer escapes.
    [UnmanagedCallersOnly(EntryPoint = "caddysharp_copy_headers")]
    public static int CopyHeaders(nint handle, byte* target, int capacity)
    {
        try
        {
            var state = State(handle);
            var bytes = state.EncodedHeaders ??= EncodeHeaders(state);
            if (target == null && capacity == 0) return bytes.Length;
            if (target == null || capacity < bytes.Length) return -1;
            bytes.AsSpan().CopyTo(new Span<byte>(target, capacity));
            return bytes.Length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
    }

    private static byte[] EncodeHeaders(RequestState state)
    {
        int length = 0;
        foreach (var pair in state.Headers)
        {
            int nameLength = Encoding.UTF8.GetByteCount(pair.Key);
            foreach (var value in pair.Value)
            {
                int valueLength = Encoding.UTF8.GetByteCount(value ?? "");
                // Preserve the original packed ABI's accepted header sizes.
                if (nameLength > short.MaxValue || valueLength > ushort.MaxValue)
                    throw new InvalidOperationException("response header too large");
                length = checked(length + 8 + nameLength + valueLength);
            }
        }

        var bytes = new byte[length];
        int offset = 0;
        foreach (var pair in state.Headers)
        {
            foreach (var value in pair.Value)
            {
                int nameLength = Encoding.UTF8.GetBytes(pair.Key, bytes.AsSpan(offset + 8));
                int valueLength = Encoding.UTF8.GetBytes(value ?? "", bytes.AsSpan(offset + 8 + nameLength));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), nameLength);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), valueLength);
                offset += 8 + nameLength + valueLength;
            }
        }

        return bytes;
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_header_count")]
    public static int HeaderCount(nint handle) => State(handle).Headers.Sum(x => x.Value.Count);

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_copy_header")]
    public static int CopyHeader(nint handle, int index, byte* name, int nameCapacity, byte* value, int valueCapacity)
    {
        try
        {
            var pair = State(handle).Headers.SelectMany(x => x.Value.Select(v => (x.Key, Value: v))).ElementAt(index);
            var nb = Encoding.UTF8.GetBytes(pair.Key);
            var vb = Encoding.UTF8.GetBytes(pair.Value ?? "");
            if (nb.Length > nameCapacity || vb.Length > valueCapacity) return -1;
            Marshal.Copy(nb, 0, (nint)name, nb.Length);
            Marshal.Copy(vb, 0, (nint)value, vb.Length);
            return (nb.Length << 16) | vb.Length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_complete")]
    public static int Complete(nint handle)
    {
        try
        {
            State(handle).Response!.RunCompletedAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_shutdown")]
    public static int Shutdown(nint app) => BridgeHost.Shutdown(app);

    [UnmanagedCallersOnly(EntryPoint = "caddysharp_free")]
    public static void Free(nint handle)
    {
        try
        {
            var gc = GCHandle.FromIntPtr(handle);
            var state = (RequestState)gc.Target!;
            if (state.Task?.IsCompleted != true) return;
            state.Cancellation.Dispose();
            state.Input.Reader.Complete();
            state.Output.Reader.Complete();
            gc.Free();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }
}
