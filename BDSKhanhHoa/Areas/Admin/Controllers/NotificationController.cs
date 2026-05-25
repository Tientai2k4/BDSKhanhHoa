using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class NotificationController : Controller
    {
        private readonly ApplicationDbContext _context;

        public NotificationController(ApplicationDbContext context)
        {
            _context = context;
        }

        private int GetCurrentAdminId()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdStr, out int userId);
            return userId;
        }

        private static string CleanText(string? value, int maxLength = 140)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Không có nội dung chi tiết.";
            }

            string text = Regex.Replace(value, "<.*?>", string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength).Trim() + "...";
            }

            return text;
        }

        private static string GetNotificationType(string? title, string? content, string? actionUrl)
        {
            string raw = $"{title} {content} {actionUrl}".ToLowerInvariant();

            if (raw.Contains("lịch hẹn"))
            {
                return "appointment";
            }

            if (raw.Contains("tư vấn") || raw.Contains("liên hệ") || raw.Contains("bình luận") || raw.Contains("khách hàng"))
            {
                return "customer";
            }

            if (raw.Contains("từ chối") || raw.Contains("vi phạm") || raw.Contains("khóa") || raw.Contains("gỡ bỏ") || raw.Contains("trùng lặp") || raw.Contains("thất bại"))
            {
                return "danger";
            }

            if (raw.Contains("duyệt") || raw.Contains("thành công") || raw.Contains("xác nhận"))
            {
                return "success";
            }

            if (!string.IsNullOrWhiteSpace(actionUrl))
            {
                return "action";
            }

            return "system";
        }

        // ==========================================
        // 1. TRANG QUẢN LÝ HỘP THƯ ADMIN
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            int adminId = GetCurrentAdminId();
            int pageSize = 12;

            var query = _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == adminId);

            filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.ToLower().Trim();

            ViewBag.TotalCount = await query.CountAsync();
            ViewBag.UnreadCount = await query.CountAsync(n => n.IsRead == false);
            ViewBag.ActionCount = await query.CountAsync(n => n.ActionUrl != null && n.ActionUrl != "" && n.IsRead == false);
            ViewBag.AppointmentCount = await query.CountAsync(n => n.Title.Contains("Lịch hẹn") && n.IsRead == false);
            ViewBag.ConsultationCount = await query.CountAsync(n => (n.Title.Contains("Tư vấn") || n.Title.Contains("Bình luận") || n.Title.Contains("Liên hệ")) && n.IsRead == false);

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
                    query = query.Where(n => n.Title.Contains("Tư vấn") || n.Title.Contains("Bình luận") || n.Title.Contains("Liên hệ"));
                    break;

                case "system":
                    query = query.Where(n =>
                        !n.Title.Contains("Lịch hẹn") &&
                        !n.Title.Contains("Tư vấn") &&
                        !n.Title.Contains("Bình luận") &&
                        !n.Title.Contains("Liên hệ"));
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
        // 2. API CHUÔNG THÔNG BÁO TRÊN ADMIN LAYOUT
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetBellNotifications()
        {
            int adminId = GetCurrentAdminId();

            if (adminId <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Phiên đăng nhập không hợp lệ."
                });
            }

            var baseQuery = _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == adminId);

            int totalCount = await baseQuery.CountAsync();
            int unreadCount = await baseQuery.CountAsync(n => n.IsRead == false);
            int actionCount = await baseQuery.CountAsync(n => n.IsRead == false && n.ActionUrl != null && n.ActionUrl != "");

            var latestNotifications = await baseQuery
                .OrderBy(n => n.IsRead)
                .ThenByDescending(n => n.CreatedAt)
                .Take(8)
                .Select(n => new
                {
                    n.NotificationID,
                    n.Title,
                    n.Content,
                    n.ActionUrl,
                    n.ActionText,
                    n.IsRead,
                    n.CreatedAt
                })
                .ToListAsync();

            var data = latestNotifications.Select(n => new
            {
                id = n.NotificationID,
                title = string.IsNullOrWhiteSpace(n.Title) ? "Thông báo từ hệ thống" : n.Title,
                content = CleanText(n.Content, 150),
                createdAt = n.CreatedAt.ToString("HH:mm - dd/MM/yyyy"),
                isRead = n.IsRead,
                hasAction = !string.IsNullOrWhiteSpace(n.ActionUrl),
                actionText = string.IsNullOrWhiteSpace(n.ActionText) ? "Xem chi tiết" : n.ActionText,
                detailsUrl = Url.Action("Details", "Notification", new { area = "Admin", id = n.NotificationID }),
                processUrl = Url.Action("ProcessAction", "Notification", new { area = "Admin", id = n.NotificationID }),
                type = GetNotificationType(n.Title, n.Content, n.ActionUrl)
            }).ToList();

            return Json(new
            {
                success = true,
                totalCount,
                unreadCount,
                actionCount,
                data
            });
        }

        // ==========================================
        // 3. XEM CHI TIẾT THÔNG BÁO
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            int adminId = GetCurrentAdminId();

            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == adminId);

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

        // ==========================================
        // 4. XỬ LÝ HÀNH ĐỘNG CỦA THÔNG BÁO
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ProcessAction(int id)
        {
            int adminId = GetCurrentAdminId();

            if (adminId <= 0)
            {
                return RedirectToAction("Login", "Account", new { area = "" });
            }

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == adminId);

            if (noti == null)
            {
                TempData["Error"] = "Thông báo không tồn tại hoặc bạn không có quyền truy cập.";
                return RedirectToAction(nameof(Index));
            }

            if (!noti.IsRead)
            {
                noti.IsRead = true;
                await _context.SaveChangesAsync();
            }

            if (string.IsNullOrWhiteSpace(noti.ActionUrl))
            {
                return RedirectToAction(nameof(Details), new { id = noti.NotificationID });
            }

            if (Url.IsLocalUrl(noti.ActionUrl))
            {
                return Redirect(noti.ActionUrl);
            }

            if (noti.ActionUrl.StartsWith("/"))
            {
                return Redirect(noti.ActionUrl);
            }

            TempData["Error"] = "Liên kết thông báo không hợp lệ.";
            return RedirectToAction(nameof(Details), new { id = noti.NotificationID });
        }
        // ==========================================
        // 5. ĐÁNH DẤU TẤT CẢ ĐÃ ĐỌC
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            int adminId = GetCurrentAdminId();

            var unreadNotis = await _context.Notifications
                .Where(n => n.UserID == adminId && n.IsRead == false)
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

            return Json(new
            {
                success = true,
                message = "Đã đánh dấu tất cả thông báo là đã đọc."
            });
        }

        // ==========================================
        // 6. XÓA THÔNG BÁO
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            int adminId = GetCurrentAdminId();

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == adminId);

            if (noti != null)
            {
                _context.Notifications.Remove(noti);
                await _context.SaveChangesAsync();

                return Json(new
                {
                    success = true,
                    message = "Xóa thông báo thành công."
                });
            }

            return Json(new
            {
                success = false,
                message = "Thông báo không tồn tại."
            });
        }
    }
}