using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class NotificationController : Controller
    {
        private readonly ApplicationDbContext _context;

        public NotificationController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetCurrentUserId()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdStr, out int userId);
            return userId;
        }

        // ==========================================
        // 1. TRANG QUẢN LÝ HỘP THƯ
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return RedirectToAction("Login", "Account");

            int pageSize = 12;
            var query = _context.Notifications.AsNoTracking().Where(n => n.UserID == userId);

            filter = filter.ToLower().Trim();

            // Xử lý đếm Badges trước khi áp dụng filter
            ViewBag.TotalCount = await query.CountAsync();
            ViewBag.UnreadCount = await query.CountAsync(n => n.IsRead == false);
            ViewBag.ActionCount = await query.CountAsync(n => n.ActionUrl != null && n.ActionUrl != "" && n.IsRead == false);

            // Lọc theo keyword chính xác như đã setup ở PropertyController
            ViewBag.AppointmentCount = await query.CountAsync(n => n.Title.Contains("Lịch hẹn") && n.IsRead == false);
            ViewBag.ConsultationCount = await query.CountAsync(n => (n.Title.Contains("Tư vấn") || n.Title.Contains("Bình luận")) && n.IsRead == false);

            switch (filter)
            {
                case "unread":
                    query = query.Where(n => n.IsRead == false);
                    break;
                case "action":
                    query = query.Where(n => n.ActionUrl != null && n.ActionUrl != "");
                    break;
                case "appointment":
                    query = query.Where(n => n.Title.Contains("Lịch hẹn"));
                    break;
                case "consultation":
                    query = query.Where(n => n.Title.Contains("Tư vấn") || n.Title.Contains("Bình luận"));
                    break;
                case "system":
                    query = query.Where(n => !n.Title.Contains("Lịch hẹn") && !n.Title.Contains("Tư vấn") && !n.Title.Contains("Bình luận"));
                    break;
            }

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var notifications = await query
                .OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentFilter = filter;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(notifications);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return RedirectToAction("Login", "Account");

            var notification = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (notification == null)
            {
                TempData["Error"] = "Thông báo không tồn tại hoặc bạn không có quyền truy cập.";
                return RedirectToAction(nameof(Index));
            }

            if (!notification.IsRead)
            {
                notification.IsRead = true;
                _context.Notifications.Update(notification);
                await _context.SaveChangesAsync();
            }

            return View(notification);
        }

        [HttpGet]
        public async Task<IActionResult> ProcessAction(int id)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return RedirectToAction("Login", "Account");

            var noti = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti != null && !string.IsNullOrWhiteSpace(noti.ActionUrl))
            {
                if (!noti.IsRead)
                {
                    noti.IsRead = true;
                    _context.Notifications.Update(noti);
                    await _context.SaveChangesAsync();
                }

                if (Url.IsLocalUrl(noti.ActionUrl)) return Redirect(noti.ActionUrl);
                else return Redirect($"~{noti.ActionUrl}");
            }

            TempData["Error"] = "Liên kết đã hết hạn hoặc không có sẵn.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return Json(new { success = false, message = "Lỗi xác thực người dùng." });

            var unreadNotis = await _context.Notifications.Where(n => n.UserID == userId && n.IsRead == false).ToListAsync();

            if (unreadNotis.Any())
            {
                foreach (var noti in unreadNotis) noti.IsRead = true;
                _context.Notifications.UpdateRange(unreadNotis);
                await _context.SaveChangesAsync();
            }
            return Json(new { success = true, message = "Đã dọn dẹp hộp thư." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return Json(new { success = false, message = "Lỗi xác thực." });

            var noti = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti != null)
            {
                _context.Notifications.Remove(noti);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Xóa thông báo thành công." });
            }
            return Json(new { success = false, message = "Thông báo không tồn tại." });
        }
    }
}