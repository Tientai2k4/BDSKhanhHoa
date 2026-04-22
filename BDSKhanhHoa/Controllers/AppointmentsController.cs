using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AppointmentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. GIAO DIỆN QUẢN LÍ LỊCH HẸN (Có Lọc & Tìm kiếm)
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index(string tab = "incoming", string statusFilter = "All")
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId)) return RedirectToAction("Login", "Account");

            var query = _context.Appointments
                .Include(a => a.Property)
                    .ThenInclude(p => p.Ward).ThenInclude(w => w.Area)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Where(a => a.BuyerID == userId || a.SellerID == userId)
                .AsQueryable();

            if (statusFilter != "All")
            {
                query = query.Where(a => a.Status == statusFilter);
            }

            var appointments = await query.OrderByDescending(a => a.CreatedAt).ToListAsync();

            ViewBag.CurrentUserId = userId;
            ViewBag.ActiveTab = tab;
            ViewBag.StatusFilter = statusFilter;

            return View(appointments);
        }

        // ==========================================
        // 2. API TẠO LỊCH HẸN MỚI TỪ MODAL UI
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> Create(int propertyId, DateTime appointmentDate, TimeSpan appointmentTime, string note)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int buyerId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để đặt lịch!" });

            var property = await _context.Properties.FindAsync(propertyId);
            if (property == null || property.IsDeleted == true)
                return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị gỡ." });

            if (property.UserID == buyerId)
                return Json(new { success = false, message = "Bạn không thể tự đặt lịch xem nhà của chính mình!" });

            // Ghép Ngày và Giờ
            DateTime fullAppointmentDate = appointmentDate.Date + appointmentTime;

            if (fullAppointmentDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian hẹn phải ở trong tương lai." });

            // Thuật toán kiểm tra trùng lịch (Khoảng cách an toàn là 2 tiếng cho 1 ca xem nhà)
            var minTime = fullAppointmentDate.AddHours(-2);
            var maxTime = fullAppointmentDate.AddHours(2);

            var isOverlapping = await _context.Appointments.AnyAsync(a =>
                a.PropertyID == propertyId &&
                a.Status != "Cancelled" &&
                a.AppointmentDate > minTime &&
                a.AppointmentDate < maxTime);

            if (isOverlapping)
            {
                return Json(new { success = false, message = "Khung giờ này (hoặc lân cận) đã có khách đặt. Vui lòng chọn giờ khác!" });
            }

            var newAppointment = new Appointment
            {
                PropertyID = propertyId,
                BuyerID = buyerId,
                SellerID = property.UserID,
                AppointmentDate = fullAppointmentDate,
                Note = note,
                Status = "Pending",
                CreatedAt = DateTime.Now
            };

            _context.Appointments.Add(newAppointment);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "Gửi yêu cầu đặt lịch thành công! Chủ nhà sẽ phản hồi sớm." });
        }

        // ==========================================
        // 3. API CẬP NHẬT TRẠNG THÁI (DUYỆT/HỦY)
        // ==========================================
        [HttpPost]
        public async Task<IActionResult> UpdateStatus([FromForm] int id, [FromForm] string status)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            var appointment = await _context.Appointments.FindAsync(id);
            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn!" });

            if (appointment.BuyerID != userId && appointment.SellerID != userId)
                return Json(new { success = false, message = "Truy cập bị từ chối!" });

            // Ràng buộc logic
            if (appointment.BuyerID == userId && status != "Cancelled")
                return Json(new { success = false, message = "Bạn chỉ có quyền hủy lịch hẹn." });

            if (appointment.Status == "Cancelled" || appointment.Status == "Completed")
                return Json(new { success = false, message = "Lịch hẹn đã đóng, không thể thay đổi trạng thái." });

            appointment.Status = status;
            appointment.UpdatedAt = DateTime.Now;

            _context.Update(appointment);
            await _context.SaveChangesAsync();

            string statusMsg = status switch
            {
                "Confirmed" => "đã xác nhận",
                "Cancelled" => "đã hủy",
                "Completed" => "đã hoàn tất",
                _ => "cập nhật"
            };

            return Json(new { success = true, message = $"Lịch hẹn {statusMsg} thành công!" });
        }
    }
}