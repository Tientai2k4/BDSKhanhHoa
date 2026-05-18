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

            filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.ToLower().Trim();

            ViewBag.TotalCount = await query.CountAsync();
            ViewBag.UnreadCount = await query.CountAsync(n => n.IsRead == false);
            ViewBag.ActionCount = await query.CountAsync(n => n.ActionUrl != null && n.ActionUrl != "" && n.IsRead == false);

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
                    query = query.Where(n =>
                        !n.Title.Contains("Lịch hẹn") &&
                        !n.Title.Contains("Tư vấn") &&
                        !n.Title.Contains("Bình luận"));
                    break;

                default:
                    filter = "all";
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

        // ==========================================
        // 2. API CHO CHUÔNG THÔNG BÁO Ở LAYOUT
        // Tự động cập nhật badge + danh sách nhanh
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetLatest()
        {
            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                return Json(new
                {
                    success = false,
                    unreadCount = 0,
                    latestId = 0,
                    items = Array.Empty<object>()
                });
            }

            int unreadCount = await _context.Notifications
                .AsNoTracking()
                .CountAsync(n => n.UserID == userId && n.IsRead == false);

            var notifications = await _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(7)
                .ToListAsync();

            int latestId = notifications.Any() ? notifications.Max(n => n.NotificationID) : 0;

            var items = notifications.Select(n => new
            {
                id = n.NotificationID,
                title = n.Title ?? "Thông báo",
                content = n.Content ?? "",
                isRead = n.IsRead,
                createdAt = n.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                detailUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),
                processUrl = Url.Action("ProcessAction", "Notification", new { id = n.NotificationID }),
                hasAction = !string.IsNullOrWhiteSpace(n.ActionUrl),
                actionText = string.IsNullOrWhiteSpace(n.ActionText) ? "Xem ngay" : n.ActionText,
                icon = GetNotificationIcon(n.Title, n.Content),
                typeName = GetNotificationTypeName(n.Title, n.Content)
            }).ToList();

            return Json(new
            {
                success = true,
                unreadCount,
                latestId,
                items
            });
        }

        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0) return RedirectToAction("Login", "Account");

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

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

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti != null && !string.IsNullOrWhiteSpace(noti.ActionUrl))
            {
                if (!noti.IsRead)
                {
                    noti.IsRead = true;
                    _context.Notifications.Update(noti);
                    await _context.SaveChangesAsync();
                }

                if (Url.IsLocalUrl(noti.ActionUrl))
                {
                    return Redirect(noti.ActionUrl);
                }

                return Redirect($"~{noti.ActionUrl}");
            }

            TempData["Error"] = "Liên kết đã hết hạn hoặc không có sẵn.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                return Json(new { success = false, message = "Lỗi xác thực người dùng." });
            }

            var unreadNotis = await _context.Notifications
                .Where(n => n.UserID == userId && n.IsRead == false)
                .ToListAsync();

            if (unreadNotis.Any())
            {
                foreach (var noti in unreadNotis)
                {
                    noti.IsRead = true;
                }

                _context.Notifications.UpdateRange(unreadNotis);
                await _context.SaveChangesAsync();
            }

            return Json(new { success = true, message = "Đã đánh dấu tất cả thông báo là đã đọc." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            int userId = GetCurrentUserId();
            if (userId <= 0)
            {
                return Json(new { success = false, message = "Lỗi xác thực." });
            }

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti != null)
            {
                _context.Notifications.Remove(noti);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Xóa thông báo thành công." });
            }

            return Json(new { success = false, message = "Thông báo không tồn tại." });
        }

        private string GetNotificationIcon(string? title, string? content)
        {
            string text = $"{title} {content}".ToLower();

            if (text.Contains("lịch hẹn"))
                return "bi-calendar-check-fill";

            if (text.Contains("tư vấn") || text.Contains("khách chờ"))
                return "bi-headset";

            if (text.Contains("bình luận"))
                return "bi-chat-left-text-fill";

            if (text.Contains("tin nhắn") || text.Contains("chat"))
                return "bi-chat-dots-fill";

            if (text.Contains("cảnh báo") || text.Contains("vi phạm"))
                return "bi-shield-exclamation";

            if (text.Contains("duyệt") || text.Contains("đăng tin"))
                return "bi-megaphone-fill";

            return "bi-bell-fill";
        }

        private string GetNotificationTypeName(string? title, string? content)
        {
            string text = $"{title} {content}".ToLower();

            if (text.Contains("lịch hẹn"))
                return "Lịch hẹn";

            if (text.Contains("tư vấn") || text.Contains("khách chờ"))
                return "Tư vấn";

            if (text.Contains("bình luận"))
                return "Bình luận";

            if (text.Contains("tin nhắn") || text.Contains("chat"))
                return "Tin nhắn";

            if (text.Contains("cảnh báo") || text.Contains("vi phạm"))
                return "Cảnh báo";

            return "Hệ thống";
        }
    }
}