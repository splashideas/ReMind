var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { status = "ReMind API scaffold" }));

app.Run();

public partial class Program
{
}
