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
            {
                return Json(new { success = false, message = "Lỗi phiên đăng nhập." });
            }

            var report = await _context.PropertyReports
                .Include(r => r.Property)
                    .ThenInclude(p => p.User)
                .FirstOrDefaultAsync(r => r.ReportID == reportId);

            if (report == null || report.Status != "Pending")
            {
                return Json(new { success = false, message = "Báo cáo không tồn tại hoặc đã được xử lý trước đó." });
            }

            if (report.Property == null)
            {
                return Json(new { success = false, message = "Tin đăng gắn với báo cáo không còn tồn tại." });
            }

            if (report.Property.User == null)
            {
                return Json(new { success = false, message = "Không tìm thấy tài khoản người bán của tin đăng này." });
            }

            if (report.Property.Status == "Sold" || report.Property.Status == "Rented" || report.Property.Status == "Expired")
            {
                return Json(new
                {
                    success = false,
                    message = "Tin đăng đã bán, đã cho thuê hoặc đã hết hạn nên không thể xử lý theo báo cáo này."
                });
            }

            if (string.IsNullOrWhiteSpace(adminNote))
            {
                adminNote = "Tin đăng cần được kiểm tra và cập nhật lại theo yêu cầu của Ban quản trị.";
            }

            adminNote = adminNote.Trim();

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                report.UpdatedAt = DateTime.Now;

                string oldPropertyStatus = report.Property.Status ?? "";
                int sellerId = report.Property.UserID;
                var seller = report.Property.User;

                // =====================================================
                // 1. BÁC BỎ BÁO CÁO
                // Giữ nguyên tin đăng, không cộng lỗi người bán.
                // =====================================================
                if (actionType == "Reject")
                {
                    report.Status = "Rejected";

                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Phản hồi báo cáo vi phạm",
                        Content =
                            $"Báo cáo của bạn về tin đăng #{report.PropertyID} đã được Ban quản trị xem xét. " +
                            $"Qua kiểm tra, chúng tôi chưa đủ cơ sở xác định tin đăng vi phạm.\n\n" +
                            $"Phản hồi từ Ban quản trị:\n{adminNote}",
                        ActionUrl = $"/Property/Details/{report.PropertyID}",
                        ActionText = "Xem lại tin đăng",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditLogService.LogAsync(
                        adminId,
                        "Bác bỏ báo cáo vi phạm",
                        "PropertyReports",
                        $"ReportID: {reportId} - PropertyID: {report.PropertyID}",
                        oldValues: $"OldReportStatus: Pending, PropertyStatus: {oldPropertyStatus}",
                        newValues: $"NewReportStatus: Rejected, PropertyStatus: {report.Property.Status}, AdminNote: {adminNote}",
                        severity: "Info"
                    );

                    return Json(new
                    {
                        success = true,
                        message = "Đã bác bỏ báo cáo. Tin đăng được giữ nguyên trạng thái hiện tại."
                    });
                }

                // =====================================================
                // 2. TẠM DỪNG HIỂN THỊ & YÊU CẦU NGƯỜI BÁN CHỈNH SỬA
                // Không cộng lỗi hồ sơ. Người bán sửa xong gửi lại Admin duyệt.
                // =====================================================
                if (actionType == "Warn")
                {
                    report.Status = "Processed";

                    await AutoExpireViolationsAsync(sellerId);

                    string reasonForSeller =
                        $"Tin đăng bị tạm dừng hiển thị do có báo cáo vi phạm hoặc dấu hiệu thông tin chưa phù hợp.\n\n" +
                        $"Lý do báo cáo: {report.Reason}\n\n" +
                        $"Yêu cầu từ Ban quản trị:\n{adminNote}\n\n" +
                        $"Vui lòng cập nhật lại nội dung tin đăng. Sau khi bạn lưu chỉnh sửa, tin sẽ được gửi lại về trạng thái Chờ duyệt để Ban quản trị kiểm tra.";

                    report.Property.Status = "Rejected";
                    report.Property.UpdatedAt = DateTime.Now;
                    report.Property.RejectionReason = reasonForSeller;
                    report.Property.IsAutoApproved = false;
                    report.Property.ApprovedAt = null;
                    report.Property.IsDuplicate = false;
                    report.Property.DuplicateReason = null;

                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Đã tiếp nhận và xử lý báo cáo",
                        Content =
                            $"Báo cáo của bạn về tin đăng #{report.PropertyID} đã được Ban quản trị ghi nhận. " +
                            $"Tin đăng đã được tạm dừng hiển thị và người đăng đã được yêu cầu cập nhật lại thông tin. " +
                            $"Cảm ơn bạn đã hỗ trợ cộng đồng minh bạch hơn.",
                        ActionUrl = "/",
                        ActionText = "Tiếp tục tìm kiếm",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    _context.Notifications.Add(new Notification
                    {
                        UserID = sellerId,
                        Title = "Tin đăng bị tạm dừng hiển thị và cần cập nhật",
                        Content =
                            $"Tin đăng \"{report.Property.Title}\" của bạn đã bị tạm dừng hiển thị do có báo cáo từ cộng đồng.\n\n" +
                            $"Lý do báo cáo: {report.Reason}\n\n" +
                            $"Yêu cầu từ Ban quản trị:\n{adminNote}\n\n" +
                            $"Bạn vui lòng kiểm tra lại hình ảnh, giá bán/giá thuê, địa chỉ, mô tả và các thông tin liên quan. " +
                            $"Sau khi bạn cập nhật và lưu lại, tin sẽ được gửi lại cho Admin duyệt trước khi hiển thị công khai.\n\n" +
                            $"Lưu ý: Hành động này chưa cộng lỗi vào tài khoản. Tuy nhiên nếu cố tình tái phạm hoặc bị xác định là lừa đảo nghiêm trọng, tài khoản có thể bị khóa theo chính sách hệ thống.",
                        ActionUrl = $"/Property/Edit/{report.PropertyID}",
                        ActionText = "Cập nhật tin đăng",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditLogService.LogAsync(
                        adminId,
                        "Tạm dừng tin do báo cáo và yêu cầu người bán chỉnh sửa",
                        "PropertyReports",
                        $"ReportID: {reportId} - PropertyID: {report.PropertyID}",
                        oldValues: $"OldReportStatus: Pending, OldPropertyStatus: {oldPropertyStatus}",
                        newValues: $"NewReportStatus: Processed, NewPropertyStatus: Rejected, RejectionReason: {reasonForSeller}",
                        severity: "Warning"
                    );

                    return Json(new
                    {
                        success = true,
                        message = "Đã tạm dừng hiển thị tin và gửi yêu cầu cập nhật cho người bán. Hành động này không cộng lỗi tài khoản."
                    });
                }

                // =====================================================
                // 3. GỠ TIN LẬP TỨC & GHI NHẬN 1 VI PHẠM
                // - Ẩn/xóa mềm tin.
                // - Cộng 1 lỗi Active vào UserViolations.
                // - Nếu tổng lỗi Active >= 3: khóa tài khoản người bán.
                // - Ghi chú AdminNote để Admin nhìn thấy lý do khóa ở quản lý người dùng.
                // =====================================================
                if (actionType == "DeleteProperty")
                {
                    report.Status = "Processed";

                    await AutoExpireViolationsAsync(sellerId);

                    int oldActiveViolationCount = await _context.UserViolations
                        .CountAsync(v => v.UserID == sellerId && v.Status == "Active");

                    string severeReason =
                        $"Tin đăng bị gỡ bỏ do vi phạm nghiêm trọng.\n\n" +
                        $"Lý do báo cáo: {report.Reason}\n\n" +
                        $"Kết luận từ Ban quản trị:\n{adminNote}";

                    var violation = new UserViolation
                    {
                        UserID = sellerId,
                        Reason = $"Vi phạm nghiêm trọng từ báo cáo tin đăng #{report.PropertyID}: {report.Reason}",
                        Description =
                            $"Hệ thống ghi nhận 1 lỗi vi phạm do Admin xác nhận báo cáo là đúng.\n\n" +
                            $"Mã báo cáo: #{report.ReportID}\n" +
                            $"Mã tin: #{report.PropertyID}\n" +
                            $"Tiêu đề tin: {report.Property.Title}\n" +
                            $"Lý do báo cáo: {report.Reason}\n" +
                            $"Ghi chú Admin: {adminNote}\n\n" +
                            $"Lỗi có hiệu lực trong {VIOLATION_EXPIRE_DAYS} ngày nếu người dùng không được ân xá.",
                        Status = "Active",
                        CreatedAt = DateTime.Now
                    };

                    _context.UserViolations.Add(violation);

                    report.Property.Status = "Rejected";
                    report.Property.IsDeleted = true;
                    report.Property.UpdatedAt = DateTime.Now;
                    report.Property.RejectionReason = severeReason;
                    report.Property.IsAutoApproved = false;
                    report.Property.ApprovedAt = null;
                    report.Property.IsDuplicate = false;
                    report.Property.DuplicateReason = null;

                    int newActiveViolationCount = oldActiveViolationCount + 1;
                    bool isAutoLocked = false;

                    if (newActiveViolationCount >= 3)
                    {
                        seller.IsActive = false;
                        isAutoLocked = true;

                        string lockNote =
                            $"[AUTO-KHÓA DO VI PHẠM - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                            $"Tài khoản bị khóa tự động do đủ {newActiveViolationCount}/3 lỗi vi phạm chính sách hệ thống. " +
                            $"Lỗi mới nhất phát sinh từ báo cáo #{report.ReportID}, tin #{report.PropertyID}. " +
                            $"Lý do: {report.Reason}. Ghi chú Admin: {adminNote}";

                        seller.AdminNote = string.IsNullOrWhiteSpace(seller.AdminNote)
                            ? lockNote
                            : seller.AdminNote + Environment.NewLine + lockNote;

                        _context.Users.Update(seller);
                    }

                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Đã xử lý báo cáo vi phạm",
                        Content =
                            $"Báo cáo của bạn về tin đăng #{report.PropertyID} đã được xác nhận. " +
                            $"Tin đăng đã bị gỡ khỏi hệ thống do vi phạm nghiêm trọng. Cảm ơn bạn đã giúp cộng đồng an toàn hơn.",
                        ActionUrl = "/",
                        ActionText = "Tiếp tục tìm kiếm",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    _context.Notifications.Add(new Notification
                    {
                        UserID = sellerId,
                        Title = isAutoLocked
                            ? "Tài khoản bị khóa do vi phạm chính sách hệ thống"
                            : "Tin đăng bị gỡ bỏ và tài khoản bị ghi nhận 1 lỗi vi phạm",
                        Content =
                            $"Tin đăng \"{report.Property.Title}\" của bạn đã bị gỡ bỏ khỏi hệ thống do vi phạm nghiêm trọng.\n\n" +
                            $"Lý do báo cáo: {report.Reason}\n\n" +
                            $"Kết luận từ Ban quản trị:\n{adminNote}\n\n" +
                            $"Hệ thống đã ghi nhận 1 lỗi vi phạm vào hồ sơ tài khoản của bạn. " +
                            $"Số lỗi hiện tại: {newActiveViolationCount}/3.\n\n" +
                            (isAutoLocked
                                ? "Do tài khoản đã đủ 3 lỗi vi phạm đang hiệu lực, hệ thống đã khóa tài khoản theo chính sách an toàn cộng đồng. Bạn có thể liên hệ Ban quản trị nếu cần khiếu nại hoặc bổ sung thông tin xác minh."
                                : $"Nếu đạt 3 lỗi vi phạm đang hiệu lực, tài khoản sẽ bị khóa tự động. Lỗi vi phạm có thể tự hết hiệu lực sau {VIOLATION_EXPIRE_DAYS} ngày nếu bạn không tái phạm."),
                        ActionUrl = isAutoLocked ? "/Account/Login" : "/Property/MyAds",
                        ActionText = isAutoLocked ? "Xem thông báo" : "Xem tin của tôi",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    await _auditLogService.LogAsync(
                        adminId,
                        isAutoLocked
                            ? "Gỡ tin, cộng lỗi và tự động khóa tài khoản người bán"
                            : "Gỡ tin và cộng 1 lỗi vi phạm cho người bán",
                        "PropertyReports",
                        $"ReportID: {reportId} - PropertyID: {report.PropertyID} - SellerID: {sellerId}",
                        oldValues: $"OldReportStatus: Pending, OldPropertyStatus: {oldPropertyStatus}, OldActiveViolations: {oldActiveViolationCount}, SellerActive: {seller.IsActive}",
                        newValues: $"NewReportStatus: Processed, NewPropertyStatus: Rejected, IsDeleted: true, NewActiveViolations: {newActiveViolationCount}, AutoLocked: {isAutoLocked}, Reason: {severeReason}",
                        severity: isAutoLocked ? "Danger" : "Warning"
                    );

                    return Json(new
                    {
                        success = true,
                        message = isAutoLocked
                            ? $"Đã gỡ tin, cộng 1 lỗi vi phạm. Người bán hiện có {newActiveViolationCount}/3 lỗi nên tài khoản đã bị khóa tự động."
                            : $"Đã gỡ tin và cộng 1 lỗi vi phạm cho người bán. Hiện tại người bán có {newActiveViolationCount}/3 lỗi đang hiệu lực."
                    });
                }

                return Json(new { success = false, message = "Hành động không hợp lệ." });
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

                // Nếu tài khoản đang bị khóa, tự động mở khóa và ghi chú lại việc ân xá
                if (!user.IsActive)
                {
                    user.IsActive = true;
                    isUnbanned = true;
                }

                string pardonNote = $"[ÂN XÁ - {DateTime.Now:dd/MM/yyyy HH:mm}] Admin đã ân xá {violations.Count} lỗi vi phạm. Lý do: {pardonReason}";
                user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                    ? pardonNote
                    : user.AdminNote + Environment.NewLine + pardonNote;

                _context.Users.Update(user);

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