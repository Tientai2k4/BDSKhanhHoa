using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Linq;
using System.Threading.Tasks;
using BDSKhanhHoa.Services;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class ConsultationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;

        public ConsultationsController(ApplicationDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        // ==========================================
        // 1. GET: /Consultations/Index (Dành cho NGƯỜI BÁN - Quản lý CRM)
        // ==========================================
        public async Task<IActionResult> Index(string searchString, string statusFilter, int page = 1)
        {
            if (!TryGetCurrentUserId(out int currentUserId)) return RedirectToAction("Login", "Account");

            int pageSize = 12;
            var query = _context.Consultations
                .Include(c => c.Property)
                .Include(c => c.Project)
                .Where(c => (c.Property != null && c.Property.UserID == currentUserId) ||
                            (c.Project != null && c.Project.OwnerUserID == currentUserId) ||
                            c.AssignedToUserID == currentUserId)
                .AsNoTracking()
                .AsQueryable();

            ViewBag.TotalLeads = await query.CountAsync();
            ViewBag.NewLeads = await query.CountAsync(c => c.Status == "New");
            ViewBag.ContactedLeads = await query.CountAsync(c => c.Status == "Contacted" || c.Status == "Closed");

            if (!string.IsNullOrEmpty(statusFilter) && statusFilter != "All")
                query = query.Where(c => c.Status == statusFilter);

            if (!string.IsNullOrEmpty(searchString))
            {
                var lowerSearch = searchString.ToLower().Trim();
                query = query.Where(c =>
                    (c.FullName != null && c.FullName.ToLower().Contains(lowerSearch)) ||
                    (c.Phone != null && c.Phone.Contains(lowerSearch)) ||
                    (c.Property != null && c.Property.Title.ToLower().Contains(lowerSearch))
                );
            }

            int totalItems = await query.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Clamp(page, 1, totalPages);

            var leads = await query
                .OrderByDescending(c => c.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.SearchString = searchString;
            ViewBag.StatusFilter = statusFilter;

            return View(leads);
        }

        // ==========================================
        // 2. GET: /Consultations/MyRequests (Dành cho NGƯỜI MUA - Xem lịch sử xin tư vấn)
        // ==========================================
        public async Task<IActionResult> MyRequests()
        {
            if (!TryGetCurrentUserId(out int currentUserId)) return RedirectToAction("Login", "Account");

            var myRequests = await _context.Consultations
                .Include(c => c.Property)
                .Include(c => c.Project)
                .Where(c => c.SenderID == currentUserId)
                .OrderByDescending(c => c.CreatedAt)
                .AsNoTracking()
                .ToListAsync();

            return View(myRequests);
        }

        // ==========================================
        // 3. POST: /Consultations/Create (API nhận Form từ Website - Bất kỳ ai cũng gửi được)
        // ==========================================
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string fullName, string phone, string email, string note, int? propertyId, int? projectId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(fullName))
                    return Json(new { success = false, message = "Họ tên và Số điện thoại là bắt buộc!" });

                int? senderId = null;
                if (User.Identity.IsAuthenticated && TryGetCurrentUserId(out int uid)) senderId = uid;

                var consultation = new Consultation
                {
                    FullName = fullName.Trim(),
                    Phone = phone.Trim(),
                    Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    PropertyID = propertyId,
                    ProjectID = projectId,
                    SenderID = senderId,
                    Status = "New",
                    CreatedAt = DateTime.Now
                };

                _context.Consultations.Add(consultation);

                // Xác định SellerID để gửi thông báo
                int? sellerId = null;
                string sourceName = "Bất động sản";

                if (propertyId.HasValue)
                {
                    var prop = await _context.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.PropertyID == propertyId.Value);
                    if (prop != null) { sellerId = prop.UserID; sourceName = prop.Title; }
                }
                else if (projectId.HasValue)
                {
                    var proj = await _context.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectID == projectId.Value);
                    if (proj != null) { sellerId = proj.OwnerUserID; sourceName = proj.ProjectName; }
                }

                // Gửi Notification và Email cho Người Bán
                if (sellerId.HasValue && sellerId.Value > 0)
                {
                    _context.Notifications.Add(new Notification
                    {
                        UserID = sellerId.Value,
                        Title = "Có khách hàng cần tư vấn",
                        Content = $"Khách hàng {consultation.FullName} ({consultation.Phone}) vừa gửi yêu cầu tư vấn cho: {sourceName}.",
                        ActionUrl = "/Consultations/Index",
                        ActionText = "Xem và Gọi ngay",
                        IsRead = false,
                        CreatedAt = DateTime.Now
                    });

                    var seller = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserID == sellerId.Value);
                    if (seller != null && !string.IsNullOrWhiteSpace(seller.Email))
                    {
                        await _emailService.SendEmailAsync(seller.Email, "[BDS Khánh Hòa] Yêu cầu tư vấn mới",
                            $"<h3>Bạn có khách hàng mới!</h3><p>Khách hàng: <strong>{consultation.FullName}</strong></p><p>SĐT: <strong>{consultation.Phone}</strong></p><p>Lời nhắn: {consultation.Note}</p><p>Vui lòng đăng nhập hệ thống CRM để quản lý.</p>");
                    }
                }

                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "Đã gửi yêu cầu thành công. Chuyên viên sẽ liên hệ với bạn sớm nhất!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        // ==========================================
        // 4. POST: /Consultations/CancelRequest (Dành cho NGƯỜI MUA)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelRequest(int id)
        {
            if (!TryGetCurrentUserId(out int currentUserId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var consultation = await _context.Consultations.FirstOrDefaultAsync(c => c.ConsultID == id && c.SenderID == currentUserId);
            if (consultation == null) return Json(new { success = false, message = "Dữ liệu không tồn tại hoặc bạn không có quyền!" });

            if (consultation.Status != "New")
                return Json(new { success = false, message = "Yêu cầu đã được người bán tiếp nhận, không thể tự hủy!" });

            consultation.Status = "Cancelled";
            consultation.UpdatedAt = DateTime.Now;

            // Báo lại cho Seller biết khách đã hủy
            int sellerId = _context.Properties.Where(p => p.PropertyID == consultation.PropertyID).Select(p => p.UserID).FirstOrDefault();
            if (sellerId > 0)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = sellerId,
                    Title = "Khách hàng đã hủy tư vấn",
                    Content = $"Khách hàng {consultation.FullName} đã rút lại yêu cầu tư vấn.",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Bạn đã hủy yêu cầu tư vấn thành công!" });
        }

        // ==========================================
        // 5. POST: /Consultations/SellerUpdateStatus (Dành cho NGƯỜI BÁN)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerUpdateStatus(int id, string newStatus, string? sellerNote)
        {
            if (!TryGetCurrentUserId(out int currentUserId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var consultation = await _context.Consultations.FirstOrDefaultAsync(c => c.ConsultID == id);
            if (consultation == null) return Json(new { success = false, message = "Không tìm thấy dữ liệu!" });

            // Kiểm tra quyền
            bool isPropertyOwner = consultation.PropertyID.HasValue && await _context.Properties.AnyAsync(p => p.PropertyID == consultation.PropertyID && p.UserID == currentUserId);
            bool isProjectOwner = consultation.ProjectID.HasValue && await _context.Projects.AnyAsync(p => p.ProjectID == consultation.ProjectID && p.OwnerUserID == currentUserId);
            bool isAssigned = consultation.AssignedToUserID == currentUserId;

            if (!isPropertyOwner && !isProjectOwner && !isAssigned)
                return Json(new { success = false, message = "Bạn không có quyền thao tác trên Lead này!" });

            consultation.Status = newStatus; // Contacted, Closed, Spam
            if (!string.IsNullOrWhiteSpace(sellerNote)) consultation.SellerNote = sellerNote.Trim();
            consultation.UpdatedAt = DateTime.Now;

            // Nếu người bán cập nhật là "Contacted" hoặc "Closed", báo lại cho Người Mua (nếu là User của hệ thống)
            if (consultation.SenderID.HasValue && (newStatus == "Contacted" || newStatus == "Closed"))
            {
                string msg = newStatus == "Contacted" ? "Người bán đã ghi nhận yêu cầu của bạn và sẽ gọi điện/đã gọi điện tư vấn." : "Yêu cầu tư vấn của bạn đã được chốt và hoàn tất quy trình.";

                _context.Notifications.Add(new Notification
                {
                    UserID = consultation.SenderID.Value,
                    Title = "Cập nhật yêu cầu tư vấn",
                    Content = msg,
                    ActionUrl = "/Consultations/MyRequests",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();
            return Json(new { success = true, message = "Đã cập nhật trạng thái chăm sóc Khách hàng!" });
        }

        // ==========================================
        // 6. POST: /Consultations/Delete (Xóa thư rác - Cho người bán)
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!TryGetCurrentUserId(out int currentUserId)) return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var consultation = await _context.Consultations.FirstOrDefaultAsync(c => c.ConsultID == id);
            if (consultation == null) return Json(new { success = false, message = "Dữ liệu không tồn tại!" });

            bool isOwner = (consultation.PropertyID.HasValue && await _context.Properties.AnyAsync(p => p.PropertyID == consultation.PropertyID && p.UserID == currentUserId)) ||
                           (consultation.ProjectID.HasValue && await _context.Projects.AnyAsync(p => p.ProjectID == consultation.ProjectID && p.OwnerUserID == currentUserId));

            if (!isOwner) return Json(new { success = false, message = "Bạn không có quyền xóa Lead này!" });

            _context.Consultations.Remove(consultation);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Đã xóa yêu cầu tư vấn vĩnh viễn!" });
        }

        // ==========================================
        // 7. GET: /Consultations/GetDetails (AJAX lấy chi tiết Lời nhắn & Modal)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> GetDetails(int id)
        {
            var c = await _context.Consultations
                .Include(x => x.Property)
                .Include(x => x.Project)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ConsultID == id);

            if (c == null) return Json(new { success = false, message = "Không tìm thấy dữ liệu." });

            return Json(new
            {
                success = true,
                data = new
                {
                    id = c.ConsultID,
                    fullName = c.FullName ?? "Khách ẩn danh",
                    phone = c.Phone,
                    email = c.Email ?? "Không có",
                    note = c.Note ?? "Không có lời nhắn",
                    sellerNote = c.SellerNote ?? "",
                    sourceTitle = c.Property?.Title ?? c.Project?.ProjectName ?? "Nguồn không xác định",
                    createdAt = c.CreatedAt.ToString("HH:mm - dd/MM/yyyy"),
                    updatedAt = c.UpdatedAt?.ToString("HH:mm - dd/MM/yyyy") ?? "Chưa cập nhật",
                    status = c.Status
                }
            });
        }
    }
}