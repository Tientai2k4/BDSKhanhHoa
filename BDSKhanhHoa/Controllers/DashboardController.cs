using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

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
            // 1. Lấy và xác thực ID của User đang đăng nhập
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return RedirectToAction("Login", "Account");
            }

            var currentUser = await _context.Users.FindAsync(userId);
            if (currentUser == null) return NotFound();

            // 2. Thống kê tổng quan Bất động sản của User
            var myProperties = await _context.Properties
                .Where(p => p.UserID == userId && p.IsDeleted == false)
                .ToListAsync();

            int totalProperties = myProperties.Count;
            int activeProperties = myProperties.Count(p => p.Status == "Approved" || p.Status == "Active");
            int pendingProperties = myProperties.Count(p => p.Status == "Pending");

            // --- BỔ SUNG: ĐẾM SỐ LƯỢNG ĐÃ BÁN / ĐÃ CHO THUÊ ---
            int soldProperties = myProperties.Count(p => p.Status == "Sold");
            int rentedProperties = myProperties.Count(p => p.Status == "Rented");

            int totalViews = myProperties.Sum(p => p.Views ?? 0);

            // 3. Thống kê Tương tác (Lịch hẹn & Yêu cầu tư vấn)
            int pendingAppointmentsCount = await _context.Appointments
                .CountAsync(a => a.SellerID == userId && a.Status == "Pending");

            int newConsultations = await _context.Consultations
                .Include(c => c.Property)
                .CountAsync(c => c.Property != null && c.Property.UserID == userId && c.Status == "New");

            int totalFavorites = await _context.Favorites
                .Include(f => f.Property)
                .CountAsync(f => f.Property != null && f.Property.UserID == userId);

            // 4. Lấy danh sách Lịch hẹn sắp tới (Top 3)
            var upcomingAppointments = await _context.Appointments
                .Include(a => a.Buyer)
                .Include(a => a.Property)
                .Where(a => (a.SellerID == userId || a.BuyerID == userId) && a.AppointmentDate >= DateTime.Now)
                .OrderBy(a => a.AppointmentDate)
                .Take(3)
                .ToListAsync();

            // 5. Lấy danh sách Giao dịch gần nhất (Top 3)
            var recentTransactions = await _context.Transactions
                .Where(t => t.UserID == userId)
                .OrderByDescending(t => t.CreatedAt)
                .Take(3)
                .ToListAsync();

            // 6. Lấy danh sách tin đăng mới nhất của user (Top 5)
            var recentProperties = myProperties
                .OrderByDescending(p => p.CreatedAt)
                .Take(5)
                .ToList();

            // 7. Lấy thông báo mới nhất (Top 4)
            var recentNotifications = await _context.Notifications
                .Where(n => n.UserID == userId)
                .OrderByDescending(n => n.CreatedAt)
                .Take(4)
                .ToListAsync();

            // 8. GIẢ LẬP DỮ LIỆU BIỂU ĐỒ 7 NGÀY QUA (Dựa trên tổng Views)
            var chartLabels = new List<string>();
            var chartData = new List<int>();
            int remainingViews = totalViews;
            Random rnd = new Random();

            for (int i = 6; i >= 0; i--)
            {
                chartLabels.Add(DateTime.Now.AddDays(-i).ToString("dd/MM"));
                if (i == 0)
                {
                    chartData.Add(remainingViews); // Ngày cuối cùng lấy nốt số dư
                }
                else
                {
                    int dailyView = remainingViews > 0 ? rnd.Next(0, (remainingViews / (i + 1)) + 5) : 0;
                    chartData.Add(dailyView);
                    remainingViews -= dailyView;
                    if (remainingViews < 0) remainingViews = 0;
                }
            }

            // Đóng gói toàn bộ dữ liệu gửi ra View
            ViewBag.CurrentUser = currentUser;
            ViewBag.TotalProperties = totalProperties;
            ViewBag.ActiveProperties = activeProperties;
            ViewBag.PendingProperties = pendingProperties;

            // --- BỔ SUNG: TRUYỀN DỮ LIỆU BÁN/THUÊ RA VIEW ---
            ViewBag.SoldProperties = soldProperties;
            ViewBag.RentedProperties = rentedProperties;

            ViewBag.TotalViews = totalViews;
            ViewBag.PendingAppointmentsCount = pendingAppointmentsCount;
            ViewBag.NewConsultations = newConsultations;
            ViewBag.TotalFavorites = totalFavorites;

            ViewBag.UpcomingAppointments = upcomingAppointments;
            ViewBag.RecentTransactions = recentTransactions;
            ViewBag.RecentProperties = recentProperties;
            ViewBag.RecentNotifications = recentNotifications;

            ViewBag.ChartLabels = JsonSerializer.Serialize(chartLabels);
            ViewBag.ChartData = JsonSerializer.Serialize(chartData);

            ViewData["Title"] = "Bảng điều khiển cá nhân";

            return View();
        }
    }
}