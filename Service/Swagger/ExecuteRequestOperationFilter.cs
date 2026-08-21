using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenAgentOrchestrator.Command.Contract;
using OpenAgentOrchestrator.Service.Command.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OpenAgentOrchestrator.Service.Swagger
{
    /// <summary>
    /// Documents the request body of <see cref="OrchestratorSessionsCommandController.Execute"/>
    /// with two schemas (application/json and multipart/form-data) since the action reads the
    /// payload manually and has no bound parameter for ApiExplorer/Swashbuckle to infer from.
    /// </summary>
    public sealed class ExecuteRequestOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            if (context.MethodInfo.DeclaringType != typeof(OrchestratorSessionsCommandController) ||
                context.MethodInfo.Name != nameof(OrchestratorSessionsCommandController.Execute))
            {
                return;
            }

            var jsonSchema = context.SchemaGenerator.GenerateSchema(typeof(ExecuteRequest), context.SchemaRepository);

            var formSchema = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["input"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Text input to execute. Provide this OR file, not both."
                    },
                    ["file"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "binary",
                        Description = "File input (UTF-8 text) to execute. Provide this OR input, not both."
                    },
                    ["sessionId"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Existing session id to continue (optional)."
                    },
                    ["context[key]"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Description = "Optional context entries. Repeat with different keys, e.g. context[locale]=en-US."
                    }
                }
            };

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = jsonSchema,
                        Example = new JsonObject
                        {
                            ["input"] = "Map this HL7 v2 ORU message to an openEHR composition."
                        }
                    },
                    ["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = formSchema
                    }
                }
            };
        }
    }
}
