using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Models; // Đảm bảo import Models
using System.Text; // Để xuất CSV

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertiesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertiesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. TỔNG HỢP DANH SÁCH TIN ĐĂNG (CÓ LỌC TRÙNG LẶP)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            // Tự động kiểm tra trùng lặp cho các tin Pending mới vào
            await CheckDuplicatesAsync();

            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage) // Lấy thông tin gói
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.Status == status);
            }

            var properties = await query
                .OrderBy(p => p.Status == "Pending" ? 0 : 1)
                .ThenByDescending(p => p.IsDuplicate) // Ưu tiên hiện tin cảnh báo trùng lặp lên trên trong danh sách chờ
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.PendingCount = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
            ViewBag.DuplicateCount = properties.Count(p => p.Status == "Pending" && p.IsDuplicate);

            return View("Index", properties);
        }

        // ==========================================
        // THÊM: HÀM KIỂM TRA TRÙNG LẶP & DUYỆT TỰ ĐỘNG
        // ==========================================
        private async Task CheckDuplicatesAsync()
        {
            var pendingProperties = await _context.Properties
                .Where(p => p.Status == "Pending" && !p.IsDuplicate)
                .ToListAsync();

            foreach (var prop in pendingProperties)
            {
                // Logic lọc trùng lặp đơn giản: Cùng người đăng, cùng Tiêu đề hoặc Địa chỉ chi tiết trong vòng 7 ngày
                var isDup = await _context.Properties.AnyAsync(p =>
                    p.PropertyID != prop.PropertyID &&
                    p.UserID == prop.UserID &&
                    p.Status != "Rejected" && // Không tính các tin đã bị từ chối
                    p.IsDeleted == false &&
                    p.CreatedAt >= DateTime.Now.AddDays(-7) &&
                    (p.Title.ToLower() == prop.Title.ToLower() ||
                     (p.AddressDetail != null && p.AddressDetail.ToLower() == prop.AddressDetail.ToLower())));

                if (isDup)
                {
                    prop.IsDuplicate = true;
                    prop.DuplicateReason = "Hệ thống phát hiện tin đăng có thể trùng lặp với một tin khác của cùng người dùng trong 7 ngày qua.";
                }
            }
            await _context.SaveChangesAsync();
        }

        // Action để kích hoạt duyệt tự động (Có thể gọi qua AJAX hoặc chạy nền)
        [HttpPost]
        public async Task<IActionResult> RunAutoApprove()
        {
            var pendingVipProps = await _context.Properties
                .Include(p => p.PostServicePackage)
                .Where(p => p.Status == "Pending" && p.IsDeleted == false && !p.IsDuplicate)
                .ToListAsync();

            int approvedCount = 0;
            foreach (var prop in pendingVipProps)
            {
                // Điều kiện duyệt tự động: Ví dụ Gói VIP Kim Cương (PriorityLevel >= 3)
                if (prop.PostServicePackage != null && prop.PostServicePackage.PriorityLevel >= 3)
                {
                    prop.Status = "Approved";
                    prop.ApprovedAt = DateTime.Now;
                    prop.UpdatedAt = DateTime.Now;
                    prop.IsAutoApproved = true;

                    // Tính lại ngày hết hạn VIP từ lúc DUYỆT
                    prop.VipExpiryDate = DateTime.Now.AddDays(prop.PostServicePackage.DurationDays);

                    // Reset lý do từ chối nếu có
                    prop.RejectionReason = null;

                    approvedCount++;
                }
            }

            if (approvedCount > 0)
                await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã duyệt tự động {approvedCount} tin VIP.";
            return RedirectToAction(nameof(Index));
        }


        // ==========================================
        // 2. MÀN HÌNH KIỂM DUYỆT NHANH
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            await CheckDuplicatesAsync();
            var pendingProperties = await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Pending" && p.IsDeleted == false)
                .OrderByDescending(p => p.IsDuplicate) // Đẩy tin báo trùng lên đầu
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = "Pending";
            ViewBag.PendingCount = pendingProperties.Count;

            return View("Index", pendingProperties);
        }

        // ==========================================
        // 3. HÀM DUYỆT / TỪ CHỐI (CẬP NHẬT LOGIC HOÀN LƯỢT & NGÀY VIP)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus, string? reason)
        {
            var property = await _context.Properties
                .Include(p => p.PostServicePackage)
                .FirstOrDefaultAsync(p => p.PropertyID == id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản này!";
                return RedirectToAction(nameof(Index));
            }

            if (newStatus == "Approved")
            {
                property.Status = "Approved";
                property.ApprovedAt = DateTime.Now;
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = null;

                // TÍNH NGÀY VIP TỪ LÚC DUYỆT
                if (property.PostServicePackage != null)
                {
                    property.VipExpiryDate = DateTime.Now.AddDays(property.PostServicePackage.DurationDays);
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = $"Đã phê duyệt tin: {property.Title}";
            }
            else if (newStatus == "Rejected")
            {
                // Kiểm tra xem tin này đã từng bị từ chối trước đó chưa (nếu sửa lại thì RejectionReason vẫn còn trước khi đổi trạng thái)
                // Logic: Nếu đang Pending và chưa từng bị Rejected (RejectionReason rỗng), thì mới hoàn lượt.
                // Nếu người dùng đã sửa và gửi lại (Status thành Pending nhưng RejectionReason cũ vẫn còn trong DB hoặc bạn thiết kế trạng thái "Resubmitted"),
                // thì không hoàn lượt lần 2. Ở đây ta dùng cờ kiểm tra đơn giản:
                bool isFirstTimeRejection = string.IsNullOrEmpty(property.RejectionReason);

                property.Status = "Rejected";
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = string.IsNullOrEmpty(reason) ? "Tin đăng vi phạm chính sách hoặc sai thông tin." : reason;

                // HOÀN TRẢ LƯỢT ĐĂNG
                if (isFirstTimeRejection)
                {
                    var transactionToRefund = await _context.Transactions
                        .Where(t => t.PropertyID == property.PropertyID && t.UserID == property.UserID && t.Status == "Success")
                        .OrderByDescending(t => t.CreatedAt) // Lấy giao dịch gần nhất
                        .FirstOrDefaultAsync();

                    if (transactionToRefund != null)
                    {
                        // Cách 1: Reset PropertyID về null để trả lại gói (khuyên dùng nếu cấu trúc DB cho phép)
                        transactionToRefund.PropertyID = null;

                        // Cách 2: Tạo một Transaction mới kiểu "Refund" (Tùy logic kế toán của bạn)
                        // _context.Transactions.Add(new Transaction { UserID = property.UserID, PackageID = property.PackageID, Type = "Hoàn lượt do tin bị từ chối", Amount = 0, Status = "Success", CreatedAt = DateTime.Now });
                    }
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = $"Đã từ chối tin: {property.Title} {(isFirstTimeRejection ? "(Đã hoàn lượt)" : "(Không hoàn lượt vì sửa lại)")}";
            }

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer)) return Redirect(referer);

            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 4. XÓA TIN ĐĂNG
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property != null)
            {
                property.IsDeleted = true;
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã đưa tin đăng vào thùng rác thành công!";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy tin đăng để xóa.";
            }
            return RedirectToAction(nameof(Index));
        }

        // ==========================================
        // 5. THÊM MỚI: XUẤT BÁO CÁO (CSV)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> ExportReport()
        {
            var properties = await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.IsDeleted == false)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var builder = new StringBuilder();
            builder.AppendLine("ID,Tiêu đề,Người đăng,Loại BĐS,Gói tin,Trạng thái,Ngày tạo,Ngày duyệt,Ngày hết hạn VIP,Tự động duyệt");

            foreach (var p in properties)
            {
                // Escape commas in title and names
                string title = $"\"{p.Title?.Replace("\"", "\"\"")}\"";
                string userName = $"\"{p.User?.FullName?.Replace("\"", "\"\"")}\"";

                builder.AppendLine($"{p.PropertyID},{title},{userName},{p.PropertyType?.TypeName},{p.PostServicePackage?.PackageName},{p.Status},{p.CreatedAt:dd/MM/yyyy HH:mm},{p.ApprovedAt?.ToString("dd/MM/yyyy HH:mm") ?? ""},{p.VipExpiryDate?.ToString("dd/MM/yyyy") ?? ""},{(p.IsAutoApproved ? "Có" : "Không")}");
            }

            return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", $"BaoCaoTinDang_{DateTime.Now:yyyyMMdd}.csv");
        }
    }
}