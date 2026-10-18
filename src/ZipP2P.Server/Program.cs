using ZipP2P.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

app.MapGet("/", () => "ZipStream signaling server");
app.MapHub<SignalingHub>("/signaling");

app.Run();
