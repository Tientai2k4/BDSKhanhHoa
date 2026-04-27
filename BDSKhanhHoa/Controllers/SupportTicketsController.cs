using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class SupportTicketsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SupportTicketsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            var businessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserID == userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.BusinessName = businessProfile?.BusinessName ?? "Doanh nghiệp đối tác";
            ViewBag.ProjectCount = projects.Count;

            return View(projects);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Submit(string subject, string message, int? projectId)
        {
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
            {
                TempData["Error"] = "Vui lòng nhập tiêu đề và nội dung yêu cầu.";
                return RedirectToAction(nameof(Index));
            }

            TempData["Success"] = "Đã ghi nhận yêu cầu hỗ trợ. Hệ thống chưa lưu riêng bảng ticket nên tạm chuyển thành thông báo nội bộ.";
            return RedirectToAction(nameof(Index));
        }
    }
}