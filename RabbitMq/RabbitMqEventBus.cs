using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMq;

public record QueueInfo(string QueueName, string RoutingKey);

public interface IRequest {
    string RoutingKey { get; }
    string ReplyQueue { get; }
}

public record ConnectionManager(
    IConnection Connection,
    IChannel PublishingChannel,
    ConcurrentBag<IChannel> ConsumerChannels
) : IDisposable {
    public void Dispose() {
        PublishingChannel.Dispose();
        while (ConsumerChannels.TryTake(out IChannel? c))
            c.Dispose();
        Connection.Dispose();
    }
}

public class RabbitMqEventBus : IDisposable {
    private readonly ConnectionFactory _factory;
    private ConnectionManager? _connectionManager;
    private TaskCompletionSource<ConnectionManager> _connectionTcs;

    public Task<ConnectionManager> ConnectionTask => _connectionTcs.Task;

    private readonly string _mainExchange = "main_exchange";
    private readonly string _publishExchange = "publish_exchange";
    private readonly string _deadLetterExchange = "dead_letter_exchange";
    private readonly string _expiredQueue = "dlq_expired";
    private readonly string _rejectedQueue = "dlq_rejected";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Func<string, Task<object>>> _subscribers = new();

    private readonly int MESSAGE_TIMEOUT_MS = 60000;
    private readonly object _syncRoot = new();
    private bool _disposed;

    public RabbitMqEventBus(string username, string password, string hostName, int port, int recoveryTimeSecond) {
        _factory = new ConnectionFactory {
            UserName = username,
            Password = password,
            HostName = hostName,
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(recoveryTimeSecond)
        };

        _connectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = InitializeConnectionAsync();
    }

    private async Task InitializeConnectionAsync() {
        while (!_connectionTcs.Task.IsCanceled && !_connectionTcs.Task.IsCompleted) {
            try {
                Console.WriteLine("[RabbitMQ] Tentative de connexion…");
                IConnection conn = await _factory.CreateConnectionAsync();
                IChannel pubChannel = await conn.CreateChannelAsync();

                conn.ConnectionShutdownAsync += OnConnectionShutdown;
                conn.RecoverySucceededAsync += OnRecoverySucceeded;
                conn.ConnectionRecoveryErrorAsync += OnRecoveryError;

                lock (_syncRoot) {
                    _connectionManager = new(conn, pubChannel, []);
                    if (!_connectionTcs.Task.IsCompleted)
                        _connectionTcs.TrySetResult(_connectionManager);
                }
                Console.WriteLine("[RabbitMQ] ✅ Connexion établie");
            } catch (Exception ex) {
                Console.WriteLine($"[RabbitMQ] ❌ Erreur de connexion initiale : {ex.Message}");
                Console.WriteLine($"[RabbitMQ] ❌ Nouvelle tentative dans : {_factory.NetworkRecoveryInterval.Seconds} secondes.");
                await Task.Delay(_factory.NetworkRecoveryInterval);
            }
        }
    }

    private Task OnConnectionShutdown(object sender, ShutdownEventArgs reason) {
        Console.WriteLine($"[RabbitMQ] ⚠️ Connexion perdue : {reason.ReplyText}");
        Console.WriteLine($"[RabbitMQ] ⚠️ Tentative de reconnexion");
        lock (_syncRoot) {
            _connectionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return Task.CompletedTask;
    }

    private async Task OnRecoverySucceeded(object sender, AsyncEventArgs e) {
        Console.WriteLine("[RabbitMQ] 🔁 Recovery réussi — recréation du channel publisher");
        try {
            IConnection conn = (IConnection)sender;
            IChannel newChannel = await conn.CreateChannelAsync();
            lock (_syncRoot) {
                _connectionManager = _connectionManager! with { PublishingChannel = newChannel };
                if (!_connectionTcs.Task.IsCompleted)
                    _connectionTcs.TrySetResult(_connectionManager);
            }
            Console.WriteLine("[RabbitMQ] ✅ Nouveau channel publisher opérationnel");
        } catch (Exception ex) {
            Console.WriteLine($"[RabbitMQ] ❌ Erreur lors du recovery : {ex.Message}");
        }
    }

    private Task OnRecoveryError(object sender, ConnectionRecoveryErrorEventArgs e) {
        Console.WriteLine($"[RabbitMQ] ⚠️ Erreur de recovery : {e.Exception.Message}");
        Console.WriteLine($"[RabbitMQ] ⚠️ Nouvelle tentative dans : {_factory.NetworkRecoveryInterval.Seconds} secondes.");
        return Task.CompletedTask;
    }

    // --------------------------- INIT / CONSUMERS ------------------------------

    public async Task Init(List<QueueInfo> queues) {
        ConnectionManager cm = await ConnectionTask;
        IChannel channel = cm.PublishingChannel;

        await channel.BasicQosAsync(0, 100, false);

        await channel.ExchangeDeclareAsync(_mainExchange, ExchangeType.Direct, durable: true);
        await channel.ExchangeDeclareAsync(_publishExchange, ExchangeType.Fanout, durable: true);
        await channel.ExchangeDeclareAsync(_deadLetterExchange, ExchangeType.Direct, durable: true);

        await channel.QueueDeclareAsync(_expiredQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(_expiredQueue, _deadLetterExchange, "expired");

        await channel.QueueDeclareAsync(_rejectedQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(_rejectedQueue, _deadLetterExchange, "rejected");

        Dictionary<string, object?> arguments = new() {
            { "x-dead-letter-exchange", _deadLetterExchange },
            { "x-message-ttl", MESSAGE_TIMEOUT_MS }
        };

        foreach (QueueInfo info in queues) {
            await channel.QueueDeclareAsync(info.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: arguments);
            await channel.QueueBindAsync(info.QueueName, _mainExchange, info.RoutingKey);
            cm.ConsumerChannels.Add(await CreateQueueConsumer(cm.Connection, info));
        }
    }

    private async Task<IChannel> CreateQueueConsumer(IConnection connection, QueueInfo info) {
        IChannel channel = await connection.CreateChannelAsync();
        AsyncEventingBasicConsumer consumer = new(channel);

        consumer.ReceivedAsync += (sender, ea) => {
            IChannel receiverChannel = ((AsyncEventingBasicConsumer)sender).Channel;
            byte[] body = ea.Body.ToArray();
            string json = Encoding.UTF8.GetString(body);
            IReadOnlyBasicProperties props = ea.BasicProperties;
            ulong deliveryTag = ea.DeliveryTag;
            _ = Task.Run(async () => {
                try {
                    if(_pendingRequests.TryRemove(props.CorrelationId ?? string.Empty, out TaskCompletionSource<string>? tcs)) {
                        tcs.SetResult(json);
                        return;
                    }
                    if (_subscribers.TryGetValue(info.RoutingKey, out Func<string, Task<object>>? handler)) {
                        object response = await handler(json);
                        if (!string.IsNullOrEmpty(props.ReplyTo)) {
                            await InnerPublish(_mainExchange, props.ReplyTo, response, props.CorrelationId);
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[RabbitMQ] ❌ Handler erreur : {ex.Message}");
                    await InnerPublish(_deadLetterExchange, "rejected", body);
                } finally {
                    await receiverChannel.BasicAckAsync(deliveryTag, false);
                }
            });
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(info.QueueName, autoAck: false, consumer);
        return channel;
    }

    // --------------------------- PUBLISH / REQUEST ------------------------------

    private async Task InnerPublish(string exchange, string routingKey, object message, string? correlationId = null, string? replyTo = null) {
        ConnectionManager cm = await ConnectionTask;
        IChannel channel = cm.PublishingChannel;
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        BasicProperties props = new() {
            Persistent = true,
            CorrelationId = correlationId,
            ReplyTo = replyTo
        };
        await channel.BasicPublishAsync(exchange, routingKey, false, props, body);
    }

    public Task Publish<T>(string routingKey, T message) =>
        InnerPublish(_mainExchange, routingKey, message);

    public void Subscribe<T, TResponse>(string routingKey, Func<T, Task<TResponse>> onMessage) {
        _subscribers[routingKey] = async (json) => {
            T? obj = JsonSerializer.Deserialize<T>(json);
            return await onMessage(obj);
        };
    }

    public async Task<TResponse> SendRequest<TResponse>(IRequest request) {
        string correlationId = Guid.NewGuid().ToString();
        TaskCompletionSource<string> tcs = new();
        _pendingRequests[correlationId] = tcs;

        await InnerPublish(_mainExchange, request.RoutingKey, request, correlationId, request.ReplyQueue);

        Task timeout = Task.Delay(MESSAGE_TIMEOUT_MS);
        if (await Task.WhenAny(tcs.Task, timeout) == tcs.Task) {
            string result = tcs.Task.Result;
            return JsonSerializer.Deserialize<TResponse>(result)!;
        }

        Console.Error.WriteLine($"[RabbitMQ] ⏳ Timeout pour {correlationId}");
        _pendingRequests.TryRemove(correlationId, out _);
        return default!;
    }

    // --------------------------- DISPOSE ------------------------------

    public void Dispose() {
        _connectionTcs.TrySetCanceled();
        _disposed = true;
        lock (_syncRoot) {
            _connectionManager?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
