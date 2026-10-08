using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace CaddySharp;

internal sealed class BridgeHost
{
    private sealed class AppLoadContext(string assembly) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assembly);
        protected override Assembly? Load(AssemblyName name)
        {
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private static readonly AsyncLocal<BridgeHost?> Starting = new();
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, BridgeHost> Hosts = new();
    private static long _nextId;
    private static bool _registered;
    private AppLoadContext? _loadContext;
    private Task? _entryPointTask;
    private readonly TaskCompletionSource _serverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CaddyServer? Server;
    internal IHostApplicationLifetime? Lifetime;

    internal static BridgeHost? Current => Starting.Value;

    internal static nint Init(string assembly, string contentRoot, string environment, Dictionary<string, string> config)
    {
        var host = new BridgeHost();
        lock (Gate)
        {
            try
            {
                if (!_registered)
                {
                    AppContext.SetData(CaddyServerRegistration.AppContextKey,
                        new Action<IHostBuilder>(CaddyServerRegistration.Configure));
                    _registered = true;
                }
                host._loadContext = new AppLoadContext(assembly);
                var asm = host._loadContext.LoadFromAssemblyPath(assembly);
                var entry = asm.EntryPoint ?? asm.GetTypes()
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                                   BindingFlags.DeclaredOnly))
                    .FirstOrDefault(m => m.Name == "Main" && m.GetParameters() is var p &&
                                         (p.Length == 0 || p.Length == 1 && p[0].ParameterType == typeof(string[])))
                    ?? throw new InvalidOperationException($"No static Main entry point was found in '{assembly}'.");
                Environment.SetEnvironmentVariable("ASPNETCORE_APPLICATIONNAME", asm.GetName().Name);
                Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", contentRoot);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
                foreach (var pair in config) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                Starting.Value = host;
                host._entryPointTask = Task.Run(async () =>
                {
                    try
                    {
                        var result = entry.Invoke(null,
                            entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
                        if (result is Task task) await task;
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw ex.InnerException ?? ex;
                    }
                });
                Task.WhenAny(host._serverStarted.Task, host._entryPointTask).GetAwaiter().GetResult();
                if (!host._serverStarted.Task.IsCompletedSuccessfully)
                    throw host._entryPointTask.Exception?.GetBaseException() ??
                          new InvalidOperationException("Application entry point exited before its server started.");
                var id = (nint)Interlocked.Increment(ref _nextId);
                Hosts.Add(id, host);
                return id;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                host.Lifetime?.StopApplication();
                try { host._entryPointTask?.Wait(TimeSpan.FromSeconds(30)); }
                catch (Exception stopError) { Console.Error.WriteLine(stopError); }
                host._entryPointTask = null;
                host.Server = null;
                host.Lifetime = null;
                host._loadContext?.Unload();
                host._loadContext = null;
                return 0;
            }
            finally
            {
                Starting.Value = null;
            }
        }
    }

    internal static BridgeHost Get(nint id)
    {
        lock (Gate) return Hosts[id];
    }

    internal static int Shutdown(nint id)
    {
        BridgeHost host;
        lock (Gate)
        {
            if (!Hosts.TryGetValue(id, out host!)) return -1;
        }
        try
        {
            host.Lifetime?.StopApplication();
            if (host._entryPointTask?.Wait(TimeSpan.FromSeconds(30)) == false) return -1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return -1;
        }
        lock (Gate) Hosts.Remove(id);
        host._entryPointTask = null;
        host.Server = null;
        host.Lifetime = null;
        host._loadContext?.Unload();
        host._loadContext = null;
        return 0;
    }

    internal void Started(CaddyServer server)
    {
        Server = server;
        _serverStarted.TrySetResult();
    }

    internal RequestState Start(
        string method,
        string scheme, 
        string host,
        string path,
        string rawTarget,
        string query, 
        string remote, 
        Dictionary<string, List<string>> headers,
        long maxResponse)
    {
        var state = new RequestState();
        var request = new HttpRequestFeature
        {
            Method = method, Scheme = scheme, Path = path, PathBase = "", QueryString = query, RawTarget = rawTarget,
            Protocol = "HTTP/1.1", Headers = new HeaderDictionary(), Body = state.Input.Reader.AsStream()
        };
        request.Headers.Host = host;
        foreach (var pair in headers) request.Headers[pair.Key] = new StringValues(pair.Value.ToArray());
        var response = new ResponseFeature(state, maxResponse);
        state.Response = response;
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(response);
        features.Set<IHttpRequestLifetimeFeature>(new LifetimeFeature(state.Cancellation));
        features.Set<IHttpConnectionFeature>(new ConnectionFeature(remote));
        state.Task = Task.Run(async () =>
        {
            try
            {
                await Server!.Process(features);
                await response.CompleteAsync();
            }
            catch (Exception ex)
            {
                state.Error = ex;
                Console.Error.WriteLine(ex);
                if (!state.Started)
                {
                    state.Status = 500;
                    state.Headers.Clear();
                    state.Started = true;
                }
                await state.Output.Writer.CompleteAsync(ex);
                state.Finished = true;
            }
        });
        return state;
    }
}
