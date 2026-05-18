using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Http;

namespace BDSKhanhHoa.Services
{
    public class AuditLogService : IAuditLogService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuditLogService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogAsync(
            int userId,
            string action,
            string moduleName,
            string target,
            string? oldValues = null,
            string? newValues = null,
            string severity = "Info")
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var request = httpContext?.Request;

            string? ipAddress = GetClientIpAddress(httpContext, request);
            string? userAgent = request?.Headers["User-Agent"].ToString();

            var auditLog = new AuditLog
            {
                UserID = userId <= 0 ? 0 : userId,

                Action = LimitText(
                    string.IsNullOrWhiteSpace(action) ? "Không xác định thao tác" : action.Trim(),
                    255),

                ModuleName = LimitText(
                    string.IsNullOrWhiteSpace(moduleName) ? "System" : moduleName.Trim(),
                    100),

                Target = LimitText(
                    string.IsNullOrWhiteSpace(target) ? "N/A" : target.Trim(),
                    255),

                OldValues = oldValues,
                NewValues = newValues,

                IPAddress = LimitText(ipAddress, 50),
                UserAgent = LimitText(userAgent, 500),

                Severity = NormalizeSeverity(severity),

                CreatedAt = DateTime.Now
            };

            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();
        }

        private static string? GetClientIpAddress(HttpContext? httpContext, HttpRequest? request)
        {
            if (httpContext == null)
            {
                return null;
            }

            string? ipAddress = null;

            if (request != null)
            {
                ipAddress = request.Headers["CF-Connecting-IP"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(ipAddress))
                {
                    ipAddress = request.Headers["X-Forwarded-For"].FirstOrDefault();
                }

                if (!string.IsNullOrWhiteSpace(ipAddress) && ipAddress.Contains(","))
                {
                    ipAddress = ipAddress.Split(',')[0].Trim();
                }

                if (string.IsNullOrWhiteSpace(ipAddress))
                {
                    ipAddress = request.Headers["X-Real-IP"].FirstOrDefault();
                }
            }

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            }

            if (ipAddress == "::1")
            {
                ipAddress = "127.0.0.1";
            }

            return string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress;
        }

        private static string NormalizeSeverity(string? severity)
        {
            string clean = string.IsNullOrWhiteSpace(severity)
                ? "Info"
                : severity.Trim();

            clean = clean switch
            {
                "Critical" => "Critical",
                "Danger" => "Danger",
                "Warning" => "Warning",
                "Info" => "Info",
                _ => "Info"
            };

            return LimitText(clean, 20) ?? "Info";
        }

        private static string? LimitText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            value = value.Trim();

            if (value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }
    }
}