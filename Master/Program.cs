using System;
using RabbitMq;

var bus = new RabbitMqEventBus(
    username: "guest",
    password: "guest",
    hostName: "172.28.43.125",
    port: 5672,
    5
);
// Déclarer les queues et exchanges
await bus.Init([
    new QueueInfo("master.response", "master.response"),
    new QueueInfo("master.data", "data")
]);

Console.WriteLine("[MASTER] 🚀 Master démarré");

bus.Subscribe<Data, bool>("data", async (data) => {
    Console.WriteLine($"[MASTER] 📨 Data reçue");
    return true;
});

while (true) {
    Console.WriteLine("Tapez un nom de job à lancer (ou 'exit' pour quitter) :");
    var jobName = Console.ReadLine();
    if (jobName == "exit")
        break;
    if (jobName == "data") {
        await bus.Publish("data", new Data());
        Console.WriteLine($"[MASTER] 📤 Envoi de la data");
        continue;
    }

    if(jobName == "health") {
        HealthResponse health = await bus.SendRequest<HealthResponse>(new HealthRequest("slave.health", "master.response"));
        Console.WriteLine($"[MASTER] ✅ État de santé reçu: {health.Status}");
        continue;
    }


    StartJobCommand command = new StartJobCommand(jobName, "slave.jobs", "master.response");

    Console.WriteLine($"[MASTER] 📤 Envoi de la commande: {jobName}");
    bool response = await bus.SendRequest<bool>(command);

    Console.WriteLine($"[MASTER] ✅ Réponse reçue: {response}");
}
bus.Dispose();
