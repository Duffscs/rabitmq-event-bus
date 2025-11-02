using Library;
using Library.Service;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
DIContainer container = new(services);

container.Load("config.json");

Master master = container.Get<Master>("master");

await master.Start();
await master.Job();
