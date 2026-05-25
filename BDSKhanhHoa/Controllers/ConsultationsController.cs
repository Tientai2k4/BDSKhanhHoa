using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using System;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ConsultationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;

        private const string StatusNew = "New";
        private const string StatusContacted = "Contacted";
        private const string StatusClosed = "Closed";
        private const string StatusSpam = "Spam";
        private const string StatusCancelled = "Cancelled";

        public ConsultationsController(ApplicationDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private static string CleanText(string? value, int maxLength = 500)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string cleaned = value.Trim();

            if (cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength);
            }

            return cleaned;
        }

        private static string CleanSystemTokens(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string cleaned = value
                .Replace("[REMIND_SELLER]", "", StringComparison.OrdinalIgnoreCase)
                .Replace("REMIND_SELLER", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            /*
                Dữ liệu cũ từng lưu dạng:
                17:32 21/05/2026 - Tên quản trị viên đã gửi thông báo nhắc người phụ trách: Tên người phụ trách.

                Không hiển thị tên quản trị viên/nhân viên nội bộ ra màn hình CRM.
                Người bán chỉ cần hiểu đây là thông báo nhắc chăm sóc khách hàng.
            */
            cleaned = Regex.Replace(
                cleaned,
                @"(?im)^\s*(\d{1,2}:\d{2}\s+\d{1,2}/\d{1,2}/\d{4})\s*-\s*.*?đã gửi thông báo nhắc người phụ trách\s*:\s*.*?\.?\s*$",
                "$1 - Quản trị viên/nhân viên quản lý đã gửi thông báo: Vui lòng liên hệ và chăm sóc khách hàng.");

            cleaned = Regex.Replace(
                cleaned,
                @"(?im)^\s*.*?đã gửi thông báo nhắc người phụ trách\s*:\s*.*?\.?\s*$",
                "Quản trị viên/nhân viên quản lý đã gửi thông báo: Vui lòng liên hệ và chăm sóc khách hàng.");

            cleaned = cleaned.Replace("nhắc người phụ trách", "nhắc chăm sóc khách hàng", StringComparison.OrdinalIgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();

            return cleaned;
        }

        private static string GetVietnameseStatusText(string? status)
        {
            return status switch
            {
                StatusContacted => "Đã gọi / tiếp nhận",
                StatusClosed => "Hoàn tất tư vấn",
                StatusSpam => "Spam / không phù hợp",
                StatusCancelled => "Khách hủy",
                _ => "Mới gửi"
            };
        }

        private static string BuildSellerNoteHistory(string? oldNote, string newNote, string newStatus)
        {
            string oldClean = CleanSystemTokens(oldNote);
            string newClean = CleanSystemTokens(newNote);

            if (string.IsNullOrWhiteSpace(newClean))
            {
                return oldClean;
            }

            string entry = $"{DateTime.Now:HH:mm dd/MM/yyyy} - {GetVietnameseStatusText(newStatus)}: {newClean}";

            if (string.IsNullOrWhiteSpace(oldClean))
            {
                return entry;
            }

            return oldClean + Environment.NewLine + entry;
        }

        private static string CleanPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return string.Empty;
            }

            string cleaned = Regex.Replace(phone.Trim(), @"[^\d\+]", "");

            if (cleaned.Length > 20)
            {
                cleaned = cleaned.Substring(0, 20);
            }

            return cleaned;
        }

        private static bool IsValidPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return false;
            }

            return Regex.IsMatch(phone, @"^(\+84|0)[0-9]{8,10}$");
        }

        private static bool IsValidEmailOrEmpty(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return true;
            }

            return Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        }

        private static bool IsSellerAllowedStatus(string? status)
        {
            return status == StatusNew
                || status == StatusContacted
                || status == StatusClosed
                || status == StatusSpam;
        }

        private static bool CanChangeLeadStatus(string currentStatus, string newStatus, out string message)
        {
            message = string.Empty;

            currentStatus = string.IsNullOrWhiteSpace(currentStatus) ? StatusNew : currentStatus;
            newStatus = string.IsNullOrWhiteSpace(newStatus) ? StatusNew : newStatus;

            if (currentStatus == StatusCancelled)
            {
                message = "Khách đã hủy yêu cầu. Lead này đã khóa xử lý, bạn chỉ có thể xem lại.";
                return false;
            }

            if (currentStatus == StatusClosed)
            {
                message = "Lead đã hoàn tất tư vấn nên không được lùi hoặc chuyển sang trạng thái khác.";
                return false;
            }

            if (currentStatus == StatusSpam)
            {
                message = "Lead đã được đánh dấu Spam/Rác nên không được lùi hoặc chuyển sang trạng thái khác.";
                return false;
            }

            if (currentStatus == StatusNew)
            {
                if (newStatus == StatusNew || newStatus == StatusContacted || newStatus == StatusSpam)
                {
                    return true;
                }

                message = "Lead mới chỉ được giữ trạng thái Mới, chuyển sang Đã gọi hoặc đánh dấu Spam.";
                return false;
            }

            if (currentStatus == StatusContacted)
            {
                if (newStatus == StatusContacted || newStatus == StatusClosed || newStatus == StatusSpam)
                {
                    return true;
                }

                message = "Lead đã gọi không được lùi về Mới. Chỉ được giữ Đã gọi, chuyển sang Hoàn tất tư vấn hoặc Spam.";
                return false;
            }

            message = "Trạng thái hiện tại không hợp lệ.";
            return false;
        }
        private static bool IsSellerNoteRequired(string currentStatus, string newStatus)
        {
            if (currentStatus == newStatus)
            {
                return false;
            }

            return newStatus == StatusContacted
                || newStatus == StatusClosed
                || newStatus == StatusSpam;
        }

        private static string GetRequiredNoteMessage(string newStatus)
        {
            return newStatus switch
            {
                StatusContacted => "Vui lòng nhập ghi chú ngắn sau khi đã gọi hoặc tiếp nhận khách.",
                StatusClosed => "Vui lòng nhập ghi chú kết quả trước khi hoàn tất tư vấn.",
                StatusSpam => "Vui lòng nhập lý do khi đánh dấu lead là Spam/Rác.",
                _ => "Vui lòng nhập ghi chú chăm sóc."
            };
        }

        private async Task<bool> UserCanManageConsultationAsync(Consultation consultation, int currentUserId)
        {
            bool isPropertyOwner = consultation.PropertyID.HasValue
                && await _context.Properties.AnyAsync(p =>
                    p.PropertyID == consultation.PropertyID.Value
                    && p.UserID == currentUserId);

            bool isProjectOwner = consultation.ProjectID.HasValue
                && await _context.Projects.AnyAsync(p =>
                    p.ProjectID == consultation.ProjectID.Value
                    && p.OwnerUserID == currentUserId);

            bool isAssigned = consultation.AssignedToUserID.HasValue
                && consultation.AssignedToUserID.Value == currentUserId;

            return isPropertyOwner || isProjectOwner || isAssigned;
        }

        private async Task<int?> GetSellerIdAsync(int? propertyId, int? projectId)
        {
            if (propertyId.HasValue)
            {
                return await _context.Properties
                    .Where(p => p.PropertyID == propertyId.Value)
                    .Select(p => (int?)p.UserID)
                    .FirstOrDefaultAsync();
            }

            if (projectId.HasValue)
            {
                return await _context.Projects
                    .Where(p => p.ProjectID == projectId.Value)
                    .Select(p => (int?)p.OwnerUserID)
                    .FirstOrDefaultAsync();
            }

            return null;
        }

        private async Task<(bool success, string sourceName, int? sellerId, string leadType, string message)> ResolveSourceAsync(int? propertyId, int? projectId)
        {
            if (!propertyId.HasValue && !projectId.HasValue)
            {
                return (false, string.Empty, null, string.Empty, "Thiếu thông tin bất động sản hoặc dự án cần tư vấn.");
            }

            if (propertyId.HasValue && projectId.HasValue)
            {
                return (false, string.Empty, null, string.Empty, "Chỉ được gửi tư vấn cho một bất động sản hoặc một dự án.");
            }

            if (propertyId.HasValue)
            {
                var property = await _context.Properties
                    .AsNoTracking()
                    .Where(p => p.PropertyID == propertyId.Value)
                    .Select(p => new
                    {
                        p.PropertyID,
                        p.Title,
                        p.UserID
                    })
                    .FirstOrDefaultAsync();

                if (property == null)
                {
                    return (false, string.Empty, null, string.Empty, "Tin bất động sản không tồn tại hoặc đã bị gỡ.");
                }

                return (true, property.Title ?? "Bất động sản", property.UserID, "Property", string.Empty);
            }

            if (projectId.HasValue)
            {
                var project = await _context.Projects
                    .AsNoTracking()
                    .Where(p => p.ProjectID == projectId.Value)
                    .Select(p => new
                    {
                        p.ProjectID,
                        p.ProjectName,
                        p.OwnerUserID
                    })
                    .FirstOrDefaultAsync();

                if (project == null)
                {
                    return (false, string.Empty, null, string.Empty, "Dự án không tồn tại hoặc đã bị gỡ.");
                }

                return (true, project.ProjectName ?? "Dự án bất động sản", project.OwnerUserID, "Project", string.Empty);
            }

            return (false, string.Empty, null, string.Empty, "Không xác định được nguồn tư vấn.");
        }

        // ==========================================================
        // 1. NGƯỜI BÁN: Quản lý lead CRM
        // GET: /Consultations/Index
        // ==========================================================
        public async Task<IActionResult> Index(string? searchString, string? statusFilter, int page = 1)
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return RedirectToAction("Login", "Account");
            }

            int pageSize = 12;

            var baseQuery = _context.Consultations
                .Include(c => c.Property)
                .Include(c => c.Project)
                .Where(c =>
                    (c.Property != null && c.Property.UserID == currentUserId)
                    || (c.Project != null && c.Project.OwnerUserID == currentUserId)
                    || c.AssignedToUserID == currentUserId)
                .AsNoTracking();

            ViewBag.TotalLeads = await baseQuery.CountAsync();
            ViewBag.NewLeads = await baseQuery.CountAsync(c => c.Status == StatusNew);
            ViewBag.ContactedLeads = await baseQuery.CountAsync(c => c.Status == StatusContacted);
            ViewBag.ClosedLeads = await baseQuery.CountAsync(c => c.Status == StatusClosed);
            ViewBag.SpamLeads = await baseQuery.CountAsync(c => c.Status == StatusSpam);
            ViewBag.CancelledLeads = await baseQuery.CountAsync(c => c.Status == StatusCancelled);

            var query = baseQuery.AsQueryable();

            if (!string.IsNullOrWhiteSpace(statusFilter) && statusFilter != "All")
            {
                query = query.Where(c => c.Status == statusFilter);
            }

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                string keyword = searchString.Trim().ToLower();

                query = query.Where(c =>
                    (c.FullName != null && c.FullName.ToLower().Contains(keyword))
                    || (c.Phone != null && c.Phone.Contains(keyword))
                    || (c.Email != null && c.Email.ToLower().Contains(keyword))
                    || (c.Note != null && c.Note.ToLower().Contains(keyword))
                    || (c.SellerNote != null && c.SellerNote.ToLower().Contains(keyword))
                    || (c.Property != null && c.Property.Title.ToLower().Contains(keyword))
                    || (c.Project != null && c.Project.ProjectName.ToLower().Contains(keyword)));
            }

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var leads = await query
                .OrderByDescending(c => c.Status == StatusNew)
                .ThenByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.SearchString = searchString ?? string.Empty;
            ViewBag.StatusFilter = string.IsNullOrWhiteSpace(statusFilter) ? "All" : statusFilter;

            return View(leads);
        }

        // ==========================================================
        // 2. NGƯỜI MUA: Lịch sử yêu cầu đã gửi
        // GET: /Consultations/MyRequests
        // ==========================================================
        public async Task<IActionResult> MyRequests()
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return RedirectToAction("Login", "Account");
            }

            var myRequests = await _context.Consultations
                .Include(c => c.Property)
                .Include(c => c.Project)
                .Where(c => c.SenderID == currentUserId)
                .OrderByDescending(c => c.CreatedAt)
                .AsNoTracking()
                .ToListAsync();

            return View(myRequests);
        }

        // ==========================================================
        // 3. KHÁCH / NGƯỜI MUA: Gửi yêu cầu tư vấn
        // POST: /Consultations/Create
        // ==========================================================
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            string fullName,
            string phone,
            string? email,
            string? note,
            int? propertyId,
            int? projectId)
        {
            try
            {
                string cleanName = CleanText(fullName, 255);
                string cleanPhone = CleanPhone(phone);
                string? cleanEmail = string.IsNullOrWhiteSpace(email) ? null : CleanText(email, 255);
                string? cleanNote = string.IsNullOrWhiteSpace(note) ? null : CleanText(note, 2000);

                if (string.IsNullOrWhiteSpace(cleanName))
                {
                    return Json(new { success = false, message = "Vui lòng nhập họ tên của bạn." });
                }

                if (!IsValidPhone(cleanPhone))
                {
                    return Json(new { success = false, message = "Số điện thoại không hợp lệ. Vui lòng nhập đúng số điện thoại Việt Nam." });
                }

                if (!IsValidEmailOrEmpty(cleanEmail))
                {
                    return Json(new { success = false, message = "Email không hợp lệ." });
                }

                var source = await ResolveSourceAsync(propertyId, projectId);

                if (!source.success)
                {
                    return Json(new { success = false, message = source.message });
                }

                int? senderId = null;

                if (User.Identity != null && User.Identity.IsAuthenticated && TryGetCurrentUserId(out int uid))
                {
                    senderId = uid;
                }

                if (senderId.HasValue && source.sellerId.HasValue && senderId.Value == source.sellerId.Value)
                {
                    return Json(new { success = false, message = "Bạn không thể tự gửi yêu cầu tư vấn cho tin/dự án của chính mình." });
                }

                var consultation = new Consultation
                {
                    FullName = cleanName,
                    Phone = cleanPhone,
                    Email = cleanEmail,
                    Note = cleanNote,
                    PropertyID = propertyId,
                    ProjectID = projectId,
                    SenderID = senderId,
                    LeadType = source.leadType,
                    Status = StatusNew,
                    CreatedAt = DateTime.Now
                };

                _context.Consultations.Add(consultation);

                if (source.sellerId.HasValue && source.sellerId.Value > 0)
                {
                    _context.Notifications.Add(new Notification
                    {
                        UserID = source.sellerId.Value,
                        Title = "Có khách hàng cần tư vấn",
                        Content = $"Khách hàng {cleanName} ({cleanPhone}) vừa gửi yêu cầu tư vấn cho: {source.sourceName}.",
                        ActionUrl = "/Consultations/Index",
                        ActionText = "Xem lead",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });
                }

                await _context.SaveChangesAsync();

                if (source.sellerId.HasValue && source.sellerId.Value > 0)
                {
                    var seller = await _context.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.UserID == source.sellerId.Value);

                    if (seller != null && !string.IsNullOrWhiteSpace(seller.Email))
                    {
                        try
                        {
                            string safeName = WebUtility.HtmlEncode(cleanName);
                            string safePhone = WebUtility.HtmlEncode(cleanPhone);
                            string safeSource = WebUtility.HtmlEncode(source.sourceName);
                            string safeNote = WebUtility.HtmlEncode(cleanNote ?? "Khách không để lại lời nhắn.");

                            string body =
                                $@"<h3>Bạn có khách hàng mới trên BDS Khánh Hòa</h3>
                                   <p><strong>Khách hàng:</strong> {safeName}</p>
                                   <p><strong>Số điện thoại:</strong> {safePhone}</p>
                                   <p><strong>Nguồn quan tâm:</strong> {safeSource}</p>
                                   <p><strong>Lời nhắn:</strong> {safeNote}</p>
                                   <p>Vui lòng đăng nhập hệ thống để chăm sóc và cập nhật trạng thái lead.</p>";

                            await _emailService.SendEmailAsync(
                                seller.Email,
                                "[BDS Khánh Hòa] Yêu cầu tư vấn mới",
                                body);
                        }
                        catch
                        {
                            // Không chặn việc tạo lead nếu gửi email thất bại.
                        }
                    }
                }

                return Json(new
                {
                    success = true,
                    message = "Đã gửi yêu cầu thành công. Người bán/chuyên viên sẽ liên hệ với bạn sớm nhất."
                });
            }
            catch
            {
                return Json(new
                {
                    success = false,
                    message = "Hệ thống đang bận. Vui lòng thử lại sau."
                });
            }
        }

        // ==========================================================
        // 4. NGƯỜI MUA: Hủy yêu cầu khi lead còn New
        // POST: /Consultations/CancelRequest
        // ==========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelRequest(int id)
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để tiếp tục." });
            }

            var consultation = await _context.Consultations
                .FirstOrDefaultAsync(c => c.ConsultID == id && c.SenderID == currentUserId);

            if (consultation == null)
            {
                return Json(new { success = false, message = "Yêu cầu không tồn tại hoặc bạn không có quyền thao tác." });
            }

            if (consultation.Status != StatusNew)
            {
                return Json(new
                {
                    success = false,
                    message = "Yêu cầu đã được người bán tiếp nhận nên bạn không thể tự hủy nữa."
                });
            }

            consultation.Status = StatusCancelled;
            consultation.UpdatedAt = DateTime.Now;

            int? sellerId = await GetSellerIdAsync(consultation.PropertyID, consultation.ProjectID);

            if (sellerId.HasValue && sellerId.Value > 0)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = sellerId.Value,
                    Title = "Khách hàng đã hủy yêu cầu tư vấn",
                    Content = $"Khách hàng {consultation.FullName} đã rút lại yêu cầu tư vấn.",
                    ActionUrl = "/Consultations/Index?statusFilter=Cancelled",
                    ActionText = "Xem yêu cầu đã hủy",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Bạn đã hủy yêu cầu tư vấn thành công."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerUpdateStatus(int id, string newStatus, string? sellerNote)
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để tiếp tục." });
            }

            newStatus = CleanText(newStatus, 50);

            if (!IsSellerAllowedStatus(newStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Trạng thái không hợp lệ. Người bán không được tự đặt trạng thái khách đã hủy."
                });
            }

            var consultation = await _context.Consultations.FirstOrDefaultAsync(c => c.ConsultID == id);

            if (consultation == null)
            {
                return Json(new { success = false, message = "Không tìm thấy yêu cầu tư vấn." });
            }

            bool canManage = await UserCanManageConsultationAsync(consultation, currentUserId);

            if (!canManage)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác trên lead này." });
            }

            string currentStatus = string.IsNullOrWhiteSpace(consultation.Status)
                ? StatusNew
                : consultation.Status;

            if (!CanChangeLeadStatus(currentStatus, newStatus, out string blockMessage))
            {
                return Json(new
                {
                    success = false,
                    message = blockMessage
                });
            }

            string cleanSellerNote = CleanSystemTokens(CleanText(sellerNote, 3000));
            bool statusChanged = currentStatus != newStatus;

            if (!statusChanged && currentStatus == StatusNew)
            {
                return Json(new
                {
                    success = false,
                    message = "Lead mới chưa được gọi hoặc tiếp nhận nên chưa cho phép lưu thêm ghi chú. Vui lòng chọn “Đã gọi / tiếp nhận” sau khi đã liên hệ khách."
                });
            }

            if (IsSellerNoteRequired(currentStatus, newStatus) && string.IsNullOrWhiteSpace(cleanSellerNote))
            {
                return Json(new
                {
                    success = false,
                    message = GetRequiredNoteMessage(newStatus)
                });
            }

            if (!statusChanged && string.IsNullOrWhiteSpace(cleanSellerNote))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng nhập ghi chú nếu bạn muốn cập nhật lead mà không đổi trạng thái."
                });
            }

            consultation.Status = newStatus;

            if (!string.IsNullOrWhiteSpace(cleanSellerNote))
            {
                consultation.SellerNote = BuildSellerNoteHistory(consultation.SellerNote, cleanSellerNote, newStatus);
            }

            consultation.UpdatedAt = DateTime.Now;

            if (statusChanged && consultation.SenderID.HasValue)
            {
                string buyerMessage = newStatus switch
                {
                    StatusContacted => "Người bán/chuyên viên đã tiếp nhận yêu cầu tư vấn của bạn.",
                    StatusClosed => "Yêu cầu tư vấn của bạn đã được hoàn tất.",
                    StatusSpam => "Yêu cầu tư vấn của bạn đã bị từ chối do thông tin không phù hợp hoặc không liên hệ được.",
                    _ => string.Empty
                };

                if (!string.IsNullOrWhiteSpace(buyerMessage))
                {
                    _context.Notifications.Add(new Notification
                    {
                        UserID = consultation.SenderID.Value,
                        Title = "Cập nhật yêu cầu tư vấn",
                        Content = buyerMessage,
                        ActionUrl = "/Consultations/MyRequests",
                        ActionText = "Xem yêu cầu",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });
                }
            }

            await _context.SaveChangesAsync();

            string successMessage = !statusChanged
                ? "Đã lưu thêm ghi chú chăm sóc. Trạng thái lead được giữ nguyên."
                : newStatus switch
                {
                    StatusContacted => "Đã cập nhật: Lead đã được tiếp nhận/gọi điện.",
                    StatusClosed => "Đã cập nhật: Lead đã hoàn tất tư vấn.",
                    StatusSpam => "Đã cập nhật: Lead đã được đánh dấu Spam/Rác.",
                    _ => "Đã cập nhật trạng thái chăm sóc khách hàng."
                };

            return Json(new
            {
                success = true,
                message = successMessage,
                processedAt = consultation.UpdatedAt.Value.ToString("HH:mm - dd/MM/yyyy"),
                status = consultation.Status
            });
        }
        // ==========================================================
        // 6. NGƯỜI BÁN: Xóa lead rác/đã hủy
        // POST: /Consultations/Delete
        // ==========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập để tiếp tục." });
            }

            var consultation = await _context.Consultations.FirstOrDefaultAsync(c => c.ConsultID == id);

            if (consultation == null)
            {
                return Json(new { success = false, message = "Dữ liệu không tồn tại." });
            }

            bool canManage = await UserCanManageConsultationAsync(consultation, currentUserId);

            if (!canManage)
            {
                return Json(new { success = false, message = "Bạn không có quyền xóa lead này." });
            }

            if (consultation.Status != StatusSpam && consultation.Status != StatusCancelled)
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ nên xóa lead rác hoặc lead khách đã hủy. Lead đang chăm sóc/chốt nên giữ lại để lưu lịch sử."
                });
            }

            _context.Consultations.Remove(consultation);
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã xóa lead khỏi danh sách."
            });
        }

        // ==========================================================
        // 7. AJAX: Lấy chi tiết lead cho modal
        // GET: /Consultations/GetDetails?id=1
        // ==========================================================
        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            if (!TryGetCurrentUserId(out int currentUserId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            var c = await _context.Consultations
                .Include(x => x.Property)
                .Include(x => x.Project)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ConsultID == id);

            if (c == null)
            {
                return Json(new { success = false, message = "Không tìm thấy dữ liệu." });
            }

            bool canManage = await UserCanManageConsultationAsync(c, currentUserId);

            if (!canManage)
            {
                return Json(new { success = false, message = "Bạn không có quyền xem lead này." });
            }

            string status = string.IsNullOrWhiteSpace(c.Status) ? StatusNew : c.Status;

            string sourceTitle = c.Property?.Title ?? c.Project?.ProjectName ?? "Nguồn không xác định";
            string sourceType = c.Property != null ? "Nhà đất" : c.Project != null ? "Dự án" : "Không xác định";

            string sourceUrl = "#";

            if (c.PropertyID.HasValue)
            {
                sourceUrl = "/Property/Details/" + c.PropertyID.Value;
            }
            else if (c.ProjectID.HasValue)
            {
                sourceUrl = "/Project/Details/" + c.ProjectID.Value;
            }

            string nextAction = status switch
            {
                StatusNew => "Cần gọi khách hoặc tiếp nhận lead.",
                StatusContacted => "Đang theo dõi. Có thể lưu thêm ghi chú, hoàn tất tư vấn hoặc đánh dấu Spam nếu không phù hợp.",
                StatusClosed => "Lead đã hoàn tất tư vấn và bị khóa xử lý.",
                StatusSpam => "Lead đã đánh dấu Spam/Rác và bị khóa xử lý.",
                StatusCancelled => "Khách đã tự hủy yêu cầu, chỉ được xem lại.",
                _ => "Cần kiểm tra trạng thái lead."
            };

            return Json(new
            {
                success = true,
                data = new
                {
                    id = c.ConsultID,
                    fullName = string.IsNullOrWhiteSpace(c.FullName) ? "Khách hàng" : c.FullName,
                    phone = c.Phone ?? "",
                    email = string.IsNullOrWhiteSpace(c.Email) ? "Không có" : c.Email,
                    note = string.IsNullOrWhiteSpace(c.Note) ? "Không có lời nhắn" : c.Note,
                    sellerNote = CleanSystemTokens(c.SellerNote),
                    sourceTitle = sourceTitle,
                    sourceType = sourceType,
                    sourceUrl = sourceUrl,
                    createdAt = c.CreatedAt.ToString("HH:mm - dd/MM/yyyy"),
                    updatedAt = c.UpdatedAt.HasValue ? c.UpdatedAt.Value.ToString("HH:mm - dd/MM/yyyy") : "Chưa xử lý",
                    status = status,
                    nextAction = nextAction
                }
            });
        }
    }
}