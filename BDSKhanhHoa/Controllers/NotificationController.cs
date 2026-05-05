using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class NotificationController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<NotificationController> _logger;

        public NotificationController(ApplicationDbContext context, ILogger<NotificationController> logger)
        {
            _context = context;
            _logger = logger;
        }

        // Hàm an toàn để lấy ID người dùng hiện tại
        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        // ==========================================
        // 1. TRANG QUẢN LÝ THÔNG BÁO TỔNG HỢP
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId)) return RedirectToAction("Login", "Account");

            int pageSize = 12;

            // Khởi tạo Query cơ bản lấy thông báo của User hiện tại
            var query = _context.Notifications.AsNoTracking().Where(n => n.UserID == userId);

            // Xử lý bộ lọc chi tiết cho từng loại thông báo
            filter = filter.ToLower().Trim();
            if (filter == "unread")
                query = query.Where(n => n.IsRead == false);
            else if (filter == "action")
                query = query.Where(n => n.ActionUrl != null && n.ActionUrl != "");
            else if (filter == "appointment")
                query = query.Where(n => n.Title.ToLower().Contains("lịch hẹn") || n.Content.ToLower().Contains("lịch hẹn"));
            else if (filter == "consultation")
                query = query.Where(n => n.Title.ToLower().Contains("tư vấn") || n.Title.ToLower().Contains("liên hệ"));
            else if (filter == "system")
                query = query.Where(n => n.Title.ToLower().Contains("vi phạm") || n.Title.ToLower().Contains("khóa") || n.Title.ToLower().Contains("cảnh cáo") || n.Title.ToLower().Contains("từ chối"));

            // Đếm tổng số lượng cho các tab (Badge)
            ViewBag.TotalCount = await _context.Notifications.CountAsync(n => n.UserID == userId);
            ViewBag.UnreadCount = await _context.Notifications.CountAsync(n => n.UserID == userId && n.IsRead == false);
            ViewBag.ActionCount = await _context.Notifications.CountAsync(n => n.UserID == userId && n.ActionUrl != null && n.ActionUrl != "" && n.IsRead == false);
            ViewBag.AppointmentCount = await _context.Notifications.CountAsync(n => n.UserID == userId && (n.Title.ToLower().Contains("lịch hẹn") || n.Content.ToLower().Contains("lịch hẹn")) && n.IsRead == false);
            ViewBag.ConsultationCount = await _context.Notifications.CountAsync(n => n.UserID == userId && (n.Title.ToLower().Contains("tư vấn") || n.Title.ToLower().Contains("liên hệ")) && n.IsRead == false);

            // Xử lý phân trang an toàn
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

        // ==========================================
        // 2. XEM CHI TIẾT & ĐÁNH DẤU ĐÃ ĐỌC
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return RedirectToAction("Login", "Account");

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (notification == null)
            {
                TempData["Error"] = "Thông báo không tồn tại hoặc bạn không có quyền truy cập.";
                return RedirectToAction(nameof(Index));
            }

            // Tự động đánh dấu đã đọc khi click xem chi tiết
            if (!notification.IsRead)
            {
                notification.IsRead = true;
                _context.Notifications.Update(notification);
                await _context.SaveChangesAsync();
            }

            return View(notification);
        }

        // ==========================================
        // 3. XỬ LÝ HÀNH ĐỘNG TỪ THÔNG BÁO (ACTION URL)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ProcessAction(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return RedirectToAction("Login", "Account");

            var noti = await _context.Notifications.FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti != null && !string.IsNullOrWhiteSpace(noti.ActionUrl))
            {
                if (!noti.IsRead)
                {
                    noti.IsRead = true;
                    _context.Notifications.Update(noti);
                    await _context.SaveChangesAsync();
                }

                // Tránh lỗi Open Redirect bằng cách đảm bảo URL là local
                if (Url.IsLocalUrl(noti.ActionUrl))
                {
                    return Redirect(noti.ActionUrl);
                }
                else
                {
                    return Redirect($"~{noti.ActionUrl}"); // Ép về thư mục gốc của website
                }
            }

            TempData["Error"] = "Liên kết xử lý không hợp lệ hoặc đã hết hạn.";
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 4. API ĐÁNH DẤU TẤT CẢ LÀ ĐÃ ĐỌC
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Lỗi xác thực người dùng." });

            var unreadNotifications = await _context.Notifications
                .Where(n => n.UserID == userId && n.IsRead == false)
                .ToListAsync();

            if (unreadNotifications.Any())
            {
                foreach (var noti in unreadNotifications)
                {
                    noti.IsRead = true;
                }
                _context.Notifications.UpdateRange(unreadNotifications);
                await _context.SaveChangesAsync();
            }

            return Json(new { success = true, message = "Đã đánh dấu tất cả là đã đọc." });
        }

        // ==========================================
        // 5. API XÓA THÔNG BÁO (ĐÃ ĐƯỢC CHUẨN HÓA DATA BINDING)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!TryGetCurrentUserId(out int userId)) return Json(new { success = false, message = "Lỗi xác thực người dùng." });

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (notification != null)
            {
                _context.Notifications.Remove(notification);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Xóa thông báo thành công." });
            }

            return Json(new { success = false, message = "Không tìm thấy thông báo cần xóa hoặc bạn không có quyền." });
        }
    }
}