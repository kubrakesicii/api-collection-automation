using SampleApi.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton(typeof(InMemoryStore<>));

var app = builder.Build();
app.MapControllers();
app.Run();
