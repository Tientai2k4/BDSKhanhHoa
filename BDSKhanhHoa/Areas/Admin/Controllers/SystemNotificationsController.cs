// File: Areas/Admin/Controllers/SystemNotificationsController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

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
        // 1. GIAO DIỆN QUẢN TRỊ TRUNG TÂM PHÁT THÔNG BÁO
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            // Lấy danh sách các vai trò (Roles) để Admin có thể phát thông báo theo nhóm
            ViewBag.Roles = await _context.Roles.AsNoTracking().ToListAsync();

            // Lấy thống kê nhanh để hiển thị trên Dashboard
            ViewBag.TotalUsers = await _context.Users.CountAsync(u => u.IsDeleted == false);
            ViewBag.TotalSent = await _context.Notifications.CountAsync();

            return View();
        }

        // ==========================================
        // 2. API XỬ LÝ PHÁT THÔNG BÁO (BROADCAST ENGINE)
        // ==========================================
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
            // 1. Kiểm tra tính hợp lệ cơ bản
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            {
                return Json(new { success = false, message = "Tiêu đề và nội dung thông báo không được để trống." });
            }

            if (string.IsNullOrWhiteSpace(targetType))
            {
                return Json(new { success = false, message = "Vui lòng chọn đối tượng nhận thông báo." });
            }

            List<int> recipientIds = new List<int>();

            try
            {
                // 2. Phân luồng lấy danh sách ID người dùng nhận thông báo
                switch (targetType)
                {
                    case "All":
                        // Lấy toàn bộ User đang hoạt động
                        recipientIds = await _context.Users
                            .Where(u => u.IsDeleted == false && u.IsActive == true)
                            .Select(u => u.UserID)
                            .ToListAsync();
                        break;

                    case "Role":
                        // Lấy User theo một Role cụ thể (Ví dụ: Chỉ gửi cho Khách hàng, hoặc chỉ gửi cho Staff)
                        if (!targetRoleId.HasValue || targetRoleId.Value <= 0)
                            return Json(new { success = false, message = "Vui lòng chọn một Nhóm người dùng hợp lệ." });

                        recipientIds = await _context.Users
                            .Where(u => u.RoleID == targetRoleId.Value && u.IsDeleted == false && u.IsActive == true)
                            .Select(u => u.UserID)
                            .ToListAsync();
                        break;

                    case "Specific":
                        // Lấy User theo danh sách ID nhập tay
                        if (string.IsNullOrWhiteSpace(targetUserIds))
                            return Json(new { success = false, message = "Vui lòng nhập ít nhất một ID người dùng nhận." });

                        // Xử lý chuỗi ID nhập vào (lọc khoảng trắng, bỏ ký tự lỗi, loại bỏ trùng lặp)
                        var rawIds = targetUserIds.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(idStr => int.TryParse(idStr.Trim(), out int parsed) ? parsed : 0)
                                                  .Where(id => id > 0)
                                                  .Distinct()
                                                  .ToList();

                        if (!rawIds.Any())
                            return Json(new { success = false, message = "Định dạng ID không hợp lệ. Vui lòng nhập các số nguyên, cách nhau bởi dấu phẩy." });

                        // Đối chiếu với Database để chắc chắn ID đó tồn tại
                        recipientIds = await _context.Users
                            .Where(u => rawIds.Contains(u.UserID) && u.IsDeleted == false)
                            .Select(u => u.UserID)
                            .ToListAsync();

                        if (!recipientIds.Any())
                            return Json(new { success = false, message = "Không tìm thấy tài khoản hợp lệ nào khớp với các ID bạn vừa nhập." });
                        break;

                    default:
                        return Json(new { success = false, message = "Phương thức gửi không được hỗ trợ." });
                }

                // 3. Tiến hành tạo hàng loạt bản ghi Notification
                if (!recipientIds.Any())
                {
                    return Json(new { success = false, message = "Không có người dùng nào thỏa mãn điều kiện nhận thông báo." });
                }

                var notifications = new List<Notification>();
                DateTime currentTime = DateTime.Now;

                // Tối ưu hóa chuỗi ActionUrl (Bảo mật: Ép về relative path nếu cố tình nhập link ngoài rác)
                if (!string.IsNullOrWhiteSpace(actionUrl) && !actionUrl.StartsWith("/"))
                {
                    actionUrl = "/" + actionUrl;
                }

                foreach (var uid in recipientIds)
                {
                    notifications.Add(new Notification
                    {
                        UserID = uid,
                        Title = title.Trim(),
                        Content = content.Trim(),
                        ActionUrl = string.IsNullOrWhiteSpace(actionUrl) ? null : actionUrl.Trim(),
                        ActionText = string.IsNullOrWhiteSpace(actionText) ? null : actionText.Trim(),
                        IsRead = false,
                        CreatedAt = currentTime
                    });
                }

                // Lưu vào CSDL
                _context.Notifications.AddRange(notifications);
                await _context.SaveChangesAsync();

                // Ghi Log hệ thống cho hành động phát thông báo
                var currentAdminId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                _context.AuditLogs.Add(new AuditLog
                {
                    UserID = int.Parse(currentAdminId),
                    Action = $"Phát thông báo Broadcast: '{title}'",
                    Target = $"Users (Count: {notifications.Count})",
                    CreatedAt = currentTime
                });
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = $"Chiến dịch thành công! Đã phát {notifications.Count} thông báo đến người dùng." });
            }
            catch (Exception ex)
            {
                // Bắt lỗi hệ thống để tránh crash server
                return Json(new { success = false, message = "Lỗi máy chủ khi phát thông báo: " + ex.Message });
            }
        }

        // ==========================================
        // 3. API CẤP SỐ LIỆU CHO QUẢ CHUÔNG (ADMIN BELL DASHBOARD)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetAdminAlerts()
        {
            try
            {
                var pendingProjects = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);
                var pendingProperties = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
                var newReports = await _context.PropertyReports.CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);

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