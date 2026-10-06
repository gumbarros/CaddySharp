FROM mcr.microsoft.com/dotnet/sdk:10.0 AS managed
RUN apt-get update && apt-get install -y --no-install-recommends clang g++ && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY src ./src
RUN dotnet build src/CaddySharp -c Release --runtime linux-x64 && \
    cp src/CaddySharp/obj/Release/net10.0/linux-x64/dnne/bin/CaddySharpNE.so src/CaddySharp/bin/Release/net10.0/linux-x64/CaddySharpNE.so && \
    dotnet build src/CaddySharp.Sample -c Release

FROM golang:1.26 AS native
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY cmd ./cmd
COPY go ./go
RUN CGO_ENABLED=1 go build -o /out/caddysharp ./cmd/caddy && go build -o /out/caddysharp-load ./cmd/load

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=managed /src/src/CaddySharp/bin/Release/net10.0/linux-x64/ ./bridge/
COPY --from=managed /src/src/CaddySharp.Sample/bin/Release/net10.0/ ./sample/
COPY --from=native /out/caddysharp /usr/local/bin/caddysharp
COPY Caddyfile.docker /app/Caddyfile
EXPOSE 8080
ENTRYPOINT ["caddysharp","run","--config","/app/Caddyfile"]
