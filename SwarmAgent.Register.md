# SwarmAgent Register call site

After `core.LoadSharedHandlers();` insert:

```csharp
HandlerForge.Register(core); // LLM-free forge loop (#20 #21)
```

Exact location (main tip):
```
var core = new AgentCore(client);
core.LoadSharedHandlers();
HandlerForge.Register(core); // LLM-free forge loop (#20 #21)

await using var node = new SwarmNode(myPort);
```

AgentRepl already has both call sites on this branch (24e996b).
