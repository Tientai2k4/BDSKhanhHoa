using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

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

        // ==========================================
        // 1. TRANG QUẢN LÝ THÔNG BÁO CỦA USER
        // ==========================================
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            int pageSize = 12; // Ưu tiên số chẵn để chia grid đẹp hơn

            var query = _context.Notifications.Where(n => n.UserID == currentUserId);

            if (filter == "unread") query = query.Where(n => !n.IsRead);
            if (filter == "action") query = query.Where(n => n.ActionUrl != null); // Lọc riêng các yêu cầu cần xử lý

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var notifications = await query
                .OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.UnreadCount = await _context.Notifications.CountAsync(n => n.UserID == currentUserId && !n.IsRead);
            ViewBag.ActionCount = await _context.Notifications.CountAsync(n => n.UserID == currentUserId && n.ActionUrl != null && !n.IsRead);
            ViewBag.CurrentFilter = filter;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(notifications);
        }

        // ==========================================
        // 2. XEM CHI TIẾT & CHUYỂN TRẠNG THÁI
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == currentUserId);

            if (notification == null)
            {
                TempData["Error"] = "Thông báo không tồn tại hoặc bạn không có quyền truy cập.";
                return RedirectToAction(nameof(Index));
            }

            // Tự động đánh dấu đã đọc
            if (!notification.IsRead)
            {
                notification.IsRead = true;
                _context.Update(notification);
                await _context.SaveChangesAsync();
            }

            return View(notification);
        }

        // ==========================================
        // 3. API ĐIỀU HƯỚNG HÀNH ĐỘNG (XỬ LÝ LỖI)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ProcessAction(int id)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var noti = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == currentUserId);

            if (noti != null && !string.IsNullOrEmpty(noti.ActionUrl))
            {
                // Đảm bảo thông báo đã được đọc khi người dùng bấm xử lý
                if (!noti.IsRead)
                {
                    noti.IsRead = true;
                    await _context.SaveChangesAsync();
                }
                return Redirect(noti.ActionUrl); // Chuyển hướng đến URL do Admin thiết lập (VD: /Property/Edit/123)
            }

            TempData["Error"] = "Liên kết xử lý không hợp lệ hoặc đã hết hạn.";
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 4. CÁC API HỖ TRỢ KHÁC
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> MarkAllAsRead()
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var unreadNotifications = await _context.Notifications.Where(n => n.UserID == currentUserId && !n.IsRead).ToListAsync();

            if (unreadNotifications.Any())
            {
                foreach (var noti in unreadNotifications) noti.IsRead = true;
                _context.UpdateRange(unreadNotifications);
                await _context.SaveChangesAsync();
            }
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            int currentUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var notification = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == currentUserId);

            if (notification != null)
            {
                _context.Notifications.Remove(notification);
                await _context.SaveChangesAsync();
                return Json(new { success = true });
            }
            return Json(new { success = false });
        }
    }
}