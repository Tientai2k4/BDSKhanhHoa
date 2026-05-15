using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Staff")] // Đảm bảo chỉ có Staff mới truy cập được Controller này
    public class StaffDashboardController : Controller
    {
        private readonly ApplicationDbContext _context;

        public StaffDashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var today = DateTime.Now;

            // 1. THỐNG KÊ KIỂM DUYỆT (Nhiệm vụ chính của Staff)
            int totalProperties = await _context.Properties.CountAsync(p => p.IsDeleted == false);
            int pendingProperties = await _context.Properties.CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);
            int pendingReports = await _context.PropertyReports.CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);

            // 2. THỐNG KÊ HỖ TRỢ & CSKH
            int pendingConsultations = await _context.Consultations.CountAsync(c => c.Status == "New");
            int pendingContacts = await _context.ContactMessages.CountAsync(c => c.Status == "Pending" || c.Status == "Chưa xử lý");

            // 3. THỐNG KÊ TRUYỀN THÔNG (Blog & Banner)
            int totalBlogs = await _context.Blogs.CountAsync(b => b.IsDeleted == false);
            int activeBanners = await _context.Banners.CountAsync(b => b.IsActive == true);

            // ĐÓNG GÓI DỮ LIỆU RA VIEW (Tuyệt đối không truyền dữ liệu doanh thu, người dùng, giao dịch)
            ViewBag.TotalProperties = totalProperties;
            ViewBag.PendingProperties = pendingProperties;
            ViewBag.PendingReports = pendingReports;

            ViewBag.PendingConsultations = pendingConsultations;
            ViewBag.PendingContacts = pendingContacts;

            ViewBag.TotalBlogs = totalBlogs;
            ViewBag.ActiveBanners = activeBanners;

            ViewData["Title"] = "Tổng quan công việc Nhân viên (Staff)";
            return View();
        }
    }
}