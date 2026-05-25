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

        private int GetCurrentUserId()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(userIdStr, out int userId);
            return userId;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return RedirectToAction("Login", "Account");
            }

            int pageSize = 12;

            filter = string.IsNullOrWhiteSpace(filter)
                ? "all"
                : filter.ToLower().Trim();

            var baseQuery = _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == userId);

            ViewBag.TotalCount = await baseQuery.CountAsync();
            ViewBag.UnreadCount = await baseQuery.CountAsync(n => n.IsRead == false);
            ViewBag.ActionCount = await baseQuery.CountAsync(n => n.ActionUrl != null && n.ActionUrl != "" && n.IsRead == false);

            ViewBag.AppointmentCount = await baseQuery.CountAsync(n => n.Title.Contains("Lịch hẹn") && n.IsRead == false);
            ViewBag.ConsultationCount = await baseQuery.CountAsync(n =>
                (n.Title.Contains("Tư vấn") ||
                 n.Title.Contains("Bình luận") ||
                 n.Title.Contains("báo cáo") ||
                 n.Title.Contains("vi phạm")) &&
                n.IsRead == false);

            IQueryable<Notification> query = baseQuery;

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
                    query = query.Where(n =>
                        n.Title.Contains("Tư vấn") ||
                        n.Title.Contains("Bình luận") ||
                        n.Title.Contains("báo cáo") ||
                        n.Title.Contains("vi phạm"));
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
                .OrderByDescending(n => n.IsRead == false)
                .ThenByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentFilter = filter;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(notifications);
        }

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
                .OrderByDescending(n => n.IsRead == false)
                .ThenByDescending(n => n.CreatedAt)
                .Take(7)
                .ToListAsync();

            int latestId = notifications.Any()
                ? notifications.Max(n => n.NotificationID)
                : 0;

            var items = notifications.Select(n => new
            {
                id = n.NotificationID,
                title = n.Title ?? "Thông báo",
                content = n.Content ?? "",
                isRead = n.IsRead,
                createdAt = n.CreatedAt.ToString("HH:mm dd/MM/yyyy"),

                // BẤM THÔNG BÁO PHẢI VÀO CHI TIẾT TRƯỚC
                detailUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),
                processUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),

                hasAction = !string.IsNullOrWhiteSpace(n.ActionUrl),
                actionText = string.IsNullOrWhiteSpace(n.ActionText) ? "Xem chi tiết" : n.ActionText,
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
        public async Task<IActionResult> GetBellNotifications()
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return Json(new
                {
                    success = false,
                    unreadCount = 0,
                    totalCount = 0,
                    data = Array.Empty<object>()
                });
            }

            var notifications = await _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == userId)
                .OrderByDescending(n => n.IsRead == false)
                .ThenByDescending(n => n.CreatedAt)
                .Take(10)
                .Select(n => new
                {
                    id = n.NotificationID,
                    title = n.Title ?? "Thông báo",
                    content = n.Content ?? "",
                    createdAt = n.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                    isRead = n.IsRead,
                    hasAction = !string.IsNullOrWhiteSpace(n.ActionUrl),

                    // BẤM CHUÔNG THÔNG BÁO CŨNG PHẢI VÀO CHI TIẾT TRƯỚC
                    processUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),
                    detailsUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),

                    type =
                        n.Title.Contains("khóa") ||
                        n.Title.Contains("vi phạm") ||
                        n.Title.Contains("báo cáo") ||
                        n.Title.Contains("cảnh báo")
                            ? "danger"
                            : n.Title.Contains("xử lý") ||
                              n.Title.Contains("thành công") ||
                              n.Title.Contains("ghi nhận")
                                ? "success"
                                : "system"
                })
                .ToListAsync();

            int unreadCount = await _context.Notifications
                .AsNoTracking()
                .CountAsync(n => n.UserID == userId && n.IsRead == false);

            int totalCount = await _context.Notifications
                .AsNoTracking()
                .CountAsync(n => n.UserID == userId);

            return Json(new
            {
                success = true,
                unreadCount,
                totalCount,
                data = notifications
            });
        }
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return RedirectToAction("Login", "Account");
            }

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
                await _context.SaveChangesAsync();
            }

            return View(notification);
        }

        [HttpGet]
        public async Task<IActionResult> ProcessAction(int id)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return RedirectToAction("Login", "Account");
            }

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

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
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkAllAsRead()
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi xác thực người dùng."
                });
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

                await _context.SaveChangesAsync();
            }

            return Json(new
            {
                success = true,
                message = "Đã đánh dấu tất cả thông báo là đã đọc."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi xác thực."
                });
            }

            var noti = await _context.Notifications
                .FirstOrDefaultAsync(n => n.NotificationID == id && n.UserID == userId);

            if (noti == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Thông báo không tồn tại."
                });
            }

            _context.Notifications.Remove(noti);
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Xóa thông báo thành công."
            });
        }

        private string GetNotificationIcon(string? title, string? content)
        {
            string text = $"{title} {content}".ToLower();

            if (text.Contains("lịch hẹn")) return "bi-calendar-check-fill";
            if (text.Contains("tư vấn") || text.Contains("khách chờ")) return "bi-headset";
            if (text.Contains("bình luận")) return "bi-chat-left-text-fill";
            if (text.Contains("tin nhắn") || text.Contains("chat") || text.Contains("trò chuyện")) return "bi-chat-dots-fill";
            if (text.Contains("báo cáo") || text.Contains("cảnh báo") || text.Contains("vi phạm") || text.Contains("khóa")) return "bi-shield-exclamation";
            if (text.Contains("duyệt") || text.Contains("đăng tin")) return "bi-megaphone-fill";

            return "bi-bell-fill";
        }

        private string GetNotificationTypeName(string? title, string? content)
        {
            string text = $"{title} {content}".ToLower();

            if (text.Contains("lịch hẹn")) return "Lịch hẹn";
            if (text.Contains("tư vấn") || text.Contains("khách chờ")) return "Tư vấn";
            if (text.Contains("bình luận")) return "Bình luận";
            if (text.Contains("tin nhắn") || text.Contains("chat") || text.Contains("trò chuyện")) return "Tin nhắn";
            if (text.Contains("báo cáo") || text.Contains("cảnh báo") || text.Contains("vi phạm") || text.Contains("khóa")) return "Cảnh báo";

            return "Hệ thống";
        }

        private string GetBellType(string? title, string? content)
        {
            string text = $"{title} {content}".ToLower();

            if (text.Contains("khóa") || text.Contains("vi phạm") || text.Contains("báo cáo") || text.Contains("cảnh báo"))
            {
                return "danger";
            }

            if (text.Contains("xử lý") || text.Contains("thành công") || text.Contains("duyệt"))
            {
                return "success";
            }

            if (text.Contains("lịch hẹn"))
            {
                return "appointment";
            }

            if (text.Contains("tư vấn") || text.Contains("liên hệ") || text.Contains("bình luận"))
            {
                return "customer";
            }

            if (text.Contains("cần") || text.Contains("chờ"))
            {
                return "action";
            }

            return "system";
        }
    }
}