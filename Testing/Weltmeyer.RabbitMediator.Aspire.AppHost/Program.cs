var builder = DistributedApplication.CreateBuilder(args);

var rabbitUser = builder.AddParameter("RabbitUser");
var rabbitPass = builder.AddParameter("RabbitPassword", true);
builder.AddRabbitMQ("rabbitmq",rabbitUser,rabbitPass)
    .WithManagementPlugin(40000)
    .WithEndpoint("management", e => e.Port = 40000);
    

builder.Build().Run();