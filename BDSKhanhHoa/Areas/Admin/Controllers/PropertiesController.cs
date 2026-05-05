using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Models;
using System.Text;

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

        [HttpGet]
        public async Task<IActionResult> Index(string status = "")
        {
            await CheckDuplicatesAsync();

            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(p => p.Status == status);
            }

            var properties = await query
                .OrderBy(p => p.Status == "Pending" ? 0 : 1)
                .ThenByDescending(p => p.IsDuplicate)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.PendingCount = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
            ViewBag.DuplicateCount = properties.Count(p => p.Status == "Pending" && p.IsDuplicate);

            // Đếm tách biệt số lượng Đã Bán và Đã Cho Thuê cho Admin
            ViewBag.SoldCount = await _context.Properties.CountAsync(p => p.Status == "Sold" && p.IsDeleted == false);
            ViewBag.RentedCount = await _context.Properties.CountAsync(p => p.Status == "Rented" && p.IsDeleted == false);

            return View("Index", properties);
        }
        private async Task CheckDuplicatesAsync()
        {
            var pendingProperties = await _context.Properties
                .Where(p => p.Status == "Pending" && !p.IsDuplicate)
                .ToListAsync();

            foreach (var prop in pendingProperties)
            {
                var isDup = await _context.Properties.AnyAsync(p =>
                    p.PropertyID != prop.PropertyID &&
                    p.UserID == prop.UserID &&
                    p.Status != "Rejected" &&
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
                if (prop.PostServicePackage != null && prop.PostServicePackage.PriorityLevel >= 3)
                {
                    prop.Status = "Approved";
                    prop.ApprovedAt = DateTime.Now;
                    prop.UpdatedAt = DateTime.Now;
                    prop.IsAutoApproved = true;
                    prop.VipExpiryDate = DateTime.Now.AddDays(prop.PostServicePackage.DurationDays);
                    prop.RejectionReason = null;
                    approvedCount++;
                }
            }

            if (approvedCount > 0) await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã duyệt tự động {approvedCount} tin VIP.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            await CheckDuplicatesAsync();
            var pendingProperties = await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Pending" && p.IsDeleted == false)
                .OrderByDescending(p => p.IsDuplicate)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = "Pending";
            ViewBag.PendingCount = pendingProperties.Count;
            ViewBag.SoldCount = await _context.Properties.CountAsync(p => p.Status == "Sold" && p.IsDeleted == false);
            ViewBag.RentedCount = await _context.Properties.CountAsync(p => p.Status == "Rented" && p.IsDeleted == false);

            return View("Index", pendingProperties);
        }
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

                if (property.PostServicePackage != null)
                {
                    property.VipExpiryDate = DateTime.Now.AddDays(property.PostServicePackage.DurationDays);
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = $"Đã phê duyệt tin: {property.Title}";
            }
            else if (newStatus == "Rejected")
            {
                bool isFirstTimeRejection = string.IsNullOrEmpty(property.RejectionReason);

                property.Status = "Rejected";
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = string.IsNullOrEmpty(reason) ? "Tin đăng vi phạm chính sách hoặc sai thông tin." : reason;

                if (isFirstTimeRejection)
                {
                    var transactionToRefund = await _context.Transactions
                        .Where(t => t.PropertyID == property.PropertyID && t.UserID == property.UserID && t.Status == "Success")
                        .OrderByDescending(t => t.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (transactionToRefund != null) transactionToRefund.PropertyID = null;
                }

                await _context.SaveChangesAsync();
                TempData["Success"] = $"Đã từ chối tin: {property.Title} {(isFirstTimeRejection ? "(Đã hoàn lượt)" : "(Không hoàn lượt vì sửa lại)")}";
            }

            string referer = Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer)) return Redirect(referer);

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var property = await _context.Properties.FindAsync(id);
            if (property != null)
            {
                property.IsDeleted = true;
                property.UpdatedAt = DateTime.Now;
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã đưa tin đăng vào thùng rác thành công!";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy tin đăng để xóa.";
            }
            return RedirectToAction(nameof(Index));
        }

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

            // Xử lý BOM để hiển thị tiếng Việt trên Excel
            builder.Append("\uFEFF");
            builder.AppendLine("ID,Tiêu đề,Người đăng,Loại BĐS,Gói tin,Trạng thái,Ngày tạo,Ngày duyệt,Ngày giao dịch,Ngày hết hạn VIP,Tự động duyệt");

            foreach (var p in properties)
            {
                string title = $"\"{p.Title?.Replace("\"", "\"\"")}\"";
                string userName = $"\"{p.User?.FullName?.Replace("\"", "\"\"")}\"";

                string transactedAt = p.SoldAt.HasValue ? p.SoldAt.Value.ToString("dd/MM/yyyy HH:mm") : "";

                builder.AppendLine($"{p.PropertyID},{title},{userName},{p.PropertyType?.TypeName},{p.PostServicePackage?.PackageName},{p.Status},{p.CreatedAt:dd/MM/yyyy HH:mm},{p.ApprovedAt?.ToString("dd/MM/yyyy HH:mm") ?? ""},{transactedAt},{p.VipExpiryDate?.ToString("dd/MM/yyyy") ?? ""},{(p.IsAutoApproved ? "Có" : "Không")}");
            }

            return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv", $"BaoCaoTinDang_{DateTime.Now:yyyyMMdd}.csv");
        }
    }
}