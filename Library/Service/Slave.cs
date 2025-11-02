namespace Library.Service;

public class Slave {
    public required string ServiceName { get; init; }
    public required RabbitMqEventBus Bus { get; init; }
    public bool _logger;

    public required List<int> IntList { get; set; }
    public required List<int> IntList2 { get; set; }
    public required HashSet<int> IntSet { get; set; }

    public required List<Data> Datas { get; set; }

    public required Dictionary<string, int> StringDict { get; set; }


    public Slave(bool logger = false) => _logger = logger;

    public Slave(string serviceName, RabbitMqEventBus bus, bool logger = false) {
        ServiceName = serviceName;
        Bus = bus;
        _logger = logger;
    }

    public async Task Start() {
        await Bus.Init([
            new QueueInfo("slave_jobs_queue", "slave.jobs"),
            new QueueInfo("slave.data", "data"),
            new QueueInfo("slave.health", "slave.health")
        ]);

        Console.WriteLine(string.Join(",", IntList));
        Console.WriteLine(string.Join(",", IntList2));
        Console.WriteLine(string.Join(",", IntSet));
        foreach (var kvp in StringDict) {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }
        foreach (var data in Datas) {
            Console.WriteLine($"Data instance: {data.Val}");
        }

        if (_logger)
            Console.WriteLine("logger true");
        Console.WriteLine("[SLAVE] 🚀 Slave démarré");

        Bus.Subscribe<StartJobCommand, bool>("slave.jobs", OnJob);

        Bus.Subscribe<Data, bool>("data", OnData);
        Bus.Subscribe<HealthRequest, HealthResponse>("slave.health", OnHealthRequest);
    }

    public void Job() {
        Console.WriteLine("[SLAVE] ⚡ Prêt à recevoir des jobs. Appuyez sur Entrée pour quitter.");
        Console.ReadLine();
    }

    public async Task<bool> OnJob(StartJobCommand command) {
        Console.WriteLine($"[SLAVE] 📨 Commande reçue: {command.JobName}");
        await Task.Delay(1000); // Simuler un traitement
        return true;
    }

    public Task<bool> OnData(Data data) {
        Console.WriteLine($"[SLAVE] 📨 Data reçue {data.Val}");
        return Task.FromResult(true);
    }

    public Task<HealthResponse> OnHealthRequest(HealthRequest healthRequest) {
        Console.WriteLine($"[SLAVE] 📨 Requête de santé reçue");
        switch (new Random().Next(0, 2)) {
            case 0:
                Console.WriteLine($"[SLAVE] ❌ État de santé: Unhealthy");
                return Task.FromResult(new HealthResponse("Unhealthy"));
            default:
                Console.WriteLine($"[SLAVE] ✅ État de santé: Healthy");
                return Task.FromResult(new HealthResponse("Healthy"));
        }
    }



}
