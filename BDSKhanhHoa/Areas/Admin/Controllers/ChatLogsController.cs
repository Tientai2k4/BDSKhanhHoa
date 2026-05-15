using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using System.Linq;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class ChatLogsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ChatLogsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var logs = await _context.ChatLogs
                .AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .Take(500)
                .ToListAsync();

            var userIds = logs.Where(l => l.UserID.HasValue).Select(l => l.UserID.Value).Distinct().ToList();
            var usersDict = await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.UserID))
                .ToDictionaryAsync(u => u.UserID, u => !string.IsNullOrEmpty(u.FullName) ? u.FullName : u.Username);

            ViewBag.UserDict = usersDict;

            return View(logs);
        }

        // [TÍNH NĂNG MỚI]: Lấy chi tiết 1 đoạn chat qua AJAX
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var log = await _context.ChatLogs.AsNoTracking().FirstOrDefaultAsync(l => l.LogID == id);
            if (log == null) return NotFound();

            string userName = "Khách vãng lai";
            if (log.UserID.HasValue)
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserID == log.UserID.Value);
                if (user != null) userName = !string.IsNullOrEmpty(user.FullName) ? user.FullName : user.Username;
            }

            return Json(new
            {
                userName = userName,
                userMessage = log.UserMessage,
                botResponse = log.BotResponse,
                time = log.CreatedAt?.ToString("dd/MM/yyyy HH:mm:ss")
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var log = await _context.ChatLogs.FindAsync(id);
            if (log != null)
            {
                _context.ChatLogs.Remove(log);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa bản ghi lịch sử chat thành công!";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}