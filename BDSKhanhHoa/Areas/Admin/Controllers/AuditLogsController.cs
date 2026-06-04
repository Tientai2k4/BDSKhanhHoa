using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    public class AuditLogListItemViewModel
    {
        public int LogID { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Severity { get; set; }
        public string? ModuleName { get; set; }
        public string? Action { get; set; }
        public string? Target { get; set; }
        public string? IPAddress { get; set; }
        public int? UserID { get; set; }
        public string? UserFullName { get; set; }
        public int? UserRoleID { get; set; }
    }

    public class AuditLogStatsViewModel
    {
        public int TotalLogs { get; set; }
        public int TodayLogs { get; set; }
        public int DangerLogs { get; set; }
        public int AuthLogs { get; set; }
    }

    public class AuditLogIndexViewModel
    {
        public List<AuditLogListItemViewModel> Logs { get; set; } = new();
        public List<string> ModuleOptions { get; set; } = new();

        public int TotalLogs { get; set; }
        public int TodayLogs { get; set; }
        public int DangerLogs { get; set; }
        public int AuthLogs { get; set; }

        public int CurrentPage { get; set; }
        public int TotalPages { get; set; }
        public int TotalItems { get; set; }

        public string Keyword { get; set; } = "";
        public string Module { get; set; } = "";
        public string Severity { get; set; } = "";
        public string DateRange { get; set; } = "all";
    }

    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class AuditLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AuditLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static IQueryable<AuditLog> ApplyFilter(
            IQueryable<AuditLog> query,
            string? keyword,
            string? module,
            string? severity,
            string? dateRange)
        {
            keyword = Normalize(keyword);
            module = Normalize(module);
            severity = Normalize(severity);
            dateRange = Normalize(dateRange).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                // Không dùng ToLower() trên cột DB để tránh làm query nặng hơn.
                // SQL Server thường dùng collation không phân biệt hoa thường.
                string like = $"%{keyword}%";

                query = query.Where(x =>
                    EF.Functions.Like(x.Action ?? "", like) ||
                    EF.Functions.Like(x.Target ?? "", like) ||
                    EF.Functions.Like(x.IPAddress ?? "", like) ||
                    EF.Functions.Like(x.ModuleName ?? "", like) ||
                    (x.User != null && (
                        EF.Functions.Like(x.User.Username ?? "", like) ||
                        EF.Functions.Like(x.User.FullName ?? "", like) ||
                        EF.Functions.Like(x.User.Email ?? "", like)
                    ))
                );
            }

            if (!string.IsNullOrWhiteSpace(module))
            {
                query = query.Where(x => x.ModuleName == module);
            }

            if (!string.IsNullOrWhiteSpace(severity))
            {
                query = query.Where(x => x.Severity == severity);
            }

            var today = DateTime.Now.Date;

            switch (dateRange)
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

            keyword = Normalize(keyword);
            module = Normalize(module);
            severity = Normalize(severity);
            dateRange = string.IsNullOrWhiteSpace(dateRange) ? "all" : dateRange.Trim();

            if (page < 1) page = 1;

            var today = DateTime.Now.Date;

            var baseQuery = _context.AuditLogs.AsNoTracking();

            var filteredQuery = ApplyFilter(
                baseQuery,
                keyword,
                module,
                severity,
                dateRange
            );

            // Gom 4 thống kê vào 1 query thay vì 4 CountAsync riêng.
            var stats = await baseQuery
                .GroupBy(x => 1)
                .Select(g => new AuditLogStatsViewModel
                {
                    TotalLogs = g.Count(),
                    TodayLogs = g.Count(x => x.CreatedAt >= today),
                    DangerLogs = g.Count(x => x.Severity == "Danger" || x.Severity == "Critical"),
                    AuthLogs = g.Count(x => x.ModuleName == "Authentication" || x.ModuleName == "Account")
                })
                .FirstOrDefaultAsync() ?? new AuditLogStatsViewModel();

            // Dropdown module chỉ lấy text module, không Include, không lấy nguyên log.
            var moduleOptions = await baseQuery
                .Where(x => x.ModuleName != null && x.ModuleName != "")
                .Select(x => x.ModuleName!)
                .Distinct()
                .OrderBy(x => x)
                .Take(100)
                .ToListAsync();

            int totalItems = await filteredQuery.CountAsync();

            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page > totalPages) page = totalPages;

            // Quan trọng: chỉ SELECT các cột cần hiển thị ở danh sách.
            // Không kéo OldValues/NewValues/UserAgent vì các cột này có thể rất dài.
            var logs = await filteredQuery
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.LogID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new AuditLogListItemViewModel
                {
                    LogID = x.LogID,
                    CreatedAt = x.CreatedAt,
                    Severity = x.Severity,
                    ModuleName = x.ModuleName,
                    Action = x.Action,
                    Target = x.Target,
                    IPAddress = x.IPAddress,
                    UserID = x.UserID,
                    UserFullName = x.User != null ? x.User.FullName : null,
                    UserRoleID = x.User != null ? x.User.RoleID : null
                })
                .ToListAsync();

            var vm = new AuditLogIndexViewModel
            {
                Logs = logs,
                ModuleOptions = moduleOptions,

                TotalLogs = stats.TotalLogs,
                TodayLogs = stats.TodayLogs,
                DangerLogs = stats.DangerLogs,
                AuthLogs = stats.AuthLogs,

                CurrentPage = page,
                TotalPages = totalPages,
                TotalItems = totalItems,

                Keyword = keyword,
                Module = module,
                Severity = severity,
                DateRange = dateRange
            };

            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            // Details chỉ xem 1 log nên Include User vẫn ổn.
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
            const int maxExportRows = 50000;

            var query = ApplyFilter(
                _context.AuditLogs.AsNoTracking(),
                keyword,
                module,
                severity,
                dateRange
            );

            // Export cũng chỉ lấy cột cần thiết, không kéo nguyên entity.
            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.LogID)
                .Take(maxExportRows)
                .Select(x => new
                {
                    x.LogID,
                    x.CreatedAt,
                    x.Severity,
                    x.ModuleName,
                    x.Action,
                    x.Target,
                    x.UserID,
                    UserName = x.User != null ? x.User.FullName : null,
                    x.IPAddress,
                    x.UserAgent
                })
                .ToListAsync();

            var sb = new StringBuilder();
            sb.Append('\uFEFF');
            sb.AppendLine("LogID,Thời gian,Mức độ,Phân khu,Hành động,Đối tượng,Mã User,Tên người dùng,IP,Trình duyệt");

            foreach (var log in logs)
            {
                sb.AppendLine(string.Join(",",
                    Csv(log.LogID.ToString()),
                    Csv(log.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")),
                    Csv(log.Severity),
                    Csv(log.ModuleName),
                    Csv(log.Action),
                    Csv(log.Target),
                    Csv(log.UserID.ToString()),
                    Csv(string.IsNullOrWhiteSpace(log.UserName) ? "Hệ thống" : log.UserName),
                    Csv(log.IPAddress),
                    Csv(log.UserAgent)
                ));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return File(bytes, "text/csv", $"NhatKyHeThong_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }

        private static string Csv(string? value)
        {
            value ??= "";
            value = value.Replace("\"", "\"\"");
            return $"\"{value}\"";
        }
    }
}