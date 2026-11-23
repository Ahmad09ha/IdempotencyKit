using IdempotencyKit.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// This is deliberately only a health-check sample. No write endpoint is protected yet.
app.MapGet("/health", () => Results.Ok(new
{
    service = "orders-aspnetcore",
    status = "ok",
    idempotency = AspNetCoreScaffoldInfo.Status
}));

app.Run();

public partial class Program;
