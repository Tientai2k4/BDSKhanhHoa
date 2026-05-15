using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // BẮT BUỘC: Chỉ Admin có quyền xem
    public class AuditLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AuditLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private IQueryable<AuditLog> BuildFilterQuery(string? keyword, string? module, string? severity, string dateRange)
        {
            var query = _context.AuditLogs
                .AsNoTracking()
                .Include(x => x.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim().ToLower();
                query = query.Where(x =>
                    (x.Action != null && x.Action.ToLower().Contains(keyword)) ||
                    (x.Target != null && x.Target.ToLower().Contains(keyword)) ||
                    (x.IPAddress != null && x.IPAddress.Contains(keyword)) ||
                    (x.User != null && (
                        (x.User.Username != null && x.User.Username.ToLower().Contains(keyword)) ||
                        (x.User.FullName != null && x.User.FullName.ToLower().Contains(keyword))
                    )));
            }

            if (!string.IsNullOrWhiteSpace(module))
            {
                query = query.Where(x => x.ModuleName == module.Trim());
            }

            if (!string.IsNullOrWhiteSpace(severity))
            {
                query = query.Where(x => x.Severity == severity.Trim());
            }

            var today = DateTime.Now.Date;
            switch (dateRange?.Trim().ToLowerInvariant())
            {
                case "today":
                    query = query.Where(x => x.CreatedAt >= today && x.CreatedAt < today.AddDays(1));
                    break;
                case "week":
                    query = query.Where(x => x.CreatedAt >= today.AddDays(-7));
                    break;
                case "month":
                    query = query.Where(x => x.CreatedAt >= today.AddMonths(-1));
                    break;
            }

            return query;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? keyword = null,
            string? module = null,
            string? severity = null,
            string dateRange = "all",
            int page = 1)
        {
            const int pageSize = 20;
            var today = DateTime.Now.Date;

            var query = BuildFilterQuery(keyword, module, severity, dateRange);

            // Bổ sung lấy danh sách Module để đổ ra dropdown (Lọc bỏ các module null/empty)
            ViewBag.ModuleOptions = await _context.AuditLogs
                .AsNoTracking()
                .Where(x => !string.IsNullOrWhiteSpace(x.ModuleName))
                .Select(x => x.ModuleName!)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            ViewBag.TotalLogs = await _context.AuditLogs.CountAsync();
            ViewBag.DangerLogs = await _context.AuditLogs.CountAsync(x => x.Severity == "Danger" || x.Severity == "Critical");
            ViewBag.TodayLogs = await _context.AuditLogs.CountAsync(x => x.CreatedAt >= today);
            ViewBag.AuthLogs = await _context.AuditLogs.CountAsync(x => x.ModuleName == "Authentication" || x.ModuleName == "Account");

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.LogID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.Keyword = keyword;
            ViewBag.Module = module;
            ViewBag.Severity = severity;
            ViewBag.DateRange = dateRange;

            return View(logs);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var log = await _context.AuditLogs
                .AsNoTracking()
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.LogID == id);

            if (log == null) return NotFound();

            return View(log);
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(
            string? keyword = null,
            string? module = null,
            string? severity = null,
            string dateRange = "all")
        {
            var query = BuildFilterQuery(keyword, module, severity, dateRange);

            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            var sb = new StringBuilder();
            sb.Append("\uFEFF"); // BOM marker chống lỗi font Tiếng Việt
            sb.AppendLine("LogID,Thời gian,Mức độ,Phân khu,Hành động,Đối tượng,Mã NV/User,Tên Người Dùng,IP,Trình duyệt");

            foreach (var log in logs)
            {
                var userName = log.User?.FullName?.Replace(",", " ") ?? "Hệ thống";
                var actionStr = log.Action?.Replace(",", " ") ?? "";
                var targetStr = log.Target?.Replace(",", " ") ?? "";
                var moduleStr = log.ModuleName?.Replace(",", " ") ?? "";
                var ip = log.IPAddress ?? "N/A";
                var agent = log.UserAgent?.Replace(",", " ") ?? "N/A";
                var dateStr = log.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");

                sb.AppendLine($"{log.LogID},{dateStr},{log.Severity},{moduleStr},{actionStr},{targetStr},{log.UserID},{userName},{ip},{agent}");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"NhatKyHeThong_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }
    }
}