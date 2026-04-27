using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class MemberProjectController : Controller
    {
        private readonly ApplicationDbContext _context;

        private static readonly HashSet<string> AllowedLeadStatuses = new(StringComparer.OrdinalIgnoreCase)
        {
            "New",
            "Contacted",
            "Resolved"
        };

        public MemberProjectController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private async Task LoadDashboardCountsAsync(int userId)
        {
            var myProjectsQuery = _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted);

            var myLeadsQuery = _context.ProjectLeads
                .AsNoTracking()
                .Where(l => l.HandledByUserID == userId);

            var myAppointmentsQuery = _context.Appointments
                .AsNoTracking()
                .Where(a => a.SellerID == userId || a.BuyerID == userId);

            ViewBag.TotalProjects = await myProjectsQuery.CountAsync();
            ViewBag.TotalLeads = await myLeadsQuery.CountAsync();
            ViewBag.NewLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "New");
            ViewBag.ContactedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Contacted");
            ViewBag.ResolvedLeads = await myLeadsQuery.CountAsync(l => l.LeadStatus == "Resolved");

            ViewBag.TotalAppointments = await myAppointmentsQuery.CountAsync();
            ViewBag.PendingAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Pending");
            ViewBag.ConfirmedAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = await myAppointmentsQuery.CountAsync(a => a.Status == "Completed");

            var today = DateTime.Now.Date;
            ViewBag.TodayAppointments = await myAppointmentsQuery.CountAsync(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            int? projectId,
            string? status,
            string? daterange,
            string? keyword,
            int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            const int pageSize = 12;

            await LoadDashboardCountsAsync(userId);

            var myProjectsQuery = _context.Projects
                .AsNoTracking()
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted);

            var myProjectsList = await myProjectsQuery
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            ViewBag.ProjectList = new SelectList(myProjectsList, "ProjectID", "ProjectName", projectId);

            var query = _context.ProjectLeads
                .AsNoTracking()
                .Include(l => l.Project)
                .Where(l => l.HandledByUserID == userId)
                .AsQueryable();

            if (projectId.HasValue && projectId.Value > 0)
            {
                query = query.Where(l => l.ProjectID == projectId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status) && AllowedLeadStatuses.Contains(status))
            {
                query = query.Where(l => l.LeadStatus == status);
            }

            if (!string.IsNullOrWhiteSpace(daterange))
            {
                var today = DateTime.Now.Date;
                switch (daterange.Trim().ToLowerInvariant())
                {
                    case "today":
                        query = query.Where(l => l.CreatedAt >= today && l.CreatedAt < today.AddDays(1));
                        break;
                    case "week":
                        query = query.Where(l => l.CreatedAt >= today.AddDays(-7));
                        break;
                    case "month":
                        query = query.Where(l => l.CreatedAt >= today.AddMonths(-1));
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                query = query.Where(l =>
                    (l.Name != null && EF.Functions.Like(l.Name, $"%{keyword}%")) ||
                    (l.Phone != null && EF.Functions.Like(l.Phone, $"%{keyword}%")) ||
                    (l.Message != null && EF.Functions.Like(l.Message, $"%{keyword}%")) ||
                    (l.Note != null && EF.Functions.Like(l.Note, $"%{keyword}%")) ||
                    (l.Project != null && l.Project.ProjectName != null && EF.Functions.Like(l.Project.ProjectName, $"%{keyword}%"))
                );
            }

            int totalItems = await query.CountAsync();
            int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);
            if (totalPages < 1) totalPages = 1;
            if (page < 1) page = 1;
            if (page > totalPages) page = totalPages;

            var leads = await query
                .OrderByDescending(l => l.CreatedAt)
                .ThenByDescending(l => l.LeadID)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.CurrentProjectId = projectId;
            ViewBag.CurrentStatus = status;
            ViewBag.CurrentDateRange = daterange;
            ViewBag.CurrentKeyword = keyword;

            return View(leads);
        }

        [HttpGet]
        public async Task<IActionResult> EditLead(int id, string? returnUrl = null)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            var lead = await _context.ProjectLeads
                .Include(l => l.Project)
                    .ThenInclude(p => p.Ward)
                        .ThenInclude(w => w.Area)
                .FirstOrDefaultAsync(l => l.LeadID == id);

            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng.";
                return RedirectToAction(nameof(Index));
            }

            if (lead.HandledByUserID != userId)
            {
                TempData["Error"] = "Bạn không có quyền xử lý hồ sơ khách hàng này.";
                return RedirectToAction(nameof(Index));
            }

            ViewBag.ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? Url.Action(nameof(Index)) : returnUrl;

            return View(lead);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLeadStatus(int id, string status, string? note, string? returnUrl)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            if (!AllowedLeadStatuses.Contains(status ?? string.Empty))
            {
                TempData["Error"] = "Trạng thái không hợp lệ.";
                return SafeRedirect(returnUrl);
            }

            var lead = await _context.ProjectLeads.FirstOrDefaultAsync(l => l.LeadID == id);
            if (lead == null)
            {
                TempData["Error"] = "Không tìm thấy hồ sơ khách hàng.";
                return SafeRedirect(returnUrl);
            }

            if (lead.HandledByUserID != userId)
            {
                TempData["Error"] = "Lỗi bảo mật: bạn không có quyền chỉnh sửa hồ sơ này.";
                return SafeRedirect(returnUrl);
            }

            lead.LeadStatus = status.Trim();
            lead.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            _context.ProjectLeads.Update(lead);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã cập nhật tiến độ cho khách hàng: {lead.Name ?? "Không tên"}";
            return SafeRedirect(returnUrl);
        }

        [HttpGet]
        public async Task<IActionResult> MyProjects()
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            await LoadDashboardCountsAsync(userId);

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var leadCounts = await _context.ProjectLeads
                .AsNoTracking()
                .Where(l => l.HandledByUserID == userId)
                .GroupBy(l => l.ProjectID)
                .Select(g => new { ProjectID = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProjectID, x => x.Count);

            ViewBag.LeadCounts = leadCounts;

            return View(projects);
        }

        private IActionResult SafeRedirect(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index));
        }
    }
}