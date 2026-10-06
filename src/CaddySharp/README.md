# CaddySharp

Call `builder.Host.UseCaddyServer()` after `WebApplication.CreateBuilder(args)`. The bridge selects its server when Caddy launches the app. Direct launches continue to use Kestrel.

The native CaddySharp bridge and Caddyfile configuration are supplied separately by the CaddySharp repository.
