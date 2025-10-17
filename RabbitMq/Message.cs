using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RabbitMq;


public record StartJobCommand(string JobName) : RabbitMq.IRequest;
public record Data();
