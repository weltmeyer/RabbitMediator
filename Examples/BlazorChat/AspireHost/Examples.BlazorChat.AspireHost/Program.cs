using Aspire.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var rabbitUser = builder.AddParameter("RabbitUser");
var rabbitPass = builder.AddParameter("RabbitPassword", true);
var rabbitmq = builder.AddRabbitMQ("rabbitmq", rabbitUser, rabbitPass)
    .WithManagementPlugin(40000);
    

builder.AddProject<Examples_BlazorChat_App>("frontend").WithReference(rabbitmq).WaitFor(rabbitmq);
await builder.Build().RunAsync();