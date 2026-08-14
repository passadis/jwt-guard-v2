using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using SentinelHostedAgent;
using SentinelHostedAgent.Configuration;
using SentinelHostedAgent.Tools;

Env.TraversePath().Load();

var options = HostedAgentOptions.FromEnvironment();
var credential = new DefaultAzureCredential();
var projectClient = new AIProjectClient(options.ProjectEndpoint, credential);

var gatewayHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var brokerHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var gatewayTool = new GatewayConfigurationTool(options, gatewayHttp, credential);
var logTool = new GatewayLogTool(options, new AzureHostedLogClient(credential));
var brokerTool = new BrokerEvidenceTool(options, brokerHttp, credential);

var tools = new List<AITool>
{
    AIFunctionFactory.Create(gatewayTool.GetAsync, "get_gateway_config"),
    AIFunctionFactory.Create(logTool.QueryAsync, "query_gate_logs"),
    AIFunctionFactory.Create(brokerTool.DecodeAsync, "decode_token"),
    AIFunctionFactory.Create(brokerTool.SimulateAsync, "simulate_gate_request"),
};
AIAgent agent = projectClient.AsAIAgent(
    model: options.ModelDeployment,
    instructions: GateExplainerInstructions.Text,
    name: "jwt-sentinel-gate-explainer",
    description: "Evidence-based explainer for Azure Application Gateway JWT Validation.",
    tools: [.. tools]);

var builder = AgentHost.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);
if (options.ToolboxName is not null)
{
    builder.Services.AddFoundryToolboxes(credential, options.ToolboxName);
}
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();

public partial class Program;
