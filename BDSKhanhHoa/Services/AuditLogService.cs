using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;

namespace BDSKhanhHoa.Services
{
    public class AuditLogService : IAuditLogService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuditLogService(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogAsync(int userId, string action, string moduleName, string target, string oldValues = null, string newValues = null, string severity = "Info")
        {
            var request = _httpContextAccessor.HttpContext?.Request;

            // Lấy IP thật nếu web chạy qua Cloudflare hoặc Nginx proxy
            var ipAddress = request?.Headers["X-Forwarded-For"].FirstOrDefault();

            if (string.IsNullOrEmpty(ipAddress))
            {
                ipAddress = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();
            }

            // Chuyển đổi IPv6 localhost thành IPv4 cho dễ nhìn
            if (ipAddress == "::1")
            {
                ipAddress = "127.0.0.1 (Localhost)";
            }

            var userAgent = request?.Headers["User-Agent"].ToString();

            var auditLog = new AuditLog
            {
                UserID = userId,
                Action = action,
                ModuleName = string.IsNullOrWhiteSpace(moduleName) ? "System" : moduleName, // Tránh lỗi null module
                Target = target,
                OldValues = oldValues,
                NewValues = newValues,
                IPAddress = ipAddress,
                UserAgent = userAgent?.Length > 500 ? userAgent.Substring(0, 500) : userAgent,
                Severity = severity,
                CreatedAt = DateTime.Now
            };

            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();
        }
    }
}