using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertyReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

        // Thời gian thử thách: Sau 90 ngày không vi phạm, lỗi cũ sẽ tự động bị xóa bỏ
        private const int VIOLATION_EXPIRE_DAYS = 90;

        public PropertyReportsController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        // ==========================================
        // HÀM HỖ TRỢ: Tự động xóa lỗi cũ đã quá hạn
        // ==========================================
        private async Task AutoExpireViolationsAsync(int userId)
        {
            var expireDate = DateTime.Now.AddDays(-VIOLATION_EXPIRE_DAYS);
            var expiredViolations = await _context.UserViolations
                .Where(v => v.UserID == userId && v.Status == "Active" && v.CreatedAt < expireDate)
                .ToListAsync();

            if (expiredViolations.Any())
            {
                foreach (var v in expiredViolations)
                {
                    v.Status = "Expired";
                    v.Description += $" [Hệ thống tự động xóa lỗi do đã vượt qua {VIOLATION_EXPIRE_DAYS} ngày thử thách]";
                }
                await _context.SaveChangesAsync();
            }
        }

        public async Task<IActionResult> Index(string status = "Pending", string searchString = null)
        {
            var query = _context.PropertyReports
                .Include(r => r.Property)
                .Include(r => r.User)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status) && status != "All")
            {
                query = query.Where(r => r.Status == status);
            }

            if (!string.IsNullOrEmpty(searchString))
            {
                query = query.Where(r => r.Reason.Contains(searchString) ||
                                         (r.Property != null && r.Property.Title.Contains(searchString)));
            }

            ViewBag.CurrentStatus = status;
            ViewBag.SearchString = searchString;

            var reports = await query
                .OrderByDescending(r => r.Status == "Pending")
                .ThenByDescending(r => r.CreatedAt)
                .ToListAsync();

            return View(reports);
        }

        public async Task<IActionResult> Details(int id)
        {
            var report = await _context.PropertyReports
                .Include(r => r.Property).ThenInclude(p => p.User)
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.ReportID == id);

            if (report == null) return NotFound();

            if (report.Property != null && report.Property.User != null)
            {
                // Kích hoạt quét tự động xóa án tích trước khi đếm
                await AutoExpireViolationsAsync(report.Property.UserID);

                ViewBag.SellerViolationsCount = await _context.UserViolations
                    .CountAsync(v => v.UserID == report.Property.UserID && v.Status == "Active");
            }

            ViewBag.ExpireDays = VIOLATION_EXPIRE_DAYS; // Chuyển biến số ngày ra View
            return View(report);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessReport(int reportId, string actionType, string adminNote)
        {
            var adminIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(adminIdStr, out int adminId))
                return Json(new { success = false, message = "Lỗi phiên đăng nhập." });

            var report = await _context.PropertyReports
                .Include(r => r.Property)
                .FirstOrDefaultAsync(r => r.ReportID == reportId);

            if (report == null || report.Status != "Pending")
                return Json(new { success = false, message = "Báo cáo không tồn tại hoặc đã được xử lý trước đó." });

            if (string.IsNullOrWhiteSpace(adminNote))
                adminNote = "Được xử lý bởi Quản trị viên.";

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                report.UpdatedAt = DateTime.Now;
                string severityLevel = "Info";

                if (actionType == "Reject")
                {
                    report.Status = "Rejected";
                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Phản hồi báo cáo vi phạm",
                        Content = $"Báo cáo của bạn về tin đăng #{report.PropertyID} đã được xem xét. Qua kiểm tra, chúng tôi thấy tin đăng chưa vi phạm quy định. Cảm ơn bạn đã đóng góp ý kiến.",
                        ActionUrl = $"/Property/Details/{report.PropertyID}",
                        ActionText = "Xem lại tin đăng",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });
                }
                else if (actionType == "Warn" || actionType == "DeleteProperty")
                {
                    report.Status = "Processed";
                    int sellerId = report.Property!.UserID;
                    severityLevel = "Warning";

                    // Dọn dẹp lỗi cũ trước khi phạt lỗi mới
                    await AutoExpireViolationsAsync(sellerId);

                    _context.UserViolations.Add(new UserViolation
                    {
                        UserID = sellerId,
                        Reason = report.Reason,
                        Description = $"Phát hiện từ Báo cáo #{report.ReportID}. Quyết định của Admin: {adminNote}",
                        ReportedBy = adminId,
                        Status = "Active",
                        CreatedAt = DateTime.Now
                    });

                    int currentViolationsCount = await _context.UserViolations.CountAsync(v => v.UserID == sellerId && v.Status == "Active") + 1;
                    bool isAutoBanned = false;

                    if (currentViolationsCount >= 3)
                    {
                        var seller = await _context.Users.FindAsync(sellerId);
                        if (seller != null && seller.IsActive)
                        {
                            seller.IsActive = false;
                            _context.Users.Update(seller);
                            isAutoBanned = true;

                            _context.Notifications.Add(new Notification
                            {
                                UserID = sellerId,
                                Title = "Tài khoản đã bị KHÓA do vi phạm nhiều lần",
                                Content = $"Tài khoản của bạn đã vi phạm quy định {currentViolationsCount} lần. Hệ thống đã tự động khóa tài khoản của bạn để bảo vệ cộng đồng. Vui lòng liên hệ Ban Quản Trị để được hỗ trợ.",
                                IsRead = false,
                                CreatedAt = DateTime.Now
                            });

                            await _auditLogService.LogAsync(adminId, "Tự động khóa tài khoản do vi phạm >= 3 lần", "Users", $"UserID: {sellerId} - Lỗi: {report.Reason}", severity: "Critical");
                        }
                    }

                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Đã xử lý báo cáo vi phạm",
                        Content = $"Báo cáo của bạn về tin đăng #{report.PropertyID} là chính xác. Chúng tôi đã áp dụng biện pháp kỷ luật đối với người đăng. Cảm ơn bạn đã giúp cộng đồng minh bạch hơn!",
                        ActionUrl = "/",
                        ActionText = "Tiếp tục tìm kiếm",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    if (actionType == "DeleteProperty")
                    {
                        report.Property.Status = "Rejected";
                        report.Property.IsDeleted = true;
                        severityLevel = "Danger";

                        if (!isAutoBanned)
                        {
                            _context.Notifications.Add(new Notification
                            {
                                UserID = sellerId,
                                Title = "Tin đăng bị gỡ bỏ do vi phạm",
                                Content = $"Tin đăng '{report.Property.Title}' của bạn đã bị gỡ bỏ do vi phạm quy định: {report.Reason}.\n\nGhi chú từ hệ thống: {adminNote}.\n\nLƯU Ý: Bạn đã vi phạm {currentViolationsCount}/3 lần. Nếu đủ 3 lần tài khoản sẽ bị khóa. Lỗi sẽ tự động được xóa sau {VIOLATION_EXPIRE_DAYS} ngày nếu bạn không tái phạm.",
                                ActionUrl = "/Property/MyAds",
                                ActionText = "Xem danh sách tin của tôi",
                                IsRead = false,
                                CreatedAt = DateTime.Now
                            });
                        }
                    }
                    else
                    {
                        if (!isAutoBanned)
                        {
                            _context.Notifications.Add(new Notification
                            {
                                UserID = sellerId,
                                Title = "Cảnh cáo vi phạm tin đăng",
                                Content = $"Tin đăng '{report.Property.Title}' của bạn bị cộng đồng báo cáo vi phạm: {report.Reason}.\n\nYêu cầu bạn chỉnh sửa lại nội dung ngay lập tức.\n\nGhi chú: {adminNote}.\n\nLƯU Ý: Bạn đã vi phạm {currentViolationsCount}/3 lần. Nếu đủ 3 lần tài khoản sẽ bị khóa. Các lỗi sẽ tự động được xóa sau {VIOLATION_EXPIRE_DAYS} ngày.",
                                ActionUrl = $"/Property/Edit/{report.PropertyID}",
                                ActionText = "Sửa tin đăng ngay",
                                IsRead = false,
                                CreatedAt = DateTime.Now
                            });
                        }
                    }
                }
                else
                {
                    return Json(new { success = false, message = "Hành động không hợp lệ." });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                await _auditLogService.LogAsync(adminId, $"Xử lý báo cáo vi phạm", "PropertyReports", $"ReportID: {reportId} - Hành động: {actionType}", severity: severityLevel);

                return Json(new { success = true, message = "Xử lý báo cáo, ghi log và gửi thông báo thành công!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Json(new { success = false, message = "Lỗi hệ thống khi xử lý: " + ex.Message });
            }
        }

        // ==========================================
        // TÍNH NĂNG MỚI: ADMIN ÂN XÁ CHO KHÁCH VIP
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PardonUser(int userId, string pardonReason)
        {
            var adminIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(adminIdStr, out int adminId))
                return Json(new { success = false, message = "Lỗi xác thực Admin." });

            var violations = await _context.UserViolations
                .Where(v => v.UserID == userId && v.Status == "Active")
                .ToListAsync();

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return Json(new { success = false, message = "Người dùng không tồn tại." });

            bool isUnbanned = false;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Xóa án tích
                foreach (var v in violations)
                {
                    v.Status = "Pardoned";
                    v.Description += $" [Được ân xá thủ công bởi Admin: {pardonReason}]";
                }

                // Nếu tài khoản đang bị khóa, tự động mở khóa
                if (!user.IsActive)
                {
                    user.IsActive = true;
                    _context.Users.Update(user);
                    isUnbanned = true;
                }

                // Gửi thông báo xoa dịu
                _context.Notifications.Add(new Notification
                {
                    UserID = userId,
                    Title = "Bạn đã được Ban Quản Trị Ân Xá",
                    Content = $"Tin vui: Ban quản trị đã xem xét và quyết định xóa bỏ toàn bộ ({violations.Count}) cảnh cáo vi phạm trước đó của bạn.\n\nLời nhắn từ Admin: {pardonReason}.\n\nCảm ơn bạn đã luôn đồng hành cùng BĐS Khánh Hòa.{(isUnbanned ? " Tài khoản của bạn đã được mở khóa." : "")}",
                    CreatedAt = DateTime.Now,
                    IsRead = false
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Ghi Log
                await _auditLogService.LogAsync(adminId, "Ân xá toàn bộ vi phạm người dùng", "Users", $"UserID: {userId} - Lý do: {pardonReason}", severity: "Warning");

                return Json(new { success = true, message = $"Đã ân xá thành công {violations.Count} vi phạm {(isUnbanned ? "và Mở khóa tài khoản" : "")}!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }
    }
}