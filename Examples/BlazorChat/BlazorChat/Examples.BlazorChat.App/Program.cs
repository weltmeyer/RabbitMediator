using Examples.BlazorChat.App;
using Examples.BlazorChat.App.Components;
using Examples.BlazorChat.App.MediatorClasses.Consumers;
using Weltmeyer.RabbitMediator;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("rabbitmq");
if (connectionString is null)
    throw new NullReferenceException("Rabbitmq connectionString not set");
builder.Services.AddRabbitMediator(
    cfg =>
    {
        cfg.ConsumerTypes = [typeof(BroadcastChatMessageConsumer), typeof(DirectChatMessageConsumer)];
        cfg.ConnectionString = connectionString;
        cfg.ServiceLifetime = ServiceLifetime.Scoped;
    });

builder.Services.AddSingleton<AppState>();
builder.Services.AddScoped<UserState>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();