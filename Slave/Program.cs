using System;
using System.Threading.Tasks;
using RabbitMq;

var bus = new RabbitMqEventBus(
    username: "guest",
    password: "guest",
    hostName: "172.20.80.118",
    port: 5672
);

// Déclarer la même queue
bus.Init([
    new QueueInfo("slave_jobs_queue", "slave.jobs"),
    new QueueInfo("slave.data", "data")
]);

// Ici, on subscribe à une IRequest
bus.Subscribe<StartJobCommand>("slave.jobs", async (command) => {
    Console.WriteLine($"[SLAVE] 📨 Commande reçue: {command.JobName}");
    await Task.Delay(1000); // Simuler un traitement
    return true;
});

bus.Subscribe<Data>("data", async (data) => {
    Console.WriteLine($"[SLAVE] 📨 Data reçue");
    return true;
});

Console.WriteLine("[SLAVE] ⚡ Prêt à recevoir des jobs. Appuyez sur Entrée pour quitter.");
Console.ReadLine();

bus.Dispose();
