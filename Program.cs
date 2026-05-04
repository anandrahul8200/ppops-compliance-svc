using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// --- HTTP Client: Reporting Service ---
builder.Services.AddHttpClient<ReportingServiceClient>(client =>
{
    var baseUrl = Environment.GetEnvironmentVariable("REPORTING_SVC_URL") ?? "http://ppops-reporting-svc:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// --- HTTP Client: Audit Service ---
builder.Services.AddHttpClient<AuditServiceClient>(client =>
{
    var baseUrl = Environment.GetEnvironmentVariable("AUDIT_SVC_URL") ?? "http://ppops-audit-svc:8080";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

app.MapControllers();
app.Run();

// --- HTTP Client Classes ---
public class ReportingServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ReportingServiceClient> _logger;

    public ReportingServiceClient(HttpClient httpClient, ILogger<ReportingServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JsonDocument?> GetDailyGenerationReportAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/reports/daily-generation");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch daily generation report from reporting-svc");
            return null;
        }
    }
}

public class AuditServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuditServiceClient> _logger;

    public AuditServiceClient(HttpClient httpClient, ILogger<AuditServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task LogAuditEventAsync(string eventType, string userId, string resource, string action)
    {
        try
        {
            var payload = new { eventType, userId, resource, action };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await _httpClient.PostAsync("/api/audit/log", content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log audit event to audit-svc");
        }
    }
}

// --- Models ---
public record ComplianceResult(string PlantId, string Status, List<string> Violations, DateTime CheckedAt);
public record ViolationRecord(string PlantId, string ViolationType, string Description, string Severity, DateTime DetectedAt);

// --- Controller ---
[ApiController]
[Route("api/compliance")]
public class ComplianceController : ControllerBase
{
    private static readonly List<ViolationRecord> _violations = new();
    private readonly ReportingServiceClient _reportingClient;
    private readonly AuditServiceClient _auditClient;
    private readonly ILogger<ComplianceController> _logger;

    public ComplianceController(ReportingServiceClient reportingClient, AuditServiceClient auditClient, ILogger<ComplianceController> logger)
    {
        _reportingClient = reportingClient;
        _auditClient = auditClient;
        _logger = logger;
    }

    [HttpGet("check/{plantId}")]
    public async Task<IActionResult> CheckCompliance(string plantId)
    {
        var report = await _reportingClient.GetDailyGenerationReportAsync();
        var violations = _violations.Where(v => v.PlantId == plantId).ToList();
        var status = violations.Any(v => v.Severity == "critical") ? "non-compliant" : "compliant";

        await _auditClient.LogAuditEventAsync("compliance_check", "system", $"plant/{plantId}", "check");

        var result = new ComplianceResult(plantId, status, violations.Select(v => v.ViolationType).ToList(), DateTime.UtcNow);
        return Ok(result);
    }

    [HttpGet("violations")]
    public IActionResult GetViolations([FromQuery] string? plantId = null, [FromQuery] string? severity = null)
    {
        var query = _violations.AsEnumerable();

        if (!string.IsNullOrEmpty(plantId))
            query = query.Where(v => v.PlantId == plantId);
        if (!string.IsNullOrEmpty(severity))
            query = query.Where(v => v.Severity == severity);

        var results = query.OrderByDescending(v => v.DetectedAt).ToList();
        return Ok(new { count = results.Count, violations = results });
    }
}