using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace CaddySharp;

internal static class BridgeHost
{
    internal static CaddyServer? Server;
    internal static string? Signature;

    internal static readonly TaskCompletionSource ServerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static Task? EntryPointTask;
    internal static IHostApplicationLifetime? Lifetime;

    internal static int Init(string assembly, string contentRoot, string environment, Dictionary<string, string> config)
    {
        lock (typeof(BridgeHost))
        {
            var signature = assembly + "|" + contentRoot + "|" + environment + "|" +
                            string.Join("|", config.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value));
            if (EntryPointTask != null) return Signature == signature ? 0 : -2;
            try
            {
                var asm = Assembly.LoadFrom(assembly);
                var entry = asm.EntryPoint ?? asm.GetTypes()
                        .SelectMany(t =>
                            t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly)).FirstOrDefault(m =>
                            m.Name == "Main" && m.GetParameters() is var p && (p.Length == 0 ||
                                                                               p.Length == 1 && p[0].ParameterType ==
                                                                               typeof(string[])))
                    ?? throw new InvalidOperationException(
                        $"No static Main entry point was found in '{assembly}'.");
                Environment.SetEnvironmentVariable("ASPNETCORE_APPLICATIONNAME", asm.GetName().Name);
                Environment.SetEnvironmentVariable("ASPNETCORE_CONTENTROOT", contentRoot);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environment);
                foreach (var pair in config) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                AppContext.SetData(CaddyServerRegistration.AppContextKey,
                    new Action<IHostBuilder>(CaddyServerRegistration.Configure));
                EntryPointTask = Task.Run(async () =>
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
                Task.WhenAny(ServerStarted.Task, EntryPointTask).GetAwaiter().GetResult();
                if (ServerStarted.Task.IsCompletedSuccessfully is false)
                    throw EntryPointTask.Exception?.GetBaseException() ??
                          new InvalidOperationException("Application entry point exited before its server started.");
                Signature = signature;
                return 0;
            }
            catch (Exception ex)
            {
                AppContext.SetData(CaddyServerRegistration.AppContextKey, null);
                Console.Error.WriteLine(ex);
                return -1;
            }
        }
    }

    internal static int Shutdown()
    {
        lock (typeof(BridgeHost))
        {
            if (EntryPointTask == null) return 0;
            try
            {
                Lifetime?.StopApplication();
                EntryPointTask.Wait(TimeSpan.FromSeconds(30));
                EntryPointTask = null;
                Server = null;
                Lifetime = null;
                AppContext.SetData(CaddyServerRegistration.AppContextKey, null);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return -1;
            }
        }
    }

    internal static RequestState Start(
        string method,
        string scheme, 
        string host,
        string path,
        string rawTarget,
        string query, 
        string remote, 
        byte[] body, //todo here the body is entirely loaded at memory.
        Dictionary<string, List<string>> headers,
        long maxResponse)
    {
        var state = new RequestState();
        var request = new HttpRequestFeature
        {
            Method = method, Scheme = scheme, Path = path, PathBase = "", QueryString = query, RawTarget = rawTarget,
            Protocol = "HTTP/1.1", Headers = new HeaderDictionary(), Body = new MemoryStream(body, false)
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
                state.Status = 500;
                state.Headers.Clear();
                state.Output.SetLength(0);
            }
        });
        return state;
    }
}