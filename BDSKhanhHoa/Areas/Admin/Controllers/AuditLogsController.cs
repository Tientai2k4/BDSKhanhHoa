using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // BẮT BUỘC: Chỉ Admin có quyền xem nhật ký hệ thống
    [Route("Admin/[controller]/[action]")]
    public class AuditLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AuditLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Tách logic Query ra một hàm riêng để dùng chung cho cả Index và Export
        private IQueryable<AuditLog> BuildFilterQuery(string? keyword, string? target, string? action, string dateRange)
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
                    (x.User != null && (
                        (x.User.Username != null && x.User.Username.ToLower().Contains(keyword)) ||
                        (x.User.FullName != null && x.User.FullName.ToLower().Contains(keyword))
                    )));
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                query = query.Where(x => x.Target == target.Trim());
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(x => x.Action == action.Trim());
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
            string? target = null,
            string? action = null,
            string dateRange = "all",
            int page = 1)
        {
            const int pageSize = 20;
            var today = DateTime.Now.Date;

            // Dùng hàm chung để build query
            var query = BuildFilterQuery(keyword, target, action, dateRange);

            // Lấy danh sách dropdown (Tối ưu performance bằng Distinct)
            ViewBag.TargetOptions = await _context.AuditLogs
                .AsNoTracking()
                .Where(x => !string.IsNullOrEmpty(x.Target))
                .Select(x => x.Target!)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            ViewBag.ActionOptions = await _context.AuditLogs
                .AsNoTracking()
                .Where(x => !string.IsNullOrEmpty(x.Action))
                .Select(x => x.Action!)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            // Thống kê nhanh
            ViewBag.TotalLogs = await _context.AuditLogs.CountAsync();
            ViewBag.TodayLogs = await _context.AuditLogs.CountAsync(x => x.CreatedAt >= today);
            ViewBag.WeekLogs = await _context.AuditLogs.CountAsync(x => x.CreatedAt >= today.AddDays(-7));
            ViewBag.MonthLogs = await _context.AuditLogs.CountAsync(x => x.CreatedAt >= today.AddMonths(-1));

            // Phân trang
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

            // Lưu state bộ lọc
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.Keyword = keyword;
            ViewBag.Target = target;
            ViewBag.Action = action;
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

        // TÍNH NĂNG MỚI: Xuất file CSV báo cáo kiểm toán
        [HttpGet]
        public async Task<IActionResult> ExportCsv(
            string? keyword = null,
            string? target = null,
            string? action = null,
            string dateRange = "all")
        {
            var query = BuildFilterQuery(keyword, target, action, dateRange);

            var logs = await query
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            var sb = new StringBuilder();
            // Thêm BOM để Excel đọc được tiếng Việt UTF-8
            sb.Append("\uFEFF");
            sb.AppendLine("LogID,Thời gian,Mã người dùng,Tên người dùng,Hành động,Đối tượng");

            foreach (var log in logs)
            {
                var userName = log.User?.FullName?.Replace(",", " ") ?? "Hệ thống";
                var actionStr = log.Action?.Replace(",", " ") ?? "";
                var targetStr = log.Target?.Replace(",", " ") ?? "";
                var dateStr = log.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");

                sb.AppendLine($"{log.LogID},{dateStr},{log.UserID},{userName},{actionStr},{targetStr}");
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var fileName = $"NhatKyHeThong_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return File(bytes, "text/csv", fileName);
        }
    }
}