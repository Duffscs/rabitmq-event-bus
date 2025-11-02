namespace Library;

public record StartJobCommand(string JobName, string RoutingKey, string ReplyQueue) : IRequest;
public record Data(int Val);

public record HealthRequest(string RoutingKey, string ReplyQueue) : IRequest;
public record HealthResponse(string Status);
