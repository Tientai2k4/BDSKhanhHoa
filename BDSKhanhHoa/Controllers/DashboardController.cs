using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize] // Bắt buộc đăng nhập
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            // Lấy ID của User đang đăng nhập
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            // 1. Thống kê tổng quan Bất động sản
            var myProperties = await _context.Properties
                .Where(p => p.UserID == userId && p.IsDeleted == false)
                .ToListAsync();

            int totalProperties = myProperties.Count;
            int activeProperties = myProperties.Count(p => p.Status == "Approved" || p.Status == "Active");
            int pendingProperties = myProperties.Count(p => p.Status == "Pending");
            int totalViews = myProperties.Sum(p => p.Views ?? 0);

            // 2. Thống kê tương tác (Lịch hẹn & Yêu cầu tư vấn)
            int pendingAppointments = await _context.Appointments
                .CountAsync(a => a.SellerID == userId && a.Status == "Pending");

            int newConsultations = await _context.Consultations
                .Include(c => c.Property)
                .CountAsync(c => c.Property != null && c.Property.UserID == userId && c.Status == "New");

            // 3. Lấy danh sách tin đăng mới nhất của user (Top 5)
            var recentProperties = myProperties
                .OrderByDescending(p => p.CreatedAt)
                .Take(5)
                .ToList();

            // 4. Lấy thông báo mới nhất
            var recentNotifications = await _context.Notifications
                .Where(n => n.UserID == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(5)
                .ToListAsync();

            // Đóng gói dữ liệu gửi ra View bằng ViewBag (Hoặc ViewModel)
            ViewBag.TotalProperties = totalProperties;
            ViewBag.ActiveProperties = activeProperties;
            ViewBag.PendingProperties = pendingProperties;
            ViewBag.TotalViews = totalViews;
            ViewBag.PendingAppointments = pendingAppointments;
            ViewBag.NewConsultations = newConsultations;
            ViewBag.RecentProperties = recentProperties;
            ViewBag.RecentNotifications = recentNotifications;

            ViewData["Title"] = "Tổng quan hoạt động";

            return View();
        }
    }
}