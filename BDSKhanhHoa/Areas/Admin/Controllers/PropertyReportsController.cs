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

        private const int VIOLATION_EXPIRE_DAYS = 90;
        private const int VIOLATION_LOCK_LIMIT = 3;
        private const string AUTO_LOCK_TAG = "AUTO-KHÓA DO VI PHẠM";

        public PropertyReportsController(
            ApplicationDbContext context,
            IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        public class LockedViolationUserItem
        {
            public int UserID { get; set; }
            public string? FullName { get; set; }
            public string? Username { get; set; }
            public string? Email { get; set; }
            public string? Phone { get; set; }
            public string? Avatar { get; set; }
            public bool IsActive { get; set; }
            public int ActiveViolationCount { get; set; }
            public DateTime? LastViolationAt { get; set; }
            public string? LastViolationReason { get; set; }
            public string? AdminNote { get; set; }
        }

        // =====================================================
        // HÀM HỖ TRỢ: TỰ ĐỘNG HẾT HIỆU LỰC LỖI CŨ SAU 90 NGÀY
        // =====================================================
        private async Task AutoExpireViolationsAsync(int userId)
        {
            var expireDate = DateTime.Now.AddDays(-VIOLATION_EXPIRE_DAYS);

            var expiredViolations = await _context.UserViolations
                .Where(v => v.UserID == userId
                            && v.Status == "Active"
                            && v.CreatedAt < expireDate)
                .ToListAsync();

            if (!expiredViolations.Any())
            {
                return;
            }

            foreach (var violation in expiredViolations)
            {
                violation.Status = "Expired";

                string expireNote =
                    $" [Hệ thống tự động chuyển lỗi sang hết hiệu lực do đã vượt qua {VIOLATION_EXPIRE_DAYS} ngày thử thách - {DateTime.Now:dd/MM/yyyy HH:mm}]";

                violation.Description = string.IsNullOrWhiteSpace(violation.Description)
                    ? expireNote.Trim()
                    : violation.Description + expireNote;
            }

            await _context.SaveChangesAsync();
        }

        // =====================================================
        // HÀM HỖ TRỢ: QUÉT TOÀN BỘ LỖI CŨ ĐỂ HẾT HIỆU LỰC
        // =====================================================
        private async Task AutoExpireAllActiveViolationsAsync()
        {
            var expireDate = DateTime.Now.AddDays(-VIOLATION_EXPIRE_DAYS);

            var expiredViolations = await _context.UserViolations
                .Where(v => v.Status == "Active" && v.CreatedAt < expireDate)
                .ToListAsync();

            if (!expiredViolations.Any())
            {
                return;
            }

            foreach (var violation in expiredViolations)
            {
                violation.Status = "Expired";

                string expireNote =
                    $" [Hệ thống tự động chuyển lỗi sang hết hiệu lực do đã vượt qua {VIOLATION_EXPIRE_DAYS} ngày thử thách - {DateTime.Now:dd/MM/yyyy HH:mm}]";

                violation.Description = string.IsNullOrWhiteSpace(violation.Description)
                    ? expireNote.Trim()
                    : violation.Description + expireNote;
            }

            await _context.SaveChangesAsync();
        }

        // =====================================================
        // HÀM HỖ TRỢ: ĐẾM LỖI ACTIVE
        // =====================================================
        private async Task<int> CountActiveViolationsAsync(int userId)
        {
            return await _context.UserViolations
                .CountAsync(v => v.UserID == userId && v.Status == "Active");
        }

        // =====================================================
        // HÀM HỖ TRỢ: KHÓA TÀI KHOẢN NẾU ĐỦ 3 LỖI
        // =====================================================
        private async Task<bool> LockUserIfReachedViolationLimitAsync(
            User user,
            int activeViolationCount,
            int reportId,
            int propertyId,
            string reportReason,
            string adminNote)
        {
            if (activeViolationCount < VIOLATION_LOCK_LIMIT)
            {
                return false;
            }

            if (!user.IsActive &&
                !string.IsNullOrWhiteSpace(user.AdminNote) &&
                user.AdminNote.Contains(AUTO_LOCK_TAG))
            {
                return true;
            }

            user.IsActive = false;

            string lockNote =
                $"[{AUTO_LOCK_TAG} - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                $"Tài khoản bị khóa tự động do có {activeViolationCount}/{VIOLATION_LOCK_LIMIT} lỗi vi phạm đang hiệu lực. " +
                $"Lỗi mới nhất phát sinh từ báo cáo #{reportId}, tin #{propertyId}. " +
                $"Lý do báo cáo: {reportReason}. " +
                $"Ghi chú Admin: {adminNote}";

            user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                ? lockNote
                : user.AdminNote + Environment.NewLine + lockNote;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return true;
        }

        // =====================================================
        // HÀM HỖ TRỢ: TỰ ĐỘNG KHÓA NHỮNG USER ĐÃ ĐỦ 3 LỖI
        // Dùng để đồng bộ trường hợp dữ liệu cũ đã có 3 lỗi nhưng chưa bị khóa
        // =====================================================
        private async Task AutoLockUsersReachedViolationLimitAsync()
        {
            var reachedLimitUsers = await _context.UserViolations
                .Where(v => v.Status == "Active")
                .GroupBy(v => v.UserID)
                .Where(g => g.Count() >= VIOLATION_LOCK_LIMIT)
                .Select(g => new
                {
                    UserID = g.Key,
                    Count = g.Count()
                })
                .ToListAsync();

            if (!reachedLimitUsers.Any())
            {
                return;
            }

            var ids = reachedLimitUsers.Select(x => x.UserID).ToList();

            var usersNeedLock = await _context.Users
                .Where(u => ids.Contains(u.UserID)
                            && !u.IsDeleted
                            && u.IsActive
                            && u.RoleID != 1
                            && u.RoleID != 2)
                .ToListAsync();

            if (!usersNeedLock.Any())
            {
                return;
            }

            foreach (var user in usersNeedLock)
            {
                int count = reachedLimitUsers.First(x => x.UserID == user.UserID).Count;

                user.IsActive = false;

                string lockNote =
                    $"[{AUTO_LOCK_TAG} - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                    $"Hệ thống tự động khóa tài khoản do có {count}/{VIOLATION_LOCK_LIMIT} lỗi vi phạm đang hiệu lực.";

                user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                    ? lockNote
                    : user.AdminNote + Environment.NewLine + lockNote;
            }

            await _context.SaveChangesAsync();
        }

        // =====================================================
        // HÀM HỖ TRỢ: LẤY DANH SÁCH TÀI KHOẢN BỊ KHÓA DO VI PHẠM
        // =====================================================
        private async Task<List<LockedViolationUserItem>> GetLockedViolationUsersAsync()
        {
            var violationGroups = await _context.UserViolations
                .Where(v => v.Status == "Active")
                .GroupBy(v => v.UserID)
                .Select(g => new
                {
                    UserID = g.Key,
                    ActiveViolationCount = g.Count(),
                    LastViolationAt = g.Max(x => x.CreatedAt)
                })
                .ToListAsync();

            var lockedUserIds = violationGroups
                .Where(x => x.ActiveViolationCount >= VIOLATION_LOCK_LIMIT)
                .Select(x => x.UserID)
                .ToList();

            var users = await _context.Users
                .Where(u => !u.IsDeleted
                            && u.RoleID != 1
                            && u.RoleID != 2
                            && (
                                lockedUserIds.Contains(u.UserID)
                                || (!u.IsActive && u.AdminNote != null && u.AdminNote.Contains(AUTO_LOCK_TAG))
                            ))
                .OrderBy(u => u.IsActive)
                .ThenByDescending(u => u.UserID)
                .ToListAsync();

            if (!users.Any())
            {
                return new List<LockedViolationUserItem>();
            }

            var userIds = users.Select(u => u.UserID).ToList();

            var latestViolations = await _context.UserViolations
                .Where(v => userIds.Contains(v.UserID) && v.Status == "Active")
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync();

            var result = new List<LockedViolationUserItem>();

            foreach (var user in users)
            {
                var group = violationGroups.FirstOrDefault(x => x.UserID == user.UserID);
                var lastViolation = latestViolations.FirstOrDefault(v => v.UserID == user.UserID);

                result.Add(new LockedViolationUserItem
                {
                    UserID = user.UserID,
                    FullName = user.FullName,
                    Username = user.Username,
                    Email = user.Email,
                    Phone = user.Phone,
                    Avatar = user.Avatar,
                    IsActive = user.IsActive,
                    ActiveViolationCount = group?.ActiveViolationCount ?? 0,
                    LastViolationAt = group?.LastViolationAt,
                    LastViolationReason = lastViolation?.Reason,
                    AdminNote = user.AdminNote
                });
            }

            return result
                .OrderByDescending(x => !x.IsActive)
                .ThenByDescending(x => x.ActiveViolationCount)
                .ThenByDescending(x => x.LastViolationAt)
                .ToList();
        }

        // =====================================================
        // DANH SÁCH BÁO CÁO
        // Có hiển thị danh sách tài khoản bị khóa do đủ lỗi
        // =====================================================
        public async Task<IActionResult> Index(string status = "Pending", string? searchString = null)
        {
            await AutoExpireAllActiveViolationsAsync();
            await AutoLockUsersReachedViolationLimitAsync();

            var query = _context.PropertyReports
                .Include(r => r.Property)
                .Include(r => r.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && status != "All")
            {
                query = query.Where(r => r.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string keyword = searchString.Trim();

                query = query.Where(r =>
                    r.Reason.Contains(keyword) ||
                    (r.Description != null && r.Description.Contains(keyword)) ||
                    (r.Property != null && r.Property.Title.Contains(keyword)));
            }

            ViewBag.CurrentStatus = status;
            ViewBag.SearchString = searchString;
            ViewBag.ViolationLockLimit = VIOLATION_LOCK_LIMIT;
            ViewBag.ExpireDays = VIOLATION_EXPIRE_DAYS;
            ViewBag.LockedViolationUsers = await GetLockedViolationUsersAsync();

            var reports = await query
                .OrderByDescending(r => r.Status == "Pending")
                .ThenByDescending(r => r.CreatedAt)
                .ToListAsync();

            return View(reports);
        }

        // =====================================================
        // CHI TIẾT BÁO CÁO
        // =====================================================
        public async Task<IActionResult> Details(int id)
        {
            var report = await _context.PropertyReports
                .Include(r => r.Property)
                    .ThenInclude(p => p.User)
                .Include(r => r.User)
                .FirstOrDefaultAsync(r => r.ReportID == id);

            if (report == null)
            {
                return NotFound();
            }

            if (report.Property != null && report.Property.User != null)
            {
                await AutoExpireViolationsAsync(report.Property.UserID);

                int activeViolationCount = await CountActiveViolationsAsync(report.Property.UserID);

                ViewBag.SellerViolationsCount = activeViolationCount;
            }
            else
            {
                ViewBag.SellerViolationsCount = 0;
            }

            ViewBag.ExpireDays = VIOLATION_EXPIRE_DAYS;
            ViewBag.ViolationLockLimit = VIOLATION_LOCK_LIMIT;

            return View(report);
        }

        // =====================================================
        // XỬ LÝ BÁO CÁO
        // actionType:
        // - Reject: Bác bỏ báo cáo, không cộng lỗi
        // - Warn: Tạm dừng tin, yêu cầu sửa, không cộng lỗi
        // - DeleteProperty: Gỡ tin, cộng 1 lỗi, đủ 3 lỗi thì khóa tài khoản
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessReport(int reportId, string actionType, string? adminNote)
        {
            var adminIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(adminIdStr, out int adminId))
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi phiên đăng nhập. Vui lòng đăng nhập lại."
                });
            }

            if (string.IsNullOrWhiteSpace(actionType))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng chọn phương án xử lý báo cáo."
                });
            }

            actionType = actionType.Trim();
            adminNote = string.IsNullOrWhiteSpace(adminNote)
                ? "Tin đăng cần được kiểm tra và cập nhật lại theo yêu cầu của Ban quản trị."
                : adminNote.Trim();

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                return await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    var report = await _context.PropertyReports
                        .Include(r => r.Property)
                            .ThenInclude(p => p.User)
                        .FirstOrDefaultAsync(r => r.ReportID == reportId);

                    if (report == null)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Báo cáo không tồn tại."
                        });
                    }

                    if (report.Status != "Pending")
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Báo cáo này đã được xử lý trước đó."
                        });
                    }

                    if (report.Property == null)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Tin đăng gắn với báo cáo không còn tồn tại."
                        });
                    }

                    if (report.Property.User == null)
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Không tìm thấy tài khoản người bán của tin đăng này."
                        });
                    }

                    if (report.Property.Status == "Sold" ||
                        report.Property.Status == "Rented" ||
                        report.Property.Status == "Expired")
                    {
                        return Json(new
                        {
                            success = false,
                            message = "Tin đăng đã bán, đã cho thuê hoặc đã hết hạn nên không thể xử lý theo báo cáo này."
                        });
                    }

                    var property = report.Property;
                    var seller = property.User;

                    int sellerId = property.UserID;
                    string oldReportStatus = report.Status;
                    string oldPropertyStatus = property.Status ?? "";
                    bool oldSellerActive = seller.IsActive;

                    report.UpdatedAt = DateTime.Now;

                    // =====================================================
                    // 1. BÁC BỎ BÁO CÁO
                    // =====================================================
                    if (actionType == "Reject")
                    {
                        report.Status = "Rejected";

                        _context.Notifications.Add(new Notification
                        {
                            UserID = report.ReportedBy,
                            Title = "Phản hồi báo cáo vi phạm",
                            Content =
                                $"Báo cáo của bạn về tin đăng #{property.PropertyID} đã được Ban quản trị xem xét.\n\n" +
                                $"Qua kiểm tra, chúng tôi chưa đủ cơ sở xác định tin đăng vi phạm nên tin đăng được giữ nguyên.\n\n" +
                                $"Phản hồi từ Ban quản trị:\n{adminNote}",
                            ActionUrl = $"/Property/Details/{property.PropertyID}",
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
                            $"ReportID: {reportId} - PropertyID: {property.PropertyID}",
                            oldValues: $"OldReportStatus: {oldReportStatus}, OldPropertyStatus: {oldPropertyStatus}",
                            newValues: $"NewReportStatus: Rejected, PropertyStatus: {property.Status}, AdminNote: {adminNote}",
                            severity: "Info"
                        );

                        return Json(new
                        {
                            success = true,
                            message = "Đã bác bỏ báo cáo. Tin đăng được giữ nguyên, người bán không bị cộng lỗi."
                        });
                    }

                    // =====================================================
                    // 2. TẠM DỪNG HIỂN THỊ & YÊU CẦU NGƯỜI BÁN CHỈNH SỬA
                    // Không cộng lỗi người bán
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

                        property.Status = "Rejected";
                        property.UpdatedAt = DateTime.Now;
                        property.RejectionReason = reasonForSeller;
                        property.IsAutoApproved = false;
                        property.ApprovedAt = null;
                        property.IsDuplicate = false;
                        property.DuplicateReason = null;

                        _context.Notifications.Add(new Notification
                        {
                            UserID = report.ReportedBy,
                            Title = "Đã tiếp nhận và xử lý báo cáo",
                            Content =
                                $"Báo cáo của bạn về tin đăng #{property.PropertyID} đã được Ban quản trị ghi nhận.\n\n" +
                                $"Tin đăng đã được tạm dừng hiển thị và người đăng đã được yêu cầu cập nhật lại thông tin.\n\n" +
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
                                $"Tin đăng \"{property.Title}\" của bạn đã bị tạm dừng hiển thị do có báo cáo từ cộng đồng.\n\n" +
                                $"Lý do báo cáo: {report.Reason}\n\n" +
                                $"Yêu cầu từ Ban quản trị:\n{adminNote}\n\n" +
                                $"Bạn vui lòng kiểm tra lại hình ảnh, giá bán/giá thuê, địa chỉ, mô tả và các thông tin liên quan. " +
                                $"Sau khi cập nhật và lưu lại, tin sẽ được gửi lại cho Admin duyệt trước khi hiển thị công khai.\n\n" +
                                $"Lưu ý: Hành động này chưa cộng lỗi vào tài khoản. Tuy nhiên nếu cố tình tái phạm hoặc bị xác định là vi phạm nghiêm trọng, tài khoản có thể bị khóa theo chính sách hệ thống.",
                            ActionUrl = $"/Property/Edit/{property.PropertyID}",
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
                            $"ReportID: {reportId} - PropertyID: {property.PropertyID} - SellerID: {sellerId}",
                            oldValues: $"OldReportStatus: {oldReportStatus}, OldPropertyStatus: {oldPropertyStatus}",
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
                    // 3. GỠ TIN LẬP TỨC & GHI NHẬN 1 LỖI VI PHẠM
                    // Nếu tổng lỗi Active >= 3 thì khóa tài khoản
                    // =====================================================
                    if (actionType == "DeleteProperty")
                    {
                        report.Status = "Processed";

                        await AutoExpireViolationsAsync(sellerId);

                        int oldActiveViolationCount = await CountActiveViolationsAsync(sellerId);

                        string severeReason =
                            $"Tin đăng bị gỡ bỏ do vi phạm nghiêm trọng.\n\n" +
                            $"Lý do báo cáo: {report.Reason}\n\n" +
                            $"Kết luận từ Ban quản trị:\n{adminNote}";

                        var violation = new UserViolation
                        {
                            UserID = sellerId,
                            Reason = $"Vi phạm nghiêm trọng từ báo cáo tin đăng #{property.PropertyID}: {report.Reason}",
                            Description =
                                $"Hệ thống ghi nhận 1 lỗi vi phạm do Admin xác nhận báo cáo là đúng.\n\n" +
                                $"Mã báo cáo: #{report.ReportID}\n" +
                                $"Mã tin: #{property.PropertyID}\n" +
                                $"Tiêu đề tin: {property.Title}\n" +
                                $"Lý do báo cáo: {report.Reason}\n" +
                                $"Ghi chú Admin: {adminNote}\n\n" +
                                $"Lỗi có hiệu lực trong {VIOLATION_EXPIRE_DAYS} ngày nếu người dùng không được ân xá.",
                            Status = "Active",
                            CreatedAt = DateTime.Now
                        };

                        _context.UserViolations.Add(violation);

                        property.Status = "Rejected";
                        property.IsDeleted = true;
                        property.UpdatedAt = DateTime.Now;
                        property.RejectionReason = severeReason;
                        property.IsAutoApproved = false;
                        property.ApprovedAt = null;
                        property.IsDuplicate = false;
                        property.DuplicateReason = null;

                        await _context.SaveChangesAsync();

                        int newActiveViolationCount = await CountActiveViolationsAsync(sellerId);

                        bool isAutoLocked = await LockUserIfReachedViolationLimitAsync(
                            seller,
                            newActiveViolationCount,
                            report.ReportID,
                            property.PropertyID,
                            report.Reason,
                            adminNote
                        );

                        _context.Notifications.Add(new Notification
                        {
                            UserID = report.ReportedBy,
                            Title = "Đã xử lý báo cáo vi phạm",
                            Content =
                                $"Báo cáo của bạn về tin đăng #{property.PropertyID} đã được xác nhận.\n\n" +
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
                                ? "Tài khoản của bạn đã bị khóa do vi phạm nhiều lần"
                                : "Tin đăng bị gỡ và tài khoản bị ghi nhận 1 lỗi vi phạm",
                            Content =
                                $"Tin đăng \"{property.Title}\" của bạn đã bị gỡ khỏi hệ thống vì vi phạm chính sách đăng tin.\n\n" +
                                $"Lý do báo cáo: {report.Reason}\n\n" +
                                $"Kết luận từ Ban quản trị:\n{adminNote}\n\n" +
                                $"Tài khoản hiện có {newActiveViolationCount}/{VIOLATION_LOCK_LIMIT} lỗi vi phạm đang hiệu lực.\n\n" +
                                (isAutoLocked
                                    ? $"Do tài khoản đã đủ {VIOLATION_LOCK_LIMIT}/{VIOLATION_LOCK_LIMIT} lỗi vi phạm đang hiệu lực, hệ thống đã tự động khóa tài khoản của bạn. Vui lòng liên hệ Ban quản trị nếu cần hỗ trợ."
                                    : $"Nếu tài khoản đạt {VIOLATION_LOCK_LIMIT}/{VIOLATION_LOCK_LIMIT} lỗi vi phạm đang hiệu lực, tài khoản sẽ bị khóa tự động. Lỗi vi phạm có thể tự hết hiệu lực sau {VIOLATION_EXPIRE_DAYS} ngày nếu bạn không tái phạm."),
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
                            $"ReportID: {reportId} - PropertyID: {property.PropertyID} - SellerID: {sellerId}",
                            oldValues:
                                $"OldReportStatus: {oldReportStatus}, " +
                                $"OldPropertyStatus: {oldPropertyStatus}, " +
                                $"OldActiveViolations: {oldActiveViolationCount}, " +
                                $"OldSellerActive: {oldSellerActive}",
                            newValues:
                                $"NewReportStatus: Processed, " +
                                $"NewPropertyStatus: Rejected, " +
                                $"IsDeleted: true, " +
                                $"NewActiveViolations: {newActiveViolationCount}, " +
                                $"AutoLocked: {isAutoLocked}, " +
                                $"Reason: {severeReason}",
                            severity: isAutoLocked ? "Danger" : "Warning"
                        );

                        return Json(new
                        {
                            success = true,
                            isAutoLocked,
                            activeViolationCount = newActiveViolationCount,
                            message = isAutoLocked
                                ? $"Đã gỡ tin, cộng 1 lỗi vi phạm. Người bán hiện có {newActiveViolationCount}/{VIOLATION_LOCK_LIMIT} lỗi nên tài khoản đã bị khóa tự động."
                                : $"Đã gỡ tin và cộng 1 lỗi vi phạm cho người bán. Hiện tại người bán có {newActiveViolationCount}/{VIOLATION_LOCK_LIMIT} lỗi đang hiệu lực."
                        });
                    }

                    return Json(new
                    {
                        success = false,
                        message = "Hành động không hợp lệ."
                    });
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi hệ thống khi xử lý báo cáo: " + ex.Message
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PardonUser(int userId, string? pardonReason)
        {
            var adminIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!int.TryParse(adminIdStr, out int adminId))
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi xác thực Admin. Vui lòng đăng nhập lại."
                });
            }

            pardonReason = string.IsNullOrWhiteSpace(pardonReason)
                ? "Admin xem xét và quyết định ân xá lỗi vi phạm cho người dùng."
                : pardonReason.Trim();

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.UserID == userId && !u.IsDeleted);

            if (user == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Người dùng không tồn tại hoặc đã bị xóa."
                });
            }

            if (user.RoleID == 1 || user.RoleID == 2)
            {
                return Json(new
                {
                    success = false,
                    message = "Không được ân xá hoặc mở khóa tài khoản Admin/Staff bằng chức năng này."
                });
            }

            var activeViolations = await _context.UserViolations
                .Where(v => v.UserID == userId && v.Status == "Active")
                .OrderByDescending(v => v.CreatedAt)
                .ToListAsync();

            int oldActiveViolationCount = activeViolations.Count;
            bool wasLocked = !user.IsActive;

            if (!activeViolations.Any())
            {
                if (!user.IsActive)
                {
                    user.IsActive = true;

                    string unlockNote =
                        $"[MỞ KHÓA SAU KIỂM TRA - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                        $"Admin mở khóa tài khoản vì không còn lỗi vi phạm đang hiệu lực. Lý do: {pardonReason}";

                    user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                        ? unlockNote
                        : user.AdminNote + Environment.NewLine + unlockNote;

                    _context.Notifications.Add(new Notification
                    {
                        UserID = userId,
                        Title = "Tài khoản của bạn đã được mở khóa",
                        Content =
                            $"Ban quản trị đã kiểm tra và mở khóa tài khoản của bạn.\n\n" +
                            $"Lý do:\n{pardonReason}\n\n" +
                            $"Vui lòng tuân thủ chính sách hệ thống khi đăng tin.",
                        ActionUrl = "/Account/Profile",
                        ActionText = "Xem hồ sơ",
                        CreatedAt = DateTime.Now,
                        IsRead = false
                    });

                    await _context.SaveChangesAsync();

                    await _auditLogService.LogAsync(
                        adminId,
                        "Mở khóa tài khoản không còn lỗi Active",
                        "Users",
                        $"UserID: {userId} - Reason: {pardonReason}",
                        oldValues: $"IsActive: false, ActiveViolations: 0",
                        newValues: $"IsActive: true, ActiveViolations: 0",
                        severity: "Warning"
                    );

                    return Json(new
                    {
                        success = true,
                        message = "Tài khoản không còn lỗi Active. Đã mở khóa tài khoản."
                    });
                }

                return Json(new
                {
                    success = true,
                    message = "Người dùng hiện không có lỗi vi phạm đang hiệu lực."
                });
            }

            foreach (var violation in activeViolations)
            {
                violation.Status = "Pardoned";

                string pardonNoteForViolation =
                    $" [Được ân xá thủ công bởi Admin lúc {DateTime.Now:dd/MM/yyyy HH:mm}. Lý do: {pardonReason}]";

                violation.Description = string.IsNullOrWhiteSpace(violation.Description)
                    ? pardonNoteForViolation.Trim()
                    : violation.Description + pardonNoteForViolation;
            }

            user.IsActive = true;

            string userPardonNote =
                $"[ÂN XÁ - {DateTime.Now:dd/MM/yyyy HH:mm}] " +
                $"Admin đã ân xá {oldActiveViolationCount} lỗi vi phạm đang hiệu lực. " +
                $"Lý do: {pardonReason}. " +
                (wasLocked ? "Tài khoản đã được mở khóa." : "Tài khoản vẫn đang hoạt động.");

            user.AdminNote = string.IsNullOrWhiteSpace(user.AdminNote)
                ? userPardonNote
                : user.AdminNote + Environment.NewLine + userPardonNote;

            _context.Notifications.Add(new Notification
            {
                UserID = userId,
                Title = "Bạn đã được Ban quản trị ân xá lỗi vi phạm",
                Content =
                    $"Ban quản trị đã xem xét và quyết định ân xá {oldActiveViolationCount} lỗi vi phạm đang hiệu lực trong hồ sơ của bạn.\n\n" +
                    $"Lời nhắn từ Ban quản trị:\n{pardonReason}\n\n" +
                    (wasLocked
                        ? "Tài khoản của bạn đã được mở khóa. Vui lòng tuân thủ chính sách đăng tin để tránh bị khóa lại."
                        : "Vui lòng tiếp tục tuân thủ chính sách đăng tin của hệ thống."),
                ActionUrl = "/Account/Profile",
                ActionText = "Xem hồ sơ",
                CreatedAt = DateTime.Now,
                IsRead = false
            });

            await _context.SaveChangesAsync();

            await _auditLogService.LogAsync(
                adminId,
                wasLocked
                    ? "Ân xá vi phạm và mở khóa tài khoản"
                    : "Ân xá vi phạm người dùng",
                "Users",
                $"UserID: {userId} - PardonedViolations: {oldActiveViolationCount} - Reason: {pardonReason}",
                oldValues: $"IsActive: {!wasLocked}, ActiveViolations: {oldActiveViolationCount}",
                newValues: $"IsActive: true, ActiveViolations: 0, PardonedViolations: {oldActiveViolationCount}",
                severity: "Warning"
            );

            return Json(new
            {
                success = true,
                message = wasLocked
                    ? $"Đã ân xá {oldActiveViolationCount} lỗi vi phạm và mở khóa tài khoản."
                    : $"Đã ân xá {oldActiveViolationCount} lỗi vi phạm. Tài khoản hiện không còn lỗi Active."
            });
        }
    }
}