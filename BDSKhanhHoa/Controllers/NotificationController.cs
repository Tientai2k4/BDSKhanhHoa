using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System.Net;
using System.Text.RegularExpressions;

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

        private async Task<bool> ShouldUseBusinessLayoutAsync(int userId)
        {
            if (userId <= 0)
            {
                return false;
            }

            bool hasBusinessProfile = await _context.BusinessProfiles
                .AsNoTracking()
                .AnyAsync(b => b.UserID == userId);

            if (hasBusinessProfile)
            {
                return true;
            }

            return await _context.Projects
                .AsNoTracking()
                .AnyAsync(p => p.OwnerUserID == userId && !p.IsDeleted);
        }

        private void SetNotificationLayout(bool useBusinessLayout)
        {
            ViewBag.IsBusinessLayout = useBusinessLayout;
            ViewBag.NotificationLayout = useBusinessLayout
                ? "~/Views/Shared/_BusinessLayout.cshtml"
                : "~/Views/Shared/_UserLayout.cshtml";
        }


        [HttpGet]
        public async Task<IActionResult> Index(string filter = "all", int page = 1)
        {
            int userId = GetCurrentUserId();

            if (userId <= 0)
            {
                return RedirectToAction("Login", "Account");
            }

            bool useBusinessLayout = await ShouldUseBusinessLayoutAsync(userId);
            SetNotificationLayout(useBusinessLayout);

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
                 n.Title.Contains("Liên hệ") ||
                 n.Title.Contains("Bình luận") ||
                 n.Title.Contains("trả lời bình luận") ||
                 n.Title.Contains("Trả lời bình luận") ||
                 n.Title.Contains("báo cáo") ||
                 n.Title.Contains("vi phạm") ||
                 n.Title.Contains("khách")) &&
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
                        n.Title.Contains("Liên hệ") ||
                        n.Title.Contains("Bình luận") ||
                        n.Title.Contains("trả lời bình luận") ||
                        n.Title.Contains("Trả lời bình luận") ||
                        n.Title.Contains("báo cáo") ||
                        n.Title.Contains("vi phạm") ||
                        n.Title.Contains("khách"));
                    break;

                case "system":
                    query = query.Where(n =>
                        !n.Title.Contains("Lịch hẹn") &&
                        !n.Title.Contains("Tư vấn") &&
                        !n.Title.Contains("Liên hệ") &&
                        !n.Title.Contains("Bình luận") &&
                        !n.Title.Contains("trả lời bình luận") &&
                        !n.Title.Contains("Trả lời bình luận"));
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

            bool useBusinessLayout = await ShouldUseBusinessLayoutAsync(userId);

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
                content = CleanDisplayText(n.Content, 170),
                isRead = n.IsRead,
                createdAt = n.CreatedAt.ToString("HH:mm dd/MM/yyyy"),

                // Bấm thông báo ở chuông/hộp nổi luôn vào chi tiết trước.
                detailUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),
                processUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),

                hasAction = !string.IsNullOrWhiteSpace(NormalizeActionUrl(n, useBusinessLayout)),
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

            bool useBusinessLayout = await ShouldUseBusinessLayoutAsync(userId);

            var latestNotifications = await _context.Notifications
                .AsNoTracking()
                .Where(n => n.UserID == userId)
                .OrderByDescending(n => n.IsRead == false)
                .ThenByDescending(n => n.CreatedAt)
                .Take(10)
                .ToListAsync();

            var data = latestNotifications.Select(n => new
            {
                id = n.NotificationID,
                title = n.Title ?? "Thông báo",
                content = CleanDisplayText(n.Content, 150),
                createdAt = n.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                isRead = n.IsRead,
                hasAction = !string.IsNullOrWhiteSpace(NormalizeActionUrl(n, useBusinessLayout)),

                // Bấm chuông thông báo cũng vào chi tiết trước, không nhảy thẳng.
                processUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),
                detailsUrl = Url.Action("Details", "Notification", new { id = n.NotificationID }),

                type = GetBellType(n.Title, n.Content)
            }).ToList();

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
                data
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

            bool useBusinessLayout = await ShouldUseBusinessLayoutAsync(userId);
            SetNotificationLayout(useBusinessLayout);

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

            string safeActionUrl = NormalizeActionUrl(notification, useBusinessLayout);

            ViewBag.SafeActionUrl = safeActionUrl;
            ViewBag.HasSafeAction = !string.IsNullOrWhiteSpace(safeActionUrl);

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

            bool useBusinessLayout = await ShouldUseBusinessLayoutAsync(userId);

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

            string actionUrl = NormalizeActionUrl(noti, useBusinessLayout);

            if (string.IsNullOrWhiteSpace(actionUrl))
            {
                return RedirectToAction(nameof(Details), new { id = noti.NotificationID });
            }

            if (Url.IsLocalUrl(actionUrl))
            {
                return Redirect(actionUrl);
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

        private static string CleanDisplayText(string? value, int maxLength = 180)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
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

        private string GetNotificationIcon(string? title, string? content)
        {
            string text = $"{title} {content}".ToLowerInvariant();

            if (text.Contains("lịch hẹn")) return "bi-calendar-check-fill";
            if (text.Contains("tư vấn") || text.Contains("khách chờ") || text.Contains("liên hệ")) return "bi-headset";
            if (text.Contains("trả lời bình luận")) return "bi-reply-fill";
            if (text.Contains("bình luận")) return "bi-chat-left-text-fill";
            if (text.Contains("tin nhắn") || text.Contains("chat") || text.Contains("trò chuyện")) return "bi-chat-dots-fill";
            if (text.Contains("báo cáo") || text.Contains("cảnh báo") || text.Contains("vi phạm") || text.Contains("khóa")) return "bi-shield-exclamation";
            if (text.Contains("duyệt") || text.Contains("đăng tin")) return "bi-megaphone-fill";

            return "bi-bell-fill";
        }

        private string GetNotificationTypeName(string? title, string? content)
        {
            string text = $"{title} {content}".ToLowerInvariant();

            if (text.Contains("lịch hẹn")) return "Lịch hẹn";
            if (text.Contains("tư vấn") || text.Contains("khách chờ") || text.Contains("liên hệ")) return "Tư vấn";
            if (text.Contains("trả lời bình luận")) return "Trả lời bình luận";
            if (text.Contains("bình luận")) return "Bình luận";
            if (text.Contains("tin nhắn") || text.Contains("chat") || text.Contains("trò chuyện")) return "Tin nhắn";
            if (text.Contains("báo cáo") || text.Contains("cảnh báo") || text.Contains("vi phạm") || text.Contains("khóa")) return "Cảnh báo";

            return "Hệ thống";
        }

        private string GetBellType(string? title, string? content)
        {
            string text = $"{title} {content}".ToLowerInvariant();

            if (text.Contains("khóa") || text.Contains("vi phạm") || text.Contains("báo cáo") || text.Contains("cảnh báo") || text.Contains("từ chối"))
            {
                return "danger";
            }

            if (text.Contains("xử lý") || text.Contains("thành công") || text.Contains("duyệt") || text.Contains("ghi nhận"))
            {
                return "success";
            }

            if (text.Contains("lịch hẹn"))
            {
                return "appointment";
            }

            if (text.Contains("tư vấn") || text.Contains("liên hệ") || text.Contains("trả lời bình luận") || text.Contains("bình luận") || text.Contains("khách"))
            {
                return "customer";
            }

            if (text.Contains("cần") || text.Contains("chờ"))
            {
                return "action";
            }

            return "system";
        }

        private static string NormalizeActionUrl(Notification noti, bool useBusinessLayout = false)
        {
            string actionUrl = noti.ActionUrl?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(actionUrl))
            {
                return "";
            }

            string pathOnly = actionUrl
                .Split('?')[0]
                .Split('#')[0]
                .TrimEnd('/')
                .ToLowerInvariant();

            string text = $"{noti.Title} {noti.Content} {noti.ActionText} {noti.ActionUrl}".ToLowerInvariant();

            if (useBusinessLayout)
            {
                if (pathOnly == "/user/consultations" ||
                    pathOnly == "/user/consultations/index" ||
                    pathOnly == "/consultations" ||
                    pathOnly == "/consultations/index")
                {
                    return "/MemberProject/Index";
                }

                if (pathOnly == "/user/appointments" ||
                    pathOnly == "/user/appointments/index" ||
                    pathOnly == "/appointments" ||
                    pathOnly == "/appointments/index")
                {
                    return "/Appointments/Index?mode=DoanhNghiep&tab=lich-den";
                }

                return actionUrl;
            }

            // Sửa link cũ/sai: hệ thống hiện tại không có Area User.
            if (pathOnly == "/user/consultations" || pathOnly == "/user/consultations/index")
            {
                return "/Consultations/Index?statusFilter=All";
            }

            if (pathOnly == "/user/appointments" || pathOnly == "/user/appointments/index")
            {
                return BuildAppointmentUserUrlByText(text);
            }

            // Chuẩn hóa các link rút gọn.
            if (pathOnly == "/consultations")
            {
                return "/Consultations/Index?statusFilter=All";
            }

            if (pathOnly == "/appointments")
            {
                return BuildAppointmentUserUrlByText(text);
            }

            // Nếu đã là link đúng của người dùng thì giữ nguyên.
            if (pathOnly == "/consultations/index" || pathOnly == "/appointments/index")
            {
                return actionUrl;
            }

            return actionUrl;
        }

        private static string BuildAppointmentUserUrlByText(string text)
        {
            bool isProjectAppointment =
                text.Contains("dự án") ||
                text.Contains("du an") ||
                text.Contains("chủ đầu tư") ||
                text.Contains("chu dau tu") ||
                text.Contains("lead") ||
                text.Contains("điều phối") ||
                text.Contains("dieu phoi") ||
                text.Contains("nhân viên") ||
                text.Contains("nhan vien") ||
                text.Contains("xem dự án") ||
                text.Contains("xem du an");

            if (isProjectAppointment)
            {
                return "/Appointments/Index?mode=DoanhNghiep&tab=lich-den";
            }

            return "/Appointments/Index?mode=CaNhan&tab=lich-den";
        }
    }
}
