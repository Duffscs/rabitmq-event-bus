using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RabbitMq;


public record StartJobCommand(string JobName, string RoutingKey, string ReplyQueue) : IRequest;
public record Data();

public record HealthRequest(string RoutingKey, string ReplyQueue) : IRequest;
public record HealthResponse(string Status);
