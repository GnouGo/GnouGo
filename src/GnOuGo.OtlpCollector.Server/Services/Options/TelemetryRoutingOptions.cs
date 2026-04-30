namespace OtlpTenantCollector.Services.Options;

public sealed class TelemetryRoutingOptions
{
    public const string SectionName = "TelemetryRouting";

    public bool Enabled { get; set; }
    public string? DefaultCollector { get; set; }
    public int TraceBufferSeconds { get; set; } = 2;
    public Dictionary<string, TelemetryCollectorOptions> Collectors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TelemetryRouteRuleOptions> Rules { get; set; } = [];
}

public sealed class TelemetryCollectorOptions
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IncludeTenantHeader { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class TelemetryRouteRuleOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Collector { get; set; } = string.Empty;
    public List<string> Signals { get; set; } = ["traces"];
    public TelemetryMatchOptions Match { get; set; } = new();
    public List<TelemetryMatchOptions> MatchAny { get; set; } = [];
}

public sealed class TelemetryMatchOptions
{
    public List<string> ServiceNames { get; set; } = [];
    public List<string> ServiceNameContains { get; set; } = [];
    public List<string> SpanNames { get; set; } = [];
    public List<string> SpanNameContains { get; set; } = [];
    public List<string> LogBodyContains { get; set; } = [];
    public List<TelemetryAttributeMatchOptions> Attributes { get; set; } = [];
    public List<TelemetryAttributeMatchOptions> ResourceAttributes { get; set; } = [];
    public List<TelemetryAttributeMatchOptions> ScopeAttributes { get; set; } = [];
    public List<TelemetryAttributeMatchOptions> AnyAttributes { get; set; } = [];
}

public sealed class TelemetryAttributeMatchOptions
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public List<string> Values { get; set; } = [];
    public string? Contains { get; set; }
    public bool Exists { get; set; } = true;
    public bool IgnoreCase { get; set; } = true;
}

