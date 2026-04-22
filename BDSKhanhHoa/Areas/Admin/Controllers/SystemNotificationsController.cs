using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class SystemNotificationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SystemNotificationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. TRANG GIAO DIỆN GỬI THÔNG BÁO
        // ==========================================
        public IActionResult Index()
        {
            return View();
        }

        // ==========================================
        // 2. API GỬI THÔNG BÁO CHO CLIENT
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendBroadcast([FromForm] string targetType, [FromForm] string? targetUserIds,
                                                       [FromForm] string title, [FromForm] string content,
                                                       [FromForm] string? actionUrl, [FromForm] string? actionText)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
                return Json(new { success = false, message = "Tiêu đề và nội dung không được để trống." });

            var notifications = new List<Notification>();
            DateTime now = DateTime.Now;

            // Xử lý gửi cho Tất cả người dùng
            if (targetType == "All")
            {
                var allUserIds = await _context.Users.Select(u => u.UserID).ToListAsync();
                foreach (var uid in allUserIds)
                {
                    notifications.Add(new Notification { UserID = uid, Title = title, Content = content, ActionUrl = actionUrl, ActionText = actionText, CreatedAt = now });
                }
            }
            // Xử lý gửi cho cá nhân / nhóm cụ thể
            else if (targetType == "Specific")
            {
                if (string.IsNullOrWhiteSpace(targetUserIds))
                    return Json(new { success = false, message = "Vui lòng nhập ID người dùng nhận." });

                // Phân tách chuỗi ID (Ví dụ: "1, 2, 5")
                var ids = targetUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(id => int.TryParse(id.Trim(), out int parsed) ? parsed : 0)
                                       .Where(id => id > 0)
                                       .Distinct()
                                       .ToList();

                var existingUsers = await _context.Users.Where(u => ids.Contains(u.UserID)).Select(u => u.UserID).ToListAsync();

                if (!existingUsers.Any())
                    return Json(new { success = false, message = "Không tìm thấy tài khoản hợp lệ nào trùng khớp với ID cung cấp." });

                foreach (var uid in existingUsers)
                {
                    notifications.Add(new Notification { UserID = uid, Title = title, Content = content, ActionUrl = actionUrl, ActionText = actionText, CreatedAt = now });
                }
            }

            if (notifications.Any())
            {
                _context.Notifications.AddRange(notifications);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = $"Chiến dịch thành công! Đã phát {notifications.Count} thông báo đến người dùng." });
            }

            return Json(new { success = false, message = "Hành động không hợp lệ." });
        }

        // ==========================================
        // 3. API CẤP SỐ LIỆU CHO QUẢ CHUÔNG (ADMIN BELL)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetAdminAlerts()
        {
            try
            {
                // Đếm số lượng cần xử lý thực tế trong DB
                var pendingProjects = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending");
                var pendingProperties = await _context.Properties.CountAsync(p => p.Status == "Pending");
                var newReports = await _context.PropertyReports.CountAsync(r => r.Status == "Pending");

                int totalAlerts = pendingProjects + pendingProperties + newReports;

                return Json(new
                {
                    success = true,
                    totalAlerts,
                    pendingProjects,
                    pendingProperties,
                    newReports
                });
            }
            catch
            {
                return Json(new { success = false });
            }
        }
    }
}