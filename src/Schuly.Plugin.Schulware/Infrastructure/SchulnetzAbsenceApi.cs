using System.Net;
using System.Text;
using System.Text.Json;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    public record StudentInfo(string Id, bool CanReportAbsences);

    public record AbsenceRow(string Id, string? DateFrom, string? DateTo, string? HourFrom, string? HourTo, string? Reason, string? Comment, string? Profile, string? Status, string? StudentTimestamp);

    public record AbsenceWriteResult(string? Id, string? Status, string? Message);

    /// <summary>Typed client for the absence routes of SchulwareAPI's mobile proxy (<c>/api/mobile/userInfo</c> and <c>/api/mobile/absences</c>).</summary>
    public class SchulnetzAbsenceApi(HttpClient http, string schulwareApiBaseUrl)
    {
        private readonly string _baseUrl = schulwareApiBaseUrl.TrimEnd('/');

        public async Task<StudentInfo> GetStudentAsync(CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, "/api/mobile/userInfo", null, ct);
            var root = doc.RootElement;
            var id = Str(root, "id") ?? throw AbsenceReportException.Unavailable();
            var allowed = root.TryGetProperty("permissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Object
                && permissions.TryGetProperty("absences", out var absences)
                && absences.ValueKind == JsonValueKind.Array
                && absences.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "abs_erfassen");
            return new StudentInfo(id, allowed);
        }

        public async Task<IReadOnlyList<AbsenceRow>> GetAbsencesAsync(CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Get, "/api/mobile/absences", null, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && Str(e, "id") is not null)
                .Select(e => new AbsenceRow(Str(e, "id")!, Str(e, "dateFrom"), Str(e, "dateTo"), Str(e, "hourFrom"), Str(e, "hourTo"), Str(e, "reason"), Str(e, "comment"), Str(e, "profile"), Str(e, "statusEAB"), Str(e, "studentTimestamp")))
                .ToList();
        }

        public async Task<IReadOnlyList<AbsenceWriteResult>> CreateAsync(object[] items, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Post, "/api/mobile/absences", items, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw AbsenceReportException.Unavailable();
            return doc.RootElement.EnumerateArray().Select(ToResult).ToList();
        }

        public async Task<AbsenceWriteResult> DeleteAsync(string absenceId, CancellationToken ct)
        {
            using var doc = await SendAsync(HttpMethod.Delete, $"/api/mobile/absences/{Uri.EscapeDataString(absenceId)}", null, ct);
            return ToResult(doc.RootElement);
        }

        private static AbsenceWriteResult ToResult(JsonElement e) => new(Str(e, "id"), Str(e, "status"), Str(e, "message"));

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(method, _baseUrl + path);
                if (body is not null)
                    request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw AbsenceReportException.SignInExpired();
                if (!response.IsSuccessStatusCode)
                    throw AbsenceReportException.Unavailable();

                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw AbsenceReportException.Unavailable();
            }
        }
    }
}
