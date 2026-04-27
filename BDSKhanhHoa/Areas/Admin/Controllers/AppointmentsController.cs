using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]
    [Route("Admin/[controller]/[action]")]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? status = "All", string? keyword = null, string? source = "All")
        {
            var query = _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property)
                    .ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(a => a.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(source) && !source.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Equals("Property", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.PropertyID != null);
                else if (source.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.ProjectID != null);
                else if (source.Equals("Lead", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(a => a.LeadID != null);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();
                query = query.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.CustomerEmail != null && EF.Functions.Like(a.CustomerEmail, $"%{keyword}%")) ||
                    (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                    (a.AssignedStaffPhone != null && EF.Functions.Like(a.AssignedStaffPhone, $"%{keyword}%")) ||
                    (a.Note != null && EF.Functions.Like(a.Note, $"%{keyword}%")) ||
                    (a.ResultNote != null && EF.Functions.Like(a.ResultNote, $"%{keyword}%")) ||
                    (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%")) ||
                    (a.Project != null && a.Project.ProjectName != null && EF.Functions.Like(a.Project.ProjectName, $"%{keyword}%")) ||
                    (a.Lead != null && a.Lead.Name != null && EF.Functions.Like(a.Lead.Name, $"%{keyword}%"))
                );
            }

            var list = await query
                .OrderByDescending(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
                .ToListAsync();

            ViewBag.Status = status;
            ViewBag.Keyword = keyword;
            ViewBag.Source = source;

            ViewBag.TotalAppointments = list.Count;
            ViewBag.PendingAppointments = list.Count(a => a.Status == "Pending");
            ViewBag.ConfirmedAppointments = list.Count(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = list.Count(a => a.Status == "Completed");
            ViewBag.CancelledAppointments = list.Count(a => a.Status == "Cancelled");

            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status, string? resultStatus = null, string? resultNote = null, string? returnUrl = null)
        {
            var item = await _context.Appointments.FirstOrDefaultAsync(a => a.AppointmentID == id);
            if (item == null)
            {
                TempData["Error"] = "Không tìm thấy lịch hẹn.";
                return SafeRedirect(returnUrl);
            }

            item.Status = status?.Trim();
            item.ResultStatus = string.IsNullOrWhiteSpace(resultStatus) ? item.ResultStatus : resultStatus.Trim();
            item.ResultNote = string.IsNullOrWhiteSpace(resultNote) ? item.ResultNote : resultNote.Trim();
            item.UpdatedAt = DateTime.Now;

            if (item.Status == "Completed")
                item.CompletedAt = DateTime.Now;

            _context.Appointments.Update(item);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật lịch hẹn.";
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