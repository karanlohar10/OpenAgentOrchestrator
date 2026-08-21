using Asp.Versioning;
using OpenAgentOrchestrator.Command.Application;
using OpenAgentOrchestrator.Query.Application;
using OpenAgentOrchestrator.Service.Swagger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
}).AddMvc();

builder.Services.AddSwaggerGen(c =>
{
    c.OperationFilter<ExecuteRequestOperationFilter>();
});

builder.Services.AddHealthChecks();

// Command/Query application services: config.yaml loading, session/checkpoint stores, tool
// binding, agent creation (chat + Agent Harness + Shell Tools), and the workflow engine.
builder.Services.AddCommandApplication(builder.Configuration);
builder.Services.AddQueryApplication();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
