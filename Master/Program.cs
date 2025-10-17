using System;
using RabbitMq;

var bus = new RabbitMqEventBus(
    username: "guest",
    password: "guest",
    hostName: "172.20.80.118",
    port: 5672
);

// Déclarer les queues et exchanges
bus.Init([
    new QueueInfo("master.data", "data")
]);

Console.WriteLine("[MASTER] 🚀 Master démarré");

bus.Subscribe<Data>("data", async (data) => {
    Console.WriteLine($"[MASTER] 📨 Data reçue");
    return true;
});

while (true) {
    Console.WriteLine("Tapez un nom de job à lancer (ou 'exit' pour quitter) :");
    var jobName = Console.ReadLine();
    if(jobName == "data") {
        bus.Publish("data", new Data());
        Console.WriteLine($"[MASTER] 📤 Envoi de la data");
        continue;
    }

    if (jobName == "exit")
        break;

    var command = new StartJobCommand(jobName);

    Console.WriteLine($"[MASTER] 📤 Envoi de la commande: {jobName}");
    var response = bus.SendRequest("slave.jobs", command);

    Console.WriteLine($"[MASTER] ✅ Réponse reçue: {response}");
}

bus.Dispose();
