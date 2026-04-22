using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var today = DateTime.Now;
            var startOfMonth = new DateTime(today.Year, today.Month, 1);

            // 1. THỐNG KÊ DOANH THU (Dựa trên hệ thống giao dịch thanh toán trực tiếp)
            var successTransactions = await _context.Transactions
                .Where(t => t.Status == "Success" || t.Status == "Completed")
                .ToListAsync();

            decimal totalRevenue = successTransactions.Sum(t => t.Amount);
            decimal monthlyRevenue = successTransactions
                .Where(t => t.CreatedAt >= startOfMonth)
                .Sum(t => t.Amount);

            // 2. THỐNG KÊ BẤT ĐỘNG SẢN & DỰ ÁN
            int totalProperties = await _context.Properties.CountAsync(p => p.IsDeleted == false);
            int pendingProperties = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);

            int totalProjects = await _context.Projects.CountAsync(p => p.IsDeleted == false);
            int pendingProjects = await _context.Projects.CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);

            // 3. THỐNG KÊ NGƯỜI DÙNG & VI PHẠM
            int totalUsers = await _context.Users.CountAsync(u => u.IsDeleted == false);
            int newUsersThisMonth = await _context.Users.CountAsync(u => u.CreatedAt >= startOfMonth && u.IsDeleted == false);

            int pendingReports = await _context.PropertyReports.CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);

            // 4. THỐNG KÊ AI CHATBOT TƯ VẤN
            int totalChatInteractions = await _context.ChatLogs.CountAsync();
            int chatInteractionsToday = await _context.ChatLogs.CountAsync(c => c.CreatedAt >= today.Date);

            // 5. DỮ LIỆU BIỂU ĐỒ DOANH THU 6 THÁNG GẦN NHẤT
            var revenueData = new List<decimal>();
            var monthLabels = new List<string>();
            for (int i = 5; i >= 0; i--)
            {
                var monthDate = today.AddMonths(-i);

                // ĐÃ SỬA LỖI Ở ĐÂY: Dùng trực tiếp t.CreatedAt.Month thay vì t.CreatedAt.Value.Month
                var revenue = await _context.Transactions
                    .Where(t => (t.Status == "Success" || t.Status == "Completed")
                             && t.CreatedAt.Month == monthDate.Month
                             && t.CreatedAt.Year == monthDate.Year)
                    .SumAsync(t => t.Amount);

                revenueData.Add(revenue);
                monthLabels.Add($"Tháng {monthDate.Month}");
            }

            // 6. DANH SÁCH GIAO DỊCH MỚI NHẤT
            var recentTransactions = await _context.Transactions
                .Include(t => t.User)
                .OrderByDescending(t => t.CreatedAt)
                .Take(5)
                .ToListAsync();

            // ĐÓNG GÓI DỮ LIỆU RA VIEW
            ViewBag.TotalRevenue = totalRevenue;
            ViewBag.MonthlyRevenue = monthlyRevenue;

            ViewBag.TotalProperties = totalProperties;
            ViewBag.PendingProperties = pendingProperties;
            ViewBag.TotalProjects = totalProjects;
            ViewBag.PendingProjects = pendingProjects;

            ViewBag.TotalUsers = totalUsers;
            ViewBag.NewUsersThisMonth = newUsersThisMonth;
            ViewBag.PendingReports = pendingReports;

            ViewBag.TotalChatInteractions = totalChatInteractions;
            ViewBag.ChatInteractionsToday = chatInteractionsToday;

            ViewBag.RevenueData = revenueData;
            ViewBag.MonthLabels = monthLabels;
            ViewBag.RecentTransactions = recentTransactions;

            ViewData["Title"] = "Tổng quan Quản trị hệ thống";
            return View();
        }
    }
}