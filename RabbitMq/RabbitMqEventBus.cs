using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;

namespace RabbitMq;

public record QueueInfo(string QueueName, string RoutingKey);
public interface IRequest { }

public class RabbitMqEventBus : IDisposable {
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private IModel? _channel;

    private readonly TaskCompletionSource<IModel> _connectionTcs;
    public Task<IModel> ConnectionTask => _connectionTcs.Task;

    private readonly string _mainExchange = "main_exchange";
    private readonly string _publishExchange = "publish_exchange";
    private readonly string _deadLetterExchange = "dead_letter_exchange";

    private readonly string _expiredQueue = "dlq_expired";
    private readonly string _rejectedQueue = "dlq_rejected";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Func<string, Task<bool>>> _subscribers = new();

    private string? _replyQueue;
    private AsyncEventingBasicConsumer? _replyConsumer;

    private readonly int MESSAGE_TIMEOUT_MS = 60000;
    private readonly int RETRY_INTERVAL_MS = 5000;

    private readonly object _syncRoot = new();
    private bool _disposed;

    public RabbitMqEventBus(string username, string password, string hostName, int port) {
        _factory = new ConnectionFactory {
            UserName = username,
            Password = password,
            HostName = hostName,
            Port = port,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        _connectionTcs = new TaskCompletionSource<IModel>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(ConnectionWorkerAsync);
    }

    private async Task ConnectionWorkerAsync() {
        while (!_disposed) {
            if (_connection == null || !_connection.IsOpen) {
                try {
                    Console.WriteLine("[RabbitMQ] Tentative de connexion…");
                    var conn = _factory.CreateConnection();
                    IModel ch = conn.CreateModel();

                    conn.ConnectionShutdown += (s, e) => {
                        Console.WriteLine($"[RabbitMQ] ⚠️ Déconnexion : {e.ReplyText}");
                    };

                    //conn.RecoverySucceeded += (s, e) => {
                    //    Console.WriteLine("[RabbitMQ] 🔄 Reconnexion automatique réussie");
                    //};

                    lock (_syncRoot) {
                        _connection = conn;
                        _channel = ch;
                        SetupReplyQueue();

                        if (!_connectionTcs.Task.IsCompleted)
                            _connectionTcs.TrySetResult(ch);
                    }

                    Console.WriteLine("[RabbitMQ] ✅ Connexion établie");
                } catch (Exception ex) {
                    Console.WriteLine($"[RabbitMQ] ❌ Erreur de connexion : {ex.Message}");
                }
            }

            await Task.Delay(RETRY_INTERVAL_MS);
        }
    }

    private void SetupReplyQueue() {
        if (_channel == null)
            return;

        _replyQueue = _channel.QueueDeclare(queue: "", exclusive: true, autoDelete: true).QueueName;
        _replyConsumer = new AsyncEventingBasicConsumer(_channel);
        _replyConsumer.Received += async(model, ea) => {
            var correlationId = ea.BasicProperties.CorrelationId;
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());

            if (_pendingRequests.TryRemove(correlationId, out var tcs))
                tcs.TrySetResult(body);
        };

        _channel.BasicConsume(queue: _replyQueue, autoAck: true, consumer: _replyConsumer);
    }

    private IModel GetConnection() => ConnectionTask.GetAwaiter().GetResult();

    public void Init(List<QueueInfo> queues) {
        IModel channel = GetConnection();

        channel.ExchangeDeclare(_mainExchange, ExchangeType.Direct, durable: true);
        channel.ExchangeDeclare(_publishExchange, ExchangeType.Fanout, durable: true);
        channel.ExchangeDeclare(_deadLetterExchange, ExchangeType.Direct, durable: true);

        channel.QueueDeclare(_expiredQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueuePurge(_expiredQueue);
        channel.QueueBind(_expiredQueue, _deadLetterExchange, "expired");

        channel.QueueDeclare(_rejectedQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueuePurge(_rejectedQueue);
        channel.QueueBind(_rejectedQueue, _deadLetterExchange, "rejected");

        foreach (var queueInfo in queues) {
            Dictionary<string, object> args = new() {
                { "x-dead-letter-exchange", _deadLetterExchange },
                { "x-message-ttl", MESSAGE_TIMEOUT_MS }
            };

            channel.QueueDeclare(queueInfo.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: args);
            channel.QueuePurge(queueInfo.QueueName);
            channel.QueueBind(queueInfo.QueueName, _mainExchange, queueInfo.RoutingKey);

            CreateQueueConsumer(queueInfo.QueueName, queueInfo.RoutingKey);
        }
    }

    private void CreateQueueConsumer(string queueName, string routingKey) {
        IModel channel = GetConnection();
        AsyncEventingBasicConsumer consumer = new(channel);

        consumer.Received += async (model, ea) => {
            string json = Encoding.UTF8.GetString(ea.Body.ToArray());
            Console.WriteLine(queueName);
            if (_subscribers.TryGetValue(routingKey, out var handler)) {
                try {
                    var props = ea.BasicProperties;
                    var replyProps = channel.CreateBasicProperties();
                    replyProps.CorrelationId = props.CorrelationId;
                    bool response = await handler(json);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                    if (!string.IsNullOrEmpty(props.ReplyTo)) {
                        channel.BasicPublish(
                            exchange: "",
                            routingKey: props.ReplyTo,
                            basicProperties: replyProps,
                            body: responseBytes
                        );
                    }

                } catch (Exception ex) {
                    Console.Error.WriteLine($"[RabbitMQ] ❌ Erreur dans le handler : {ex.Message}");
                    MoveToRejected(ea.Body.ToArray());
                }
            }

            channel.BasicAck(ea.DeliveryTag, false);
        };

        channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
    }

    public void Publish<T>(string routingKey, T message) {
        IModel channel = GetConnection();
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        IBasicProperties props = channel.CreateBasicProperties();
        props.Persistent = true;

        channel.BasicPublish(
            exchange: _mainExchange,
            routingKey: routingKey,
            basicProperties: props,
            body: body
        );
    }

    public void Subscribe<T>(string routingKey, Func<T, Task<bool>> onMessage) {
        Func<string, Task<bool>> wrapper = new(async (json) => {
            T? obj = JsonSerializer.Deserialize<T>(json);
            if (obj != null)
                return await onMessage(obj);
            return false;
        });

        _subscribers.AddOrUpdate(
            routingKey,
            _ => wrapper,
            (_, list) => {
                return wrapper;
            });
    }

    public bool SendRequest<TRequest>(string routingKey, TRequest request)
        where TRequest : IRequest {
        IModel channel = GetConnection();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        var props = channel.CreateBasicProperties();
        var correlationId = Guid.NewGuid().ToString();
        props.CorrelationId = correlationId;
        props.ReplyTo = _replyQueue;
        props.Persistent = true;

        var tcs = new TaskCompletionSource<string>();
        _pendingRequests[correlationId] = tcs;

        channel.BasicPublish(
            exchange: _mainExchange,
            routingKey: routingKey,
            basicProperties: props,
            body: body
        );

        if (tcs.Task.Wait(MESSAGE_TIMEOUT_MS)) {
            var responseBody = tcs.Task.Result;
            return JsonSerializer.Deserialize<bool>(responseBody);
        } else {
            Console.Error.WriteLine($"[RabbitMQ] ⏳ Request timeout pour correlationId {correlationId}");
            _pendingRequests.TryRemove(correlationId, out _);
            return default;
        }
    }

    private void MoveToRejected(byte[] body) {
        IModel channel = GetConnection();
        IBasicProperties props = channel.CreateBasicProperties();
        props.Persistent = true;
        channel.BasicPublish(_deadLetterExchange, "rejected", props, body);
    }

    public void Dispose() {
        _disposed = true;
        lock (_syncRoot) {
            _channel?.Close();
            _connection?.Close();
        }
    }
}
