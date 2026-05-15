using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertiesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService;

        public PropertiesController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string status = "", bool duplicateOnly = false, string keyword = "")
        {
            await CheckDuplicatesAsync();

            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .Where(p => p.IsDeleted == false)
                .AsQueryable();

            if (duplicateOnly)
            {
                query = query.Where(p => p.IsDuplicate);
            }
            else if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(p => p.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim().ToLower();

                query = query.Where(p =>
                    p.Title.ToLower().Contains(keyword) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains(keyword)) ||
                    (p.User != null && p.User.FullName != null && p.User.FullName.ToLower().Contains(keyword)) ||
                    (p.User != null && p.User.Phone != null && p.User.Phone.Contains(keyword)));
            }

            var properties = await query
                .OrderByDescending(p => p.IsDuplicate)
                .ThenBy(p => p.Status == "Pending" ? 0 : p.Status == "Rejected" ? 1 : 2)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.CurrentStatus = status;
            ViewBag.DuplicateOnly = duplicateOnly;
            ViewBag.Keyword = keyword;

            ViewBag.TotalCount = await _context.Properties.CountAsync(p => p.IsDeleted == false);
            ViewBag.PendingCount = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
            ViewBag.ApprovedCount = await _context.Properties.CountAsync(p => p.Status == "Approved" && p.IsDeleted == false);
            ViewBag.RejectedCount = await _context.Properties.CountAsync(p => p.Status == "Rejected" && p.IsDeleted == false);
            ViewBag.DuplicateCount = await _context.Properties.CountAsync(p => p.IsDuplicate && p.IsDeleted == false);
            ViewBag.SoldCount = await _context.Properties.CountAsync(p => p.Status == "Sold" && p.IsDeleted == false);
            ViewBag.RentedCount = await _context.Properties.CountAsync(p => p.Status == "Rented" && p.IsDeleted == false);

            return View("Index", properties);
        }

        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            return await Index(status: "Pending", duplicateOnly: false, keyword: "");
        }

        private async Task CheckDuplicatesAsync()
        {
            DateTime duplicateFrom = DateTime.Now.AddDays(-30);

            var propertiesToCheck = await _context.Properties
                .Where(p =>
                    p.IsDeleted == false &&
                    p.Status != "Rejected" &&
                    p.Status != "Deleted" &&
                    p.Status != "Sold" &&
                    p.Status != "Rented" &&
                    p.CreatedAt >= duplicateFrom)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            foreach (var prop in propertiesToCheck)
            {
                var matchedProperty = await FindBestDuplicateAsync(prop, duplicateFrom);

                if (matchedProperty != null)
                {
                    prop.IsDuplicate = true;
                    prop.DuplicateReason =
                        $"Hệ thống phát hiện tin đăng này có dấu hiệu trùng với tin #{matchedProperty.PropertyID}: \"{matchedProperty.Title}\". " +
                        $"Tiêu chí nghi trùng có thể gồm tiêu đề, địa chỉ, khu vực, loại bất động sản, giá hoặc diện tích trong vòng 30 ngày gần đây.";
                }
                else
                {
                    if (prop.IsDuplicate && prop.Status != "Rejected")
                    {
                        prop.IsDuplicate = false;
                        prop.DuplicateReason = null;
                    }
                }
            }

            await _context.SaveChangesAsync();
        }

        private async Task<Property?> FindBestDuplicateAsync(Property prop, DateTime duplicateFrom)
        {
            string currentTitle = NormalizeText(prop.Title);
            string currentAddress = NormalizeText(prop.AddressDetail);

            var candidates = await _context.Properties
                .AsNoTracking()
                .Where(p =>
                    p.PropertyID != prop.PropertyID &&
                    p.UserID == prop.UserID &&
                    p.IsDeleted == false &&
                    p.Status != "Rejected" &&
                    p.Status != "Deleted" &&
                    p.Status != "Sold" &&
                    p.Status != "Rented" &&
                    p.CreatedAt >= duplicateFrom)
                .OrderByDescending(p => p.CreatedAt)
                .Take(80)
                .ToListAsync();

            foreach (var item in candidates)
            {
                string otherTitle = NormalizeText(item.Title);
                string otherAddress = NormalizeText(item.AddressDetail);

                bool sameTitle =
                    !string.IsNullOrWhiteSpace(currentTitle) &&
                    currentTitle == otherTitle;

                bool nearTitle =
                    !string.IsNullOrWhiteSpace(currentTitle) &&
                    !string.IsNullOrWhiteSpace(otherTitle) &&
                    currentTitle.Length >= 18 &&
                    otherTitle.Length >= 18 &&
                    (currentTitle.Contains(otherTitle) || otherTitle.Contains(currentTitle));

                bool sameAddress =
                    !string.IsNullOrWhiteSpace(currentAddress) &&
                    currentAddress == otherAddress;

                bool sameWardAndType =
                    prop.WardID == item.WardID &&
                    prop.TypeID == item.TypeID;

                bool nearPrice =
                    prop.Price.HasValue &&
                    item.Price.HasValue &&
                    Math.Abs(prop.Price.Value - item.Price.Value) <= 1000000M;

                bool nearArea =
                    prop.AreaSize.HasValue &&
                    item.AreaSize.HasValue &&
                    Math.Abs(prop.AreaSize.Value - item.AreaSize.Value) <= 2M;

                bool strongSameInfo =
                    sameWardAndType &&
                    nearPrice &&
                    nearArea;

                if (sameTitle || sameAddress || (nearTitle && sameWardAndType) || (strongSameInfo && nearTitle))
                {
                    return item;
                }
            }

            return null;
        }

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();

            foreach (var ch in text)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(ch);

                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            string normalized = builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace("đ", "d");

            normalized = Regex.Replace(normalized, @"\s+", " ");
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", "");

            return normalized.Trim();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus, string? reason)
        {
            var property = await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PostServicePackage)
                .FirstOrDefaultAsync(p => p.PropertyID == id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản này!";
                return RedirectBackToIndex();
            }

            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            if (newStatus == "Approved")
            {
                if (property.IsDuplicate)
                {
                    TempData["Error"] = "Tin này đang bị cảnh báo trùng lặp. Vui lòng yêu cầu người đăng sửa lại hoặc bỏ cảnh báo trùng nếu đã kiểm tra chắc chắn không trùng.";
                    return RedirectBackToIndex();
                }

                property.Status = "Approved";
                property.ApprovedAt = DateTime.Now;
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = null;
                property.IsDuplicate = false;
                property.DuplicateReason = null;

                if (property.PostServicePackage != null)
                {
                    property.VipExpiryDate = DateTime.Now.AddDays(property.PostServicePackage.DurationDays);
                }

                _context.Notifications.Add(new Notification
                {
                    UserID = property.UserID,
                    Title = "Tin đăng đã được duyệt",
                    Content = $"Tin đăng \"{property.Title}\" đã được quản trị viên phê duyệt và đang hiển thị trên hệ thống.",
                    ActionUrl = $"/Property/Details/{property.PropertyID}",
                    ActionText = "Xem tin đăng",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });

                await _context.SaveChangesAsync();

                await _auditLogService.LogAsync(
                    adminId,
                    "Phê duyệt tin đăng",
                    "Properties",
                    $"PropertyID: {id} - {property.Title}",
                    severity: "Info");

                TempData["Success"] = $"Đã phê duyệt tin: {property.Title}";
            }
            else if (newStatus == "Rejected")
            {
                string rejectReason = string.IsNullOrWhiteSpace(reason)
                    ? "Tin đăng vi phạm chính sách, sai thông tin hoặc cần chỉnh sửa trước khi hiển thị."
                    : reason.Trim();

                bool isFirstTimeRejection = string.IsNullOrWhiteSpace(property.RejectionReason);

                property.Status = "Rejected";
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = rejectReason;

                if (isFirstTimeRejection)
                {
                    await RefundPropertyCreditAsync(property);
                }

                _context.Notifications.Add(new Notification
                {
                    UserID = property.UserID,
                    Title = "Tin đăng cần chỉnh sửa",
                    Content = $"Tin đăng \"{property.Title}\" chưa được duyệt. Lý do: {rejectReason}",
                    ActionUrl = $"/Property/Edit/{property.PropertyID}",
                    ActionText = "Sửa tin ngay",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });

                await _context.SaveChangesAsync();

                await _auditLogService.LogAsync(
                    adminId,
                    "Từ chối tin đăng",
                    "Properties",
                    $"PropertyID: {id} - Lý do: {property.RejectionReason}",
                    severity: "Warning");

                TempData["Success"] = $"Đã từ chối tin: {property.Title} {(isFirstTimeRejection ? "(Đã hoàn lượt đăng)" : "(Không hoàn lượt vì đã từng từ chối)")}";
            }

            return RedirectBackToIndex();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NotifyDuplicate(int id, string? reason)
        {
            var property = await _context.Properties
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.PropertyID == id && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng cần xử lý.";
                return RedirectBackToIndex();
            }

            string duplicateReason = string.IsNullOrWhiteSpace(reason)
                ? "Tin đăng có dấu hiệu trùng lặp với tin đã đăng trước đó. Vui lòng chỉnh sửa tiêu đề, mô tả, hình ảnh, giá, địa chỉ hoặc nội dung để tránh trùng lặp."
                : reason.Trim();

            bool isFirstTimeRejection = string.IsNullOrWhiteSpace(property.RejectionReason);

            property.IsDuplicate = true;
            property.DuplicateReason = duplicateReason;
            property.Status = "Rejected";
            property.RejectionReason = duplicateReason;
            property.UpdatedAt = DateTime.Now;

            if (isFirstTimeRejection)
            {
                await RefundPropertyCreditAsync(property);
            }

            string actionUrl = $"/Property/Edit/{property.PropertyID}";

            bool alreadyHasUnreadNotice = await _context.Notifications.AnyAsync(n =>
                n.UserID == property.UserID &&
                n.IsRead == false &&
                n.ActionUrl == actionUrl &&
                n.Title != null &&
                n.Title.Contains("trùng lặp"));

            if (!alreadyHasUnreadNotice)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = property.UserID,
                    Title = "Cảnh báo tin đăng trùng lặp",
                    Content =
                        $"Tin đăng \"{property.Title}\" bị hệ thống/quản trị viên đánh dấu là có dấu hiệu trùng lặp. " +
                        $"Vui lòng chỉnh sửa lại nội dung để tin không trùng với tin đã đăng trước đó. Lý do: {duplicateReason}",
                    ActionUrl = actionUrl,
                    ActionText = "Sửa tin ngay",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();

            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            await _auditLogService.LogAsync(
                adminId,
                "Gửi cảnh báo tin đăng trùng lặp",
                "Properties",
                $"PropertyID: {id} - UserID: {property.UserID} - Lý do: {duplicateReason}",
                severity: "Warning");

            TempData["Success"] = "Đã gửi thông báo yêu cầu người đăng sửa lại tin trùng lặp.";
            return RedirectBackToIndex();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearDuplicateFlag(int id)
        {
            var property = await _context.Properties.FirstOrDefaultAsync(p => p.PropertyID == id && p.IsDeleted == false);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng.";
                return RedirectBackToIndex();
            }

            property.IsDuplicate = false;
            property.DuplicateReason = null;
            property.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            await _auditLogService.LogAsync(
                adminId,
                "Bỏ cảnh báo trùng lặp tin đăng",
                "Properties",
                $"PropertyID: {id}",
                severity: "Info");

            TempData["Success"] = "Đã bỏ cảnh báo trùng lặp cho tin đăng.";
            return RedirectBackToIndex();
        }

        private async Task RefundPropertyCreditAsync(Property property)
        {
            var transactionToRefund = await _context.Transactions
                .Where(t =>
                    t.PropertyID == property.PropertyID &&
                    t.UserID == property.UserID &&
                    t.Status == "Success")
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync();

            if (transactionToRefund != null)
            {
                transactionToRefund.PropertyID = null;
            }
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

                int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

                await _auditLogService.LogAsync(
                    adminId,
                    "Xóa mềm tin đăng",
                    "Properties",
                    $"PropertyID: {id}",
                    severity: "Danger");

                TempData["Success"] = "Đã đưa tin đăng vào thùng rác thành công!";
            }
            else
            {
                TempData["Error"] = "Không tìm thấy tin đăng để xóa.";
            }

            return RedirectBackToIndex();
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

            builder.Append("\uFEFF");
            builder.AppendLine("ID,Tiêu đề,Người đăng,Loại BĐS,Gói tin,Trạng thái,Trùng lặp,Lý do trùng,Lý do từ chối,Ngày tạo,Ngày duyệt,Ngày giao dịch,Ngày hết hạn VIP,Tự động duyệt");

            foreach (var p in properties)
            {
                string title = CsvText(p.Title);
                string userName = CsvText(p.User?.FullName);
                string typeName = CsvText(p.PropertyType?.TypeName);
                string packageName = CsvText(p.PostServicePackage?.PackageName);
                string status = CsvText(p.Status);
                string duplicate = p.IsDuplicate ? "Có" : "Không";
                string duplicateReason = CsvText(p.DuplicateReason);
                string rejectionReason = CsvText(p.RejectionReason);
                string transactedAt = p.SoldAt.HasValue ? p.SoldAt.Value.ToString("dd/MM/yyyy HH:mm") : "";

                builder.AppendLine(
                    $"{p.PropertyID}," +
                    $"{title}," +
                    $"{userName}," +
                    $"{typeName}," +
                    $"{packageName}," +
                    $"{status}," +
                    $"{duplicate}," +
                    $"{duplicateReason}," +
                    $"{rejectionReason}," +
                    $"{p.CreatedAt:dd/MM/yyyy HH:mm}," +
                    $"{p.ApprovedAt?.ToString("dd/MM/yyyy HH:mm") ?? ""}," +
                    $"{transactedAt}," +
                    $"{p.VipExpiryDate?.ToString("dd/MM/yyyy") ?? ""}," +
                    $"{(p.IsAutoApproved ? "Có" : "Không")}");
            }

            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

            await _auditLogService.LogAsync(
                adminId,
                "Xuất báo cáo dữ liệu tin đăng",
                "Properties",
                "Export CSV",
                severity: "Info");

            return File(
                Encoding.UTF8.GetBytes(builder.ToString()),
                "text/csv",
                $"BaoCaoTinDang_{DateTime.Now:yyyyMMdd}.csv");
        }

        private static string CsvText(string? value)
        {
            value ??= "";
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private IActionResult RedirectBackToIndex()
        {
            string referer = Request.Headers["Referer"].ToString();

            if (!string.IsNullOrWhiteSpace(referer))
            {
                return Redirect(referer);
            }

            return RedirectToAction(nameof(Index));
        }
    }
}