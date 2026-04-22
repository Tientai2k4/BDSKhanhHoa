using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
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

        public PropertyReportsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. DANH SÁCH BÁO CÁO (Lọc & Phân trang)
        // ==========================================
        public async Task<IActionResult> Index(string status = "Pending", string searchString = null)
        {
            var query = _context.PropertyReports
                .Include(r => r.Property)
                .Include(r => r.User) // Người báo cáo
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

            // Sắp xếp: Mới nhất lên đầu, Pending lên đầu
            var reports = await query
                .OrderByDescending(r => r.Status == "Pending")
                .ThenByDescending(r => r.CreatedAt)
                .ToListAsync();

            return View(reports);
        }

        // ==========================================
        // 2. CHI TIẾT ĐỂ ĐỐI CHIẾU
        // ==========================================
        public async Task<IActionResult> Details(int id)
        {
            var report = await _context.PropertyReports
                .Include(r => r.Property).ThenInclude(p => p.User) // Lấy thông tin người đăng tin
                .Include(r => r.User) // Người báo cáo
                .FirstOrDefaultAsync(r => r.ReportID == id);

            if (report == null) return NotFound();

            // Đếm số vi phạm trước đó của người đăng tin này
            if (report.Property != null && report.Property.User != null)
            {
                ViewBag.SellerViolationsCount = await _context.UserViolations
                    .CountAsync(v => v.UserID == report.Property.UserID && v.Status == "Active");
            }

            return View(report);
        }

        // ==========================================
        // 3. XỬ LÝ BÁO CÁO (CORE LOGIC - DÙNG TRANSACTION)
        // ==========================================
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

            // BẮT ĐẦU TRANSACTION DB
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                report.UpdatedAt = DateTime.Now;
                if (actionType == "Reject")
                {
                    // 3.1 BÁC BỎ BÁO CÁO (Tin đăng không vi phạm)
                    report.Status = "Rejected";

                    // Thông báo cho người báo cáo (Có link dẫn về xem lại tin đăng đó)
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
                    // 3.2 CHẤP THUẬN BÁO CÁO (Tin đăng có lỗi)
                    report.Status = "Processed";
                    int sellerId = report.Property!.UserID;

                    // A. Ghi log vi phạm cho người đăng tin
                    _context.UserViolations.Add(new UserViolation
                    {
                        UserID = sellerId,
                        Reason = report.Reason,
                        Description = $"Phát hiện từ Báo cáo #{report.ReportID}. Quyết định của Admin: {adminNote}",
                        ReportedBy = adminId,
                        Status = "Active",
                        CreatedAt = DateTime.Now
                    });

                    // B. Thông báo cho NGƯỜI BÁO CÁO
                    _context.Notifications.Add(new Notification
                    {
                        UserID = report.ReportedBy,
                        Title = "Đã xử lý báo cáo vi phạm",
                        Content = $"Báo cáo của bạn về tin đăng #{report.PropertyID} là chính xác. Chúng tôi đã áp dụng biện pháp kỷ luật đối với người đăng. Cảm ơn bạn đã giúp cộng đồng bất động sản Khánh Hòa minh bạch hơn!",
                        ActionUrl = "/", // Dẫn về trang chủ để tìm kiếm tiếp
                        ActionText = "Tiếp tục tìm kiếm",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    if (actionType == "DeleteProperty")
                    {
                        // C1. Xóa / Ẩn tin đăng
                        report.Property.Status = "Rejected";
                        report.Property.IsDeleted = true;

                        // Thông báo gỡ tin cho NGƯỜI ĐĂNG (Có link dẫn về trang Quản lý tin đăng của họ)
                        _context.Notifications.Add(new Notification
                        {
                            UserID = sellerId,
                            Title = "Tin đăng bị gỡ bỏ do vi phạm",
                            Content = $"Tin đăng '{report.Property.Title}' của bạn đã bị gỡ bỏ do vi phạm quy định: {report.Reason}.\n\nGhi chú từ hệ thống: {adminNote}.\n\nTài khoản của bạn đã bị ghi nhận 1 lỗi vi phạm. Nếu tiếp tục vi phạm, tài khoản có thể bị khóa vĩnh viễn.",
                            ActionUrl = "/Property/MyAds", // Dẫn về trang danh sách tin của User
                            ActionText = "Xem danh sách tin của tôi",
                            IsRead = false,
                            CreatedAt = DateTime.Now
                        });
                    }
                    else
                    {
                        // C2. Chỉ cảnh cáo (Tin vẫn giữ nguyên) - CHỖ NÀY QUAN TRỌNG NHẤT
                        _context.Notifications.Add(new Notification
                        {
                            UserID = sellerId,
                            Title = "Cảnh cáo vi phạm tin đăng",
                            Content = $"Tin đăng '{report.Property.Title}' của bạn bị cộng đồng báo cáo vi phạm: {report.Reason}.\n\nYêu cầu bạn chỉnh sửa lại nội dung ngay lập tức.\n\nGhi chú từ Admin: {adminNote}.",
                            ActionUrl = $"/Property/Edit/{report.PropertyID}", // LINK DẪN TRỰC TIẾP VÀO TRANG SỬA TIN
                            ActionText = "Sửa tin đăng ngay", // TÊN NÚT HIỂN THỊ LÊN VIEW
                            IsRead = false,
                            CreatedAt = DateTime.Now
                        });
                    }
                }
                else
                {
                    return Json(new { success = false, message = "Hành động không hợp lệ." });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Json(new { success = true, message = "Xử lý báo cáo, ghi log và gửi thông báo thành công!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Json(new { success = false, message = "Lỗi hệ thống khi xử lý: " + ex.Message });
            }
        }
    }
}