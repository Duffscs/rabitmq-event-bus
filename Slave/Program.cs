using System;
using System.Threading.Tasks;
using RabbitMq;

var bus = new RabbitMqEventBus(
    username: "guest",
    password: "guest",
    hostName: "172.28.43.125",
    port: 5672,
    5
);

// Déclarer la même queue
await bus.Init([
    new QueueInfo("slave_jobs_queue", "slave.jobs"),
    new QueueInfo("slave.data", "data"),
    new QueueInfo("slave.health", "slave.health")
]);

// Ici, on subscribe à une IRequest
bus.Subscribe<StartJobCommand, bool>("slave.jobs", async (command) => {
    Console.WriteLine($"[SLAVE] 📨 Commande reçue: {command.JobName}");
    await Task.Delay(1000); // Simuler un traitement
    return true;
});

bus.Subscribe<Data, bool>("data", (data) => {
    Console.WriteLine($"[SLAVE] 📨 Data reçue");
    return Task.FromResult(true);
});

bus.Subscribe<HealthRequest, HealthResponse>("slave.health", (request) => {
    Console.WriteLine($"[SLAVE] 📨 Requête de santé reçue");
    // Randomly decide if healthy or not
    switch (new Random().Next(0, 2)) {
        case 0:
            Console.WriteLine($"[SLAVE] ❌ État de santé: Unhealthy");
            return Task.FromResult(new HealthResponse("Unhealthy"));
        default:
            Console.WriteLine($"[SLAVE] ✅ État de santé: Healthy");
            return Task.FromResult(new HealthResponse("Healthy"));
    }
});

Console.WriteLine("[SLAVE] ⚡ Prêt à recevoir des jobs. Appuyez sur Entrée pour quitter.");
Console.ReadLine();

bus.Dispose();
