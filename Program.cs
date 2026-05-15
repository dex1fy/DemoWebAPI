using DemoWebAPI.Data;
using DemoWebAPI.Endpoints;
using DemoWebAPI.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Use dotnet user-secrets or environment variables.");
}

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AmlDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<DatabaseExplorerService>();
builder.Services.AddScoped<BusinessRulesService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapDatabaseExplorerEndpoints();
app.MapBusinessRuleEndpoints();

app.Run();
