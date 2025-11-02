using Library;
using Library.Service;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
DIContainer container = new(services);


container.Load("config.json");
Slave slave = container.Get<Slave>("slave");
await slave.Start();
slave.Job();
