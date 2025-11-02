namespace Library.Service;

public class Master(bool logger = false) {
    public required string ServiceName { get; init; }
    public required RabbitMqEventBus Bus { get; init; }

    public async Task Start() {
        await Bus.Init([
            new QueueInfo("master.response", "master.response"),
            new QueueInfo("master.data", "data")
        ]);

        Console.WriteLine("[MASTER] 🚀 Master démarré");
        if (logger)
            Console.WriteLine("logger true");

        Bus.Subscribe<Data, bool>("data", OnData);
    }

    public async Task Job() {
        while (true) {
            Console.WriteLine("Tapez un nom de job à lancer (ou 'exit' pour quitter) :");
            var jobName = Console.ReadLine();
            if (jobName == "exit")
                break;
            if (jobName == "data") {
                await Bus.Publish("data", new Data(Random.Shared.Next()));
                Console.WriteLine($"[MASTER] 📤 Envoi de la data");
                continue;
            }

            if (jobName == "health") {
                HealthResponse health = await Bus.SendRequest<HealthResponse>(new HealthRequest("slave.health", "master.response"));
                Console.WriteLine($"[MASTER] ✅ État de santé reçu: {health.Status}");
                continue;
            }

            StartJobCommand command = new(jobName, "slave.jobs", "master.response");

            Console.WriteLine($"[MASTER] 📤 Envoi de la commande: {jobName}");
            bool response = await Bus.SendRequest<bool>(command);

            Console.WriteLine($"[MASTER] ✅ Réponse reçue: {response}");
        }
        Bus.Dispose();
    }

    public Task<bool> OnData(Data data) {
        Console.WriteLine($"[MASTER] 📨 Data reçue");
        return Task.FromResult(true);
    }
}
