using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AnalyticsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AnalyticsController(ApplicationDbContext context)
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
        public async Task<IActionResult> Index()
        {
            if (!TryGetCurrentUserId(out int userId))
                return Challenge();

            var projects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Where(p => p.OwnerUserID == userId && !p.IsDeleted)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var projectIds = projects.Select(p => p.ProjectID).ToList();

            var leads = await _context.ProjectLeads
                .AsNoTracking()
                .Where(l => projectIds.Contains(l.ProjectID) || l.HandledByUserID == userId)
                .ToListAsync();

            var appointments = await _context.Appointments
                .AsNoTracking()
                .Where(a => a.SellerID == userId || a.BuyerID == userId || (a.ProjectID.HasValue && projectIds.Contains(a.ProjectID.Value)))
                .ToListAsync();

            var properties = await _context.Properties
                .AsNoTracking()
                .Where(p => p.UserID == userId && p.IsDeleted != true)
                .ToListAsync();

            ViewBag.TotalProjects = projects.Count;
            ViewBag.TotalLeads = leads.Count;
            ViewBag.NewLeads = leads.Count(l => l.LeadStatus == "New");
            ViewBag.ContactedLeads = leads.Count(l => l.LeadStatus == "Contacted");
            ViewBag.ResolvedLeads = leads.Count(l => l.LeadStatus == "Resolved");
            ViewBag.TotalAppointments = appointments.Count;
            ViewBag.ConfirmedAppointments = appointments.Count(a => a.Status == "Confirmed");
            ViewBag.CompletedAppointments = appointments.Count(a => a.Status == "Completed");
            ViewBag.PendingAppointments = appointments.Count(a => a.Status == "Pending");
            ViewBag.TotalViews = properties.Sum(p => p.Views ?? 0);

            var leadMap = leads
                .GroupBy(x => x.ProjectID)
                .ToDictionary(g => g.Key, g => g.Count());

            var appointmentMap = appointments
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var viewMap = properties
                .Where(x => x.ProjectID.HasValue)
                .GroupBy(x => x.ProjectID!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Views ?? 0));

            ViewBag.LeadMap = leadMap;
            ViewBag.AppointmentMap = appointmentMap;
            ViewBag.ViewMap = viewMap;

            return View(projects);
        }
    }
}