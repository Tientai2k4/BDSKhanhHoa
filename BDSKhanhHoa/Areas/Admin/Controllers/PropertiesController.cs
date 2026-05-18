using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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

        // =====================================================
        // DANH SÁCH QUẢN LÝ TIN ĐĂNG
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Index(string status = "", bool duplicateOnly = false, string keyword = "")
        {
            await CheckDuplicatesAsync();

            var query = _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
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

        // =====================================================
        // KIỂM TRA TIN TRÙNG
        // =====================================================
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

        // =====================================================
        // DUYỆT / TỪ CHỐI TIN
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string newStatus, string? reason)
        {
            var property = await GetPropertyForAuditAsync(id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy thông tin bất động sản này.";
                return RedirectBackToIndex();
            }

            if (property.Status == "Sold" || property.Status == "Rented")
            {
                TempData["Error"] = "Tin đã bán hoặc đã cho thuê, không thể duyệt hoặc từ chối lại.";
                return RedirectBackToIndex();
            }

            int adminId = GetCurrentAdminId();

            if (newStatus == "Approved")
            {
                if (property.IsDuplicate)
                {
                    TempData["Error"] = "Tin này đang bị cảnh báo trùng lặp. Vui lòng yêu cầu người đăng sửa lại hoặc bỏ cảnh báo trùng nếu đã kiểm tra chắc chắn không trùng.";
                    return RedirectBackToIndex();
                }

                string oldValues = BuildPropertyAuditJson(property, "Trước khi Admin phê duyệt tin đăng.");

                property.Status = "Approved";
                property.ApprovedAt = DateTime.Now;
                property.UpdatedAt = DateTime.Now;
                property.RejectionReason = null;
                property.IsDuplicate = false;
                property.DuplicateReason = null;

                if (property.PostServicePackage != null && property.PostServicePackage.DurationDays > 0)
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

                var updatedProperty = await GetPropertyForAuditAsync(property.PropertyID);
                string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Sau khi Admin phê duyệt tin đăng.");

                await _auditLogService.LogAsync(
                    adminId,
                    "Phê duyệt tin đăng",
                    "Properties",
                    $"PropertyID: {property.PropertyID}",
                    oldValues: oldValues,
                    newValues: newValues,
                    severity: "Info");

                TempData["Success"] = $"Đã phê duyệt tin: {property.Title}";
                return RedirectBackToIndex();
            }

            if (newStatus == "Rejected")
            {
                string rejectReason = string.IsNullOrWhiteSpace(reason)
                    ? "Tin đăng vi phạm chính sách, sai thông tin hoặc cần chỉnh sửa trước khi hiển thị."
                    : reason.Trim();

                bool isFirstTimeRejection = string.IsNullOrWhiteSpace(property.RejectionReason);

                string oldValues = BuildPropertyAuditJson(property, "Trước khi Admin từ chối tin đăng.");

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

                var updatedProperty = await GetPropertyForAuditAsync(property.PropertyID);
                string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Sau khi Admin từ chối tin đăng.");

                await _auditLogService.LogAsync(
                    adminId,
                    "Từ chối tin đăng",
                    "Properties",
                    $"PropertyID: {property.PropertyID}",
                    oldValues: oldValues,
                    newValues: newValues,
                    severity: "Warning");

                TempData["Success"] = $"Đã từ chối tin: {property.Title} {(isFirstTimeRejection ? "(Đã hoàn lượt đăng)" : "(Không hoàn lượt vì đã từng từ chối)")}";
                return RedirectBackToIndex();
            }

            TempData["Error"] = "Trạng thái xử lý không hợp lệ.";
            return RedirectBackToIndex();
        }

        // =====================================================
        // GỬI CẢNH BÁO TIN TRÙNG
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> NotifyDuplicate(int id, string? reason)
        {
            var property = await GetPropertyForAuditAsync(id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng cần xử lý.";
                return RedirectBackToIndex();
            }

            if (property.Status == "Sold" || property.Status == "Rented")
            {
                TempData["Error"] = "Tin đã bán hoặc đã cho thuê, không thể gửi cảnh báo trùng lặp.";
                return RedirectBackToIndex();
            }

            string duplicateReason = string.IsNullOrWhiteSpace(reason)
                ? "Tin đăng có dấu hiệu trùng lặp với tin đã đăng trước đó. Vui lòng chỉnh sửa tiêu đề, mô tả, hình ảnh, giá, địa chỉ hoặc nội dung để tránh trùng lặp."
                : reason.Trim();

            bool isFirstTimeRejection = string.IsNullOrWhiteSpace(property.RejectionReason);

            string oldValues = BuildPropertyAuditJson(property, "Trước khi Admin gửi cảnh báo tin trùng lặp.");

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

            int adminId = GetCurrentAdminId();

            var updatedProperty = await GetPropertyForAuditAsync(property.PropertyID);
            string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Sau khi Admin gửi cảnh báo tin trùng lặp.");

            await _auditLogService.LogAsync(
                adminId,
                "Gửi cảnh báo tin đăng trùng lặp",
                "Properties",
                $"PropertyID: {property.PropertyID}",
                oldValues: oldValues,
                newValues: newValues,
                severity: "Warning");

            TempData["Success"] = "Đã gửi thông báo yêu cầu người đăng sửa lại tin trùng lặp.";
            return RedirectBackToIndex();
        }

        // =====================================================
        // BỎ CẢNH BÁO TRÙNG
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearDuplicateFlag(int id)
        {
            var property = await GetPropertyForAuditAsync(id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng.";
                return RedirectBackToIndex();
            }

            string oldValues = BuildPropertyAuditJson(property, "Trước khi Admin bỏ cảnh báo trùng lặp.");

            property.IsDuplicate = false;
            property.DuplicateReason = null;
            property.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            int adminId = GetCurrentAdminId();

            var updatedProperty = await GetPropertyForAuditAsync(property.PropertyID);
            string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Sau khi Admin bỏ cảnh báo trùng lặp.");

            await _auditLogService.LogAsync(
                adminId,
                "Bỏ cảnh báo trùng lặp tin đăng",
                "Properties",
                $"PropertyID: {property.PropertyID}",
                oldValues: oldValues,
                newValues: newValues,
                severity: "Info");

            TempData["Success"] = "Đã bỏ cảnh báo trùng lặp cho tin đăng.";
            return RedirectBackToIndex();
        }

        // =====================================================
        // HOÀN LƯỢT ĐĂNG KHI TỪ CHỐI LẦN ĐẦU
        // =====================================================
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

        // =====================================================
        // XÓA MỀM TIN ĐĂNG
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var property = await GetPropertyForAuditAsync(id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng để xóa.";
                return RedirectBackToIndex();
            }

            string oldValues = BuildPropertyAuditJson(property, "Trước khi Admin xóa mềm tin đăng.");

            property.IsDeleted = true;
            property.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            int adminId = GetCurrentAdminId();

            var updatedProperty = await _context.Properties
                .AsNoTracking()
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .FirstOrDefaultAsync(p => p.PropertyID == property.PropertyID);

            string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Sau khi Admin xóa mềm tin đăng.");

            await _auditLogService.LogAsync(
                adminId,
                "Xóa mềm tin đăng",
                "Properties",
                $"PropertyID: {property.PropertyID}",
                oldValues: oldValues,
                newValues: newValues,
                severity: "Danger");

            TempData["Success"] = "Đã đưa tin đăng vào thùng rác thành công.";
            return RedirectBackToIndex();
        }

        // =====================================================
        // XUẤT BÁO CÁO CSV
        // =====================================================
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
                string status = CsvText(StatusText(p.Status));
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

            int adminId = GetCurrentAdminId();

            string exportValues = JsonSerializer.Serialize(new
            {
                Module = "Properties",
                Action = "Xuất báo cáo dữ liệu tin đăng",
                TotalRows = properties.Count,
                ExportedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
            }, JsonOptions());

            await _auditLogService.LogAsync(
                adminId,
                "Xuất báo cáo dữ liệu tin đăng",
                "Properties",
                "Export CSV",
                oldValues: null,
                newValues: exportValues,
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

        // =====================================================
        // ADMIN CHỈNH SỬA TIN ĐĂNG
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var property = await GetPropertyForAuditAsync(id);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng cần chỉnh sửa.";
                return RedirectToAction(nameof(Index));
            }

            if (IsLockedProperty(property))
            {
                TempData["Error"] = GetLockedPropertyMessage(property);
                return RedirectToAction(nameof(Index));
            }

            await LoadPropertyEditViewBagAsync(property);

            return View("Edit", property);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int PropertyID,
            string Title,
            string Description,
            string? AddressDetail,
            int TypeID,
            int WardID,
            int PackageID,
            decimal? Price,
            decimal? AreaSize,
            decimal? Width,
            decimal? Length,
            string? AdminEditNote)
        {
            var property = await GetPropertyForAuditAsync(PropertyID);

            if (property == null)
            {
                TempData["Error"] = "Không tìm thấy tin đăng cần chỉnh sửa.";
                return RedirectToAction(nameof(Index));
            }

            if (IsLockedProperty(property))
            {
                TempData["Error"] = GetLockedPropertyMessage(property);
                return RedirectToAction(nameof(Index));
            }

            Title = Title?.Trim() ?? "";
            Description = Description?.Trim() ?? "";
            AddressDetail = AddressDetail?.Trim();
            AdminEditNote = AdminEditNote?.Trim();

            if (string.IsNullOrWhiteSpace(Title))
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Tiêu đề tin đăng không được để trống.";
                return View("Edit", property);
            }

            if (string.IsNullOrWhiteSpace(Description))
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Mô tả tin đăng không được để trống.";
                return View("Edit", property);
            }

            var selectedType = await _context.PropertyTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TypeID == TypeID);

            if (selectedType == null)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Loại bất động sản không hợp lệ.";
                return View("Edit", property);
            }

            var selectedWard = await _context.Wards
                .AsNoTracking()
                .Include(w => w.Area)
                .FirstOrDefaultAsync(w => w.WardID == WardID);

            if (selectedWard == null)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Phường / xã không hợp lệ.";
                return View("Edit", property);
            }

            var selectedPackage = await _context.PostServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PackageID == PackageID);

            if (selectedPackage == null)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Gói tin không hợp lệ.";
                return View("Edit", property);
            }

            if (Price.HasValue && Price.Value < 0)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Giá bất động sản không được nhỏ hơn 0.";
                return View("Edit", property);
            }

            if (AreaSize.HasValue && AreaSize.Value < 0)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Diện tích không được nhỏ hơn 0.";
                return View("Edit", property);
            }

            if (Width.HasValue && Width.Value < 0)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Chiều ngang không được nhỏ hơn 0.";
                return View("Edit", property);
            }

            if (Length.HasValue && Length.Value < 0)
            {
                await LoadPropertyEditViewBagAsync(property);
                TempData["Error"] = "Chiều dài không được nhỏ hơn 0.";
                return View("Edit", property);
            }

            int adminId = GetCurrentAdminId();

            int oldPackageID = property.PackageID;
            string oldStatus = property.Status ?? "";
            bool oldIsAutoApproved = property.IsAutoApproved;

            string oldValues = BuildPropertyAuditJson(property, "Dữ liệu trước khi Admin chỉnh sửa tin đăng.");

            property.Title = Title;
            property.Description = Description;
            property.AddressDetail = AddressDetail;
            property.TypeID = TypeID;
            property.WardID = WardID;
            property.PackageID = PackageID;
            property.Price = Price;
            property.AreaSize = AreaSize;
            property.Width = Width;
            property.Length = Length;
            property.UpdatedAt = DateTime.Now;

            property.IsAutoApproved = false;

            if (property.Status == "Rejected")
            {
                property.Status = "Pending";
                property.RejectionReason = null;
            }

            bool isChangePackage = oldPackageID != PackageID;

            if (isChangePackage && selectedPackage.DurationDays > 0)
            {
                property.VipExpiryDate = DateTime.Now.AddDays(selectedPackage.DurationDays);
            }

            _context.Notifications.Add(new Notification
            {
                UserID = property.UserID,
                Title = "Tin đăng đã được quản trị viên chỉnh sửa",
                Content = string.IsNullOrWhiteSpace(AdminEditNote)
                    ? $"Tin đăng \"{property.Title}\" đã được quản trị viên cập nhật một số thông tin để phù hợp với quy định hiển thị."
                    : $"Tin đăng \"{property.Title}\" đã được quản trị viên cập nhật. Ghi chú: {AdminEditNote}",
                ActionUrl = $"/Property/Details/{property.PropertyID}",
                ActionText = "Xem tin đăng",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            var updatedProperty = await GetPropertyForAuditAsync(property.PropertyID);
            string newValues = BuildPropertyAuditJson(updatedProperty ?? property, "Dữ liệu sau khi Admin chỉnh sửa tin đăng.");

            string summaryValues = BuildAdminEditSummaryJson(
                adminId: adminId,
                propertyId: property.PropertyID,
                oldPackageId: oldPackageID,
                newPackageId: PackageID,
                oldStatus: oldStatus,
                newStatus: property.Status ?? "",
                oldIsAutoApproved: oldIsAutoApproved,
                newIsAutoApproved: property.IsAutoApproved,
                changedPackage: isChangePackage,
                adminNote: AdminEditNote);

            await _auditLogService.LogAsync(
                adminId,
                "Admin chỉnh sửa tin đăng",
                "Properties",
                $"PropertyID: {property.PropertyID}",
                oldValues: oldValues,
                newValues: newValues + Environment.NewLine + Environment.NewLine + "===== TOM TAT THAO TAC ADMIN =====" + Environment.NewLine + summaryValues,
                severity: "Warning");

            TempData["Success"] = "Đã cập nhật tin đăng thành công và ghi nhận nhật ký hoạt động Admin.";

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> GetWardsByArea(int areaId)
        {
            if (areaId <= 0)
            {
                return Json(new List<object>());
            }

            var wards = await _context.Wards
                .AsNoTracking()
                .Where(w => w.AreaID == areaId)
                .OrderBy(w => w.WardName)
                .Select(w => new
                {
                    wardID = w.WardID,
                    wardName = w.WardName
                })
                .ToListAsync();

            return Json(wards);
        }

        // =====================================================
        // HELPER LOAD DATA
        // =====================================================
        private async Task<Property?> GetPropertyForAuditAsync(int propertyId)
        {
            return await _context.Properties
                .Include(p => p.User)
                .Include(p => p.PropertyType)
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PostServicePackage)
                .FirstOrDefaultAsync(p => p.PropertyID == propertyId && p.IsDeleted == false);
        }

        private async Task LoadPropertyEditViewBagAsync(Property property)
        {
            ViewBag.ParentTypes = await _context.PropertyTypes
                .AsNoTracking()
                .Where(t => t.ParentID == null)
                .OrderBy(t => t.TypeName)
                .ToListAsync();

            var subTypes = await _context.PropertyTypes
                .AsNoTracking()
                .Where(t => t.ParentID != null)
                .Select(t => new
                {
                    t.TypeID,
                    t.TypeName,
                    t.ParentID
                })
                .OrderBy(t => t.TypeName)
                .ToListAsync();

            ViewBag.SubTypesJson = JsonSerializer.Serialize(subTypes);

            ViewBag.Areas = await _context.Areas
                .AsNoTracking()
                .OrderBy(a => a.AreaName)
                .ToListAsync();

            ViewBag.Wards = await _context.Wards
                .AsNoTracking()
                .OrderBy(w => w.WardName)
                .ToListAsync();

            ViewBag.CurrentAreaID = property.Ward?.AreaID ?? 0;

            ViewBag.Packages = await _context.PostServicePackages
                .AsNoTracking()
                .OrderBy(p => p.PriorityLevel)
                .ThenBy(p => p.Price)
                .ToListAsync();
        }

        // =====================================================
        // HELPER AUDIT LOG
        // =====================================================
        private static string BuildPropertyAuditJson(Property property, string? note = null)
        {
            var data = new
            {
                GhiChu = note,

                TinDang = new
                {
                    property.PropertyID,
                    property.UserID,
                    NguoiDang = property.User?.FullName,
                    SoDienThoaiNguoiDang = property.User?.Phone,
                    EmailNguoiDang = property.User?.Email,

                    property.Title,
                    property.Description,
                    property.AddressDetail,

                    property.TypeID,
                    LoaiBatDongSan = property.PropertyType?.TypeName,

                    property.WardID,
                    PhuongXa = property.Ward?.WardName,
                    AreaID = property.Ward?.AreaID,
                    KhuVuc = property.Ward?.Area?.AreaName,

                    property.PackageID,
                    GoiTin = property.PostServicePackage?.PackageName,
                    LoaiGoi = property.PostServicePackage?.PackageType,

                    GiaTriGia = property.Price,
                    GiaHienThi = MoneyText(property.Price),

                    property.AreaSize,
                    property.Width,
                    property.Length,

                    property.Status,
                    TrangThaiHienThi = StatusText(property.Status),

                    property.IsAutoApproved,
                    property.IsDuplicate,
                    property.DuplicateReason,
                    property.RejectionReason,

                    ApprovedAt = DateTimeText(property.ApprovedAt),
                    VipExpiryDate = DateTimeText(property.VipExpiryDate),
                    SoldAt = DateTimeText(property.SoldAt),
                    CreatedAt = DateTimeText(property.CreatedAt),
                    UpdatedAt = DateTimeText(property.UpdatedAt)
                }
            };

            return JsonSerializer.Serialize(data, JsonOptions());
        }

        private static string BuildAdminEditSummaryJson(
            int adminId,
            int propertyId,
            int oldPackageId,
            int newPackageId,
            string oldStatus,
            string newStatus,
            bool oldIsAutoApproved,
            bool newIsAutoApproved,
            bool changedPackage,
            string? adminNote)
        {
            var data = new
            {
                AdminID = adminId,
                PropertyID = propertyId,

                ThayDoiGoiTin = changedPackage,
                OldPackageID = oldPackageId,
                NewPackageID = newPackageId,

                OldStatus = oldStatus,
                OldStatusText = StatusText(oldStatus),

                NewStatus = newStatus,
                NewStatusText = StatusText(newStatus),

                OldIsAutoApproved = oldIsAutoApproved,
                NewIsAutoApproved = newIsAutoApproved,

                AdminNote = adminNote,
                Time = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
            };

            return JsonSerializer.Serialize(data, JsonOptions());
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true
            };
        }

        // =====================================================
        // TIỆN ÍCH CHUNG
        // =====================================================
        private IActionResult RedirectBackToIndex()
        {
            string referer = Request.Headers["Referer"].ToString();

            if (!string.IsNullOrWhiteSpace(referer))
            {
                return Redirect(referer);
            }

            return RedirectToAction(nameof(Index));
        }

        private int GetCurrentAdminId()
        {
            string? userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (int.TryParse(userId, out int adminId))
            {
                return adminId;
            }

            return 0;
        }

        private static bool IsLockedProperty(Property? property)
        {
            return property != null &&
                   (property.Status == "Sold" || property.Status == "Rented");
        }

        private static string GetLockedPropertyMessage(Property property)
        {
            return property.Status == "Sold"
                ? "Tin đăng này đã bán nên không được phép chỉnh sửa nội dung."
                : "Tin đăng này đã cho thuê nên không được phép chỉnh sửa nội dung.";
        }

        private static string StatusText(string? status)
        {
            return status switch
            {
                "Approved" => "Đang hiển thị",
                "Pending" => "Chờ duyệt",
                "Rejected" => "Bị từ chối",
                "Sold" => "Đã bán",
                "Rented" => "Đã cho thuê",
                "Deleted" => "Đã xóa",
                null or "" => "Chưa xác định",
                _ => status
            };
        }

        private static string MoneyText(decimal? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return "Thỏa thuận";
            }

            return value.Value.ToString("N0", new CultureInfo("vi-VN")) + " đ";
        }

        private static string DateTimeText(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("dd/MM/yyyy HH:mm:ss")
                : "";
        }
    }
}