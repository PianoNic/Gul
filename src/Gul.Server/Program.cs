using Gul.Server;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = null;
});

builder.Services.AddSingleton<TunnelRegistry>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var authority = builder.Configuration["Oidc:Authority"];
        options.MetadataAddress = $"{authority?.TrimEnd('/')}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = builder.Configuration.GetValue("Oidc:RequireHttpsMetadata", true);
        options.TokenValidationParameters.ValidIssuer = authority;
        options.TokenValidationParameters.ValidateAudience = false;

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.Request.Path.StartsWithSegments("/tunnel"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<TunnelForwardingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapGet("/config", (IConfiguration config) => Results.Ok(new
{
    authority = config["Oidc:Authority"],
    clientId = config["Oidc:ClientId"],
    scopes = config["Oidc:Scopes"] ?? "openid profile email",
    baseDomain = config["Gul:BaseDomain"],
})).AllowAnonymous();

app.MapHub<TunnelHub>("/tunnel").RequireAuthorization();

app.Run();

public partial class Program { }
