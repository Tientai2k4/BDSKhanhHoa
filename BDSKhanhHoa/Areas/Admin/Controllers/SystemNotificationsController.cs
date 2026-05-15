using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class SystemNotificationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;
        private readonly ILogger<SystemNotificationsController> _logger;

        private const int ROLE_ADMIN = 1;
        private const int ROLE_STAFF = 2;
        private const int ROLE_MEMBER = 3;

        public SystemNotificationsController(
            ApplicationDbContext context,
            IAuditLogService auditLogService,
            ILogger<SystemNotificationsController> logger)
        {
            _context = context;
            _auditLogService = auditLogService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            bool isAdmin = User.IsInRole("Admin");
            bool isStaff = User.IsInRole("Staff");

            var rolesQuery = _context.Roles.AsNoTracking();

            if (isStaff && !isAdmin)
            {
                rolesQuery = rolesQuery.Where(r => r.RoleID == ROLE_MEMBER);
            }

            ViewBag.Roles = await rolesQuery.OrderBy(r => r.RoleID).ToListAsync();

            ViewBag.IsAdmin = isAdmin;
            ViewBag.IsStaff = isStaff;

            ViewBag.TotalUsers = await _context.Users
                .CountAsync(u => u.IsDeleted == false && u.IsActive == true);

            ViewBag.TotalMembers = await _context.Users
                .CountAsync(u => u.IsDeleted == false && u.IsActive == true && u.RoleID == ROLE_MEMBER);

            ViewBag.TotalSent = await _context.Notifications.CountAsync();

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendBroadcast(
            [FromForm] string targetType,
            [FromForm] string? targetUserIds,
            [FromForm] int? targetRoleId,
            [FromForm] string title,
            [FromForm] string content,
            [FromForm] string? actionUrl,
            [FromForm] string? actionText)
        {
            bool isAdmin = User.IsInRole("Admin");
            bool isStaff = User.IsInRole("Staff") && !isAdmin;

            if (!isAdmin && !isStaff)
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn không có quyền phát thông báo hệ thống."
                });
            }

            title = title?.Trim() ?? "";
            content = content?.Trim() ?? "";
            targetType = targetType?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(title))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng nhập tiêu đề thông báo."
                });
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng nhập nội dung thông báo."
                });
            }

            if (title.Length > 255)
            {
                return Json(new
                {
                    success = false,
                    message = "Tiêu đề không được vượt quá 255 ký tự."
                });
            }

            if (string.IsNullOrWhiteSpace(targetType))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng chọn đối tượng nhận thông báo."
                });
            }

            if (isStaff && targetType == "All")
            {
                return Json(new
                {
                    success = false,
                    message = "Nhân viên không được phát thông báo toàn hệ thống. Vui lòng chọn nhóm Thành viên hoặc nhập ID khách hàng cụ thể."
                });
            }

            List<int> recipientIds = new List<int>();

            try
            {
                switch (targetType)
                {
                    case "All":
                        if (!isAdmin)
                        {
                            return Json(new
                            {
                                success = false,
                                message = "Chỉ Admin mới được phát thông báo toàn hệ thống."
                            });
                        }

                        recipientIds = await _context.Users
                            .Where(u =>
                                u.IsDeleted == false &&
                                u.IsActive == true)
                            .Select(u => u.UserID)
                            .ToListAsync();
                        break;

                    case "Role":
                        if (!targetRoleId.HasValue || targetRoleId.Value <= 0)
                        {
                            return Json(new
                            {
                                success = false,
                                message = "Vui lòng chọn nhóm quyền nhận thông báo."
                            });
                        }

                        if (isStaff && targetRoleId.Value != ROLE_MEMBER)
                        {
                            return Json(new
                            {
                                success = false,
                                message = "Nhân viên chỉ được gửi thông báo cho nhóm Thành viên/khách hàng."
                            });
                        }

                        recipientIds = await _context.Users
                            .Where(u =>
                                u.RoleID == targetRoleId.Value &&
                                u.IsDeleted == false &&
                                u.IsActive == true)
                            .Select(u => u.UserID)
                            .ToListAsync();
                        break;

                    case "Specific":
                        if (string.IsNullOrWhiteSpace(targetUserIds))
                        {
                            return Json(new
                            {
                                success = false,
                                message = "Vui lòng nhập ít nhất một ID người dùng nhận thông báo."
                            });
                        }

                        var rawIds = targetUserIds
                            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(idStr => int.TryParse(idStr.Trim(), out int parsed) ? parsed : 0)
                            .Where(id => id > 0)
                            .Distinct()
                            .ToList();

                        if (!rawIds.Any())
                        {
                            return Json(new
                            {
                                success = false,
                                message = "Danh sách ID người dùng không hợp lệ."
                            });
                        }

                        var usersQuery = _context.Users
                            .Where(u =>
                                rawIds.Contains(u.UserID) &&
                                u.IsDeleted == false &&
                                u.IsActive == true);

                        if (isStaff)
                        {
                            usersQuery = usersQuery.Where(u => u.RoleID == ROLE_MEMBER);
                        }

                        recipientIds = await usersQuery
                            .Select(u => u.UserID)
                            .ToListAsync();

                        if (!recipientIds.Any())
                        {
                            string msg = isStaff
                                ? "Không tìm thấy tài khoản Thành viên hợp lệ. Staff không được gửi thông báo cho Admin hoặc Staff khác."
                                : "Không tìm thấy tài khoản hợp lệ nào khớp với các ID đã nhập.";

                            return Json(new
                            {
                                success = false,
                                message = msg
                            });
                        }
                        break;

                    default:
                        return Json(new
                        {
                            success = false,
                            message = "Phương thức gửi thông báo không hợp lệ."
                        });
                }

                if (!recipientIds.Any())
                {
                    return Json(new
                    {
                        success = false,
                        message = "Không có người dùng nào thỏa mãn điều kiện nhận thông báo."
                    });
                }

                actionUrl = NormalizeActionUrl(actionUrl);
                actionText = string.IsNullOrWhiteSpace(actionText) ? null : actionText.Trim();

                if (string.IsNullOrWhiteSpace(actionUrl))
                {
                    actionText = null;
                }

                DateTime now = DateTime.Now;

                var notifications = recipientIds.Select(uid => new Notification
                {
                    UserID = uid,
                    Title = title,
                    Content = content,
                    ActionUrl = actionUrl,
                    ActionText = actionText,
                    IsRead = false,
                    CreatedAt = now
                }).ToList();

                _context.Notifications.AddRange(notifications);
                await _context.SaveChangesAsync();

                int actorId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int uid)
                    ? uid
                    : 0;

                string actorRole = isAdmin ? "Admin" : "Staff";
                string targetSummary = BuildTargetSummary(targetType, targetRoleId, targetUserIds);

                await _auditLogService.LogAsync(
                    actorId,
                    $"{actorRole} phát thông báo hệ thống: {title}",
                    "SystemNotifications",
                    $"Đối tượng: {targetSummary}; Số người nhận: {notifications.Count}",
                    severity: "Info"
                );

                return Json(new
                {
                    success = true,
                    message = $"Đã phát thành công {notifications.Count} thông báo."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi phát thông báo hệ thống");

                return Json(new
                {
                    success = false,
                    message = "Lỗi máy chủ khi phát thông báo. Vui lòng thử lại sau."
                });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAdminAlerts()
        {
            try
            {
                bool isAdmin = User.IsInRole("Admin");
                bool isStaff = User.IsInRole("Staff") && !isAdmin;

                int pendingProjects = 0;

                if (isAdmin)
                {
                    pendingProjects = await _context.Projects
                        .CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);
                }

                int pendingProperties = await _context.Properties
                    .CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);

                int newReports = await _context.PropertyReports
                    .CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);

                int pendingConsultations = await _context.Consultations
                    .CountAsync(c => c.Status == "New");

                int pendingContacts = await _context.ContactMessages
                    .CountAsync(c => c.Status == "Pending" || c.Status == "Chưa xử lý");

                int totalAlerts = pendingProjects
                                + pendingProperties
                                + newReports
                                + pendingConsultations
                                + pendingContacts;

                return Json(new
                {
                    success = true,
                    totalAlerts,
                    pendingProjects,
                    pendingProperties,
                    newReports,
                    pendingConsultations,
                    pendingContacts
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi lấy cảnh báo hệ thống");

                return Json(new
                {
                    success = false,
                    message = "Không thể tải cảnh báo hệ thống."
                });
            }
        }

        private string? NormalizeActionUrl(string? actionUrl)
        {
            if (string.IsNullOrWhiteSpace(actionUrl))
                return null;

            actionUrl = actionUrl.Trim();

            if (actionUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                actionUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                actionUrl.StartsWith("//"))
            {
                return null;
            }

            if (!actionUrl.StartsWith("/"))
            {
                actionUrl = "/" + actionUrl;
            }

            if (actionUrl.Length > 500)
            {
                actionUrl = actionUrl.Substring(0, 500);
            }

            return actionUrl;
        }

        private string BuildTargetSummary(string targetType, int? targetRoleId, string? targetUserIds)
        {
            return targetType switch
            {
                "All" => "Toàn bộ hệ thống",
                "Role" => $"Theo RoleID = {targetRoleId}",
                "Specific" => $"Danh sách ID: {targetUserIds}",
                _ => "Không xác định"
            };
        }
    }
}