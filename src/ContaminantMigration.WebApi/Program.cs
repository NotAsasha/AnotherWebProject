using ContaminantMigration.Infrastructure;
using ContaminantMigration.Infrastructure.Health;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "O";
});

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Service-Instance"] = Environment.MachineName;
    await next(context);
});

app.MapControllers();
app.MapServiceHealthChecks();
app.Run();
