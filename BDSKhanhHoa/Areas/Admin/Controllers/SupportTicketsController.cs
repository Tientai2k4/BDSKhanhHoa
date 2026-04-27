using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]
    [Route("Admin/[controller]/[action]")]
    public class SupportTicketsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public SupportTicketsController(ApplicationDbContext context)
        {
            _context = context;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        [HttpGet]
        public async Task<IActionResult> Index(string tab = "all", string? status = null, string? keyword = null)
        {
            if (!TryGetCurrentUserId(out _))
                return Challenge();

            var projectNames = await _context.Projects
                .AsNoTracking()
                .Select(p => new { p.ProjectID, p.ProjectName })
                .ToDictionaryAsync(x => x.ProjectID, x => x.ProjectName);

            var propertyNames = await _context.Properties
                .AsNoTracking()
                .Select(p => new { p.PropertyID, p.Title })
                .ToDictionaryAsync(x => x.PropertyID, x => x.Title);

            var consultationsQuery = _context.Consultations.AsNoTracking().AsQueryable();
            var contactMessagesQuery = _context.ContactMessages.AsNoTracking().AsQueryable();
            var leadsQuery = _context.ProjectLeads.AsNoTracking().Include(x => x.Project).AsQueryable();
            var reportsQuery = _context.PropertyReports.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                consultationsQuery = consultationsQuery.Where(x =>
                    (x.FullName != null && EF.Functions.Like(x.FullName, $"%{keyword}%")) ||
                    (x.Phone != null && EF.Functions.Like(x.Phone, $"%{keyword}%")) ||
                    (x.Email != null && EF.Functions.Like(x.Email, $"%{keyword}%")) ||
                    (x.Note != null && EF.Functions.Like(x.Note, $"%{keyword}%")));

                contactMessagesQuery = contactMessagesQuery.Where(x =>
                    (x.FullName != null && EF.Functions.Like(x.FullName, $"%{keyword}%")) ||
                    (x.Phone != null && EF.Functions.Like(x.Phone, $"%{keyword}%")) ||
                    (x.Email != null && EF.Functions.Like(x.Email, $"%{keyword}%")) ||
                    (x.Subject != null && EF.Functions.Like(x.Subject, $"%{keyword}%")) ||
                    (x.Message != null && EF.Functions.Like(x.Message, $"%{keyword}%")));

                leadsQuery = leadsQuery.Where(x =>
                    (x.Name != null && EF.Functions.Like(x.Name, $"%{keyword}%")) ||
                    (x.Phone != null && EF.Functions.Like(x.Phone, $"%{keyword}%")) ||
                    (x.Email != null && EF.Functions.Like(x.Email, $"%{keyword}%")) ||
                    (x.Message != null && EF.Functions.Like(x.Message, $"%{keyword}%")) ||
                    (x.Note != null && EF.Functions.Like(x.Note, $"%{keyword}%")) ||
                    (x.Project != null && x.Project.ProjectName != null && EF.Functions.Like(x.Project.ProjectName, $"%{keyword}%")));

                reportsQuery = reportsQuery.Where(x =>
                    (x.Reason != null && EF.Functions.Like(x.Reason, $"%{keyword}%")) ||
                    (x.Description != null && EF.Functions.Like(x.Description, $"%{keyword}%")));
            }

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => x.Status == status);
                contactMessagesQuery = contactMessagesQuery.Where(x => x.Status == status);
                leadsQuery = leadsQuery.Where(x => x.LeadStatus == status);
                reportsQuery = reportsQuery.Where(x => x.Status == status);
            }

            if (tab.Equals("consultations", StringComparison.OrdinalIgnoreCase))
            {
                contactMessagesQuery = contactMessagesQuery.Where(x => false);
                leadsQuery = leadsQuery.Where(x => false);
                reportsQuery = reportsQuery.Where(x => false);
            }
            else if (tab.Equals("contacts", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => false);
                leadsQuery = leadsQuery.Where(x => false);
                reportsQuery = reportsQuery.Where(x => false);
            }
            else if (tab.Equals("leads", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => false);
                contactMessagesQuery = contactMessagesQuery.Where(x => false);
                reportsQuery = reportsQuery.Where(x => false);
            }
            else if (tab.Equals("reports", StringComparison.OrdinalIgnoreCase))
            {
                consultationsQuery = consultationsQuery.Where(x => false);
                contactMessagesQuery = contactMessagesQuery.Where(x => false);
                leadsQuery = leadsQuery.Where(x => false);
            }

            ViewBag.Consultations = await consultationsQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(100)
                .ToListAsync();

            ViewBag.ContactMessages = await contactMessagesQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(100)
                .ToListAsync();

            ViewBag.ProjectLeads = await leadsQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(100)
                .ToListAsync();

            ViewBag.PropertyReports = await reportsQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(100)
                .ToListAsync();

            ViewBag.ProjectNames = projectNames;
            ViewBag.PropertyNames = propertyNames;

            ViewBag.ConsultationCount = await _context.Consultations.CountAsync();
            ViewBag.ContactMessageCount = await _context.ContactMessages.CountAsync();
            ViewBag.ProjectLeadCount = await _context.ProjectLeads.CountAsync();
            ViewBag.ReportCount = await _context.PropertyReports.CountAsync();

            ViewBag.ActiveTab = tab;
            ViewBag.Status = status;
            ViewBag.Keyword = keyword;

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateConsultationStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.Consultations.FirstOrDefaultAsync(x => x.ConsultID == id);
            if (item == null)
            {
                TempData["Error"] = "Không tìm thấy yêu cầu tư vấn.";
                return SafeRedirect(returnUrl);
            }

            item.Status = status?.Trim();
            _context.Consultations.Update(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái yêu cầu tư vấn.";
            return SafeRedirect(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateContactStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.ContactMessages.FirstOrDefaultAsync(x => x.ContactID == id);
            if (item == null)
            {
                TempData["Error"] = "Không tìm thấy tin liên hệ.";
                return SafeRedirect(returnUrl);
            }

            item.Status = status?.Trim();
            _context.ContactMessages.Update(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái tin liên hệ.";
            return SafeRedirect(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLeadStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.ProjectLeads.FirstOrDefaultAsync(x => x.LeadID == id);
            if (item == null)
            {
                TempData["Error"] = "Không tìm thấy lead dự án.";
                return SafeRedirect(returnUrl);
            }

            item.LeadStatus = status?.Trim();
            _context.ProjectLeads.Update(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái lead.";
            return SafeRedirect(returnUrl);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateReportStatus(int id, string status, string? returnUrl = null)
        {
            var item = await _context.PropertyReports.FirstOrDefaultAsync(x => x.ReportID == id);
            if (item == null)
            {
                TempData["Error"] = "Không tìm thấy báo cáo vi phạm.";
                return SafeRedirect(returnUrl);
            }

            item.Status = status?.Trim();
            _context.PropertyReports.Update(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật trạng thái báo cáo.";
            return SafeRedirect(returnUrl);
        }

        private IActionResult SafeRedirect(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction(nameof(Index));
        }
    }
}