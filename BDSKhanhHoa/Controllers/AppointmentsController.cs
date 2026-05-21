using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
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
        private readonly IEmailService _emailService;

        /*
            Quy ước trạng thái tiếng Việt mới:
            - Chờ xác nhận
            - Đã xác nhận
            - Đã hủy
            - Đã hoàn tất
            - Đang dời lịch
            - Khách không đến

            Quy ước kết quả sau buổi xem:
            - Khách quan tâm
            - Khách không quan tâm
            - Chờ đặt cọc
            - Cần chăm sóc thêm

            Code vẫn có hàm chuẩn hóa để đọc được dữ liệu cũ:
            Pending       -> Chờ xác nhận
            Confirmed     -> Đã xác nhận
            Cancelled     -> Đã hủy
            Completed     -> Đã hoàn tất
            Rescheduled   -> Đang dời lịch
            NoShow        -> Khách không đến
        */

        private static class TrangThaiLichHen
        {
            public const string ChoXacNhan = "Chờ xác nhận";
            public const string DaXacNhan = "Đã xác nhận";
            public const string DaHuy = "Đã hủy";
            public const string DaHoanTat = "Đã hoàn tất";
            public const string DangDoiLich = "Đang dời lịch";
            public const string KhachKhongDen = "Khách không đến";
        }

        private static class KetQuaLichHen
        {
            public const string KhachQuanTam = "Khách quan tâm";
            public const string KhachKhongQuanTam = "Khách không quan tâm";
            public const string ChoDatCoc = "Chờ đặt cọc";
            public const string CanChamSocThem = "Cần chăm sóc thêm";
        }

        private static readonly HashSet<string> DanhSachTrangThaiHopLe = new(StringComparer.OrdinalIgnoreCase)
        {
            TrangThaiLichHen.ChoXacNhan,
            TrangThaiLichHen.DaXacNhan,
            TrangThaiLichHen.DaHuy,
            TrangThaiLichHen.DaHoanTat,
            TrangThaiLichHen.DangDoiLich,
            TrangThaiLichHen.KhachKhongDen,

            // Hỗ trợ dữ liệu cũ tiếng Anh
            "Pending",
            "Confirmed",
            "Cancelled",
            "Completed",
            "Rescheduled",
            "NoShow"
        };

        private static readonly HashSet<string> DanhSachKetQuaHopLe = new(StringComparer.OrdinalIgnoreCase)
        {
            KetQuaLichHen.KhachQuanTam,
            KetQuaLichHen.KhachKhongQuanTam,
            KetQuaLichHen.ChoDatCoc,
            KetQuaLichHen.CanChamSocThem,

            // Hỗ trợ dữ liệu cũ tiếng Anh
            "Interested",
            "NotInterested",
            "DepositPending",
            "FollowUp"
        };

        public AppointmentsController(ApplicationDbContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        private bool TryGetCurrentUserId(out int userId)
        {
            userId = 0;
            string? userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(userIdStr, out userId);
        }

        private static string ChuanHoaTrangThai(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return TrangThaiLichHen.ChoXacNhan;

            string value = status.Trim();

            return value switch
            {
                "Pending" => TrangThaiLichHen.ChoXacNhan,
                "Confirmed" => TrangThaiLichHen.DaXacNhan,
                "Cancelled" => TrangThaiLichHen.DaHuy,
                "Completed" => TrangThaiLichHen.DaHoanTat,
                "Rescheduled" => TrangThaiLichHen.DangDoiLich,
                "NoShow" => TrangThaiLichHen.KhachKhongDen,

                TrangThaiLichHen.ChoXacNhan => TrangThaiLichHen.ChoXacNhan,
                TrangThaiLichHen.DaXacNhan => TrangThaiLichHen.DaXacNhan,
                TrangThaiLichHen.DaHuy => TrangThaiLichHen.DaHuy,
                TrangThaiLichHen.DaHoanTat => TrangThaiLichHen.DaHoanTat,
                TrangThaiLichHen.DangDoiLich => TrangThaiLichHen.DangDoiLich,
                TrangThaiLichHen.KhachKhongDen => TrangThaiLichHen.KhachKhongDen,

                _ => TrangThaiLichHen.ChoXacNhan
            };
        }

        private static string ChuanHoaKetQua(string? resultStatus)
        {
            if (string.IsNullOrWhiteSpace(resultStatus))
                return "";

            string value = resultStatus.Trim();

            return value switch
            {
                "Interested" => KetQuaLichHen.KhachQuanTam,
                "NotInterested" => KetQuaLichHen.KhachKhongQuanTam,
                "DepositPending" => KetQuaLichHen.ChoDatCoc,
                "FollowUp" => KetQuaLichHen.CanChamSocThem,

                KetQuaLichHen.KhachQuanTam => KetQuaLichHen.KhachQuanTam,
                KetQuaLichHen.KhachKhongQuanTam => KetQuaLichHen.KhachKhongQuanTam,
                KetQuaLichHen.ChoDatCoc => KetQuaLichHen.ChoDatCoc,
                KetQuaLichHen.CanChamSocThem => KetQuaLichHen.CanChamSocThem,

                _ => ""
            };
        }

        private IQueryable<Appointment> BuildBaseQuery(int userId)
        {
            return _context.Appointments
                .AsNoTracking()
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead)
                .Where(a => a.BuyerID == userId || a.SellerID == userId);
        }

        private static IQueryable<Appointment> LocTheoTrangThai(IQueryable<Appointment> query, string? statusFilter)
        {
            if (string.IsNullOrWhiteSpace(statusFilter) || statusFilter == "Tất cả")
                return query;

            string trangThai = ChuanHoaTrangThai(statusFilter);

            return query.Where(a =>
                a.Status == trangThai
                || a.Status == ChuyenTrangThaiVietSangCu(trangThai)
            );
        }

        private static string ChuyenTrangThaiVietSangCu(string trangThaiTiengViet)
        {
            return trangThaiTiengViet switch
            {
                TrangThaiLichHen.ChoXacNhan => "Pending",
                TrangThaiLichHen.DaXacNhan => "Confirmed",
                TrangThaiLichHen.DaHuy => "Cancelled",
                TrangThaiLichHen.DaHoanTat => "Completed",
                TrangThaiLichHen.DangDoiLich => "Rescheduled",
                TrangThaiLichHen.KhachKhongDen => "NoShow",
                _ => ""
            };
        }

        private static string ChuyenKetQuaVietSangCu(string ketQuaTiengViet)
        {
            return ketQuaTiengViet switch
            {
                KetQuaLichHen.KhachQuanTam => "Interested",
                KetQuaLichHen.KhachKhongQuanTam => "NotInterested",
                KetQuaLichHen.ChoDatCoc => "DepositPending",
                KetQuaLichHen.CanChamSocThem => "FollowUp",
                _ => ""
            };
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? mode = null,
            string tab = "tat-ca",
            string statusFilter = "Tất cả",
            string? keyword = null,
            string dateRange = "tat-ca",
            int page = 1)
        {
            if (!TryGetCurrentUserId(out int userId))
                return RedirectToAction("Login", "Account");

            const int pageSize = 12;

            string appointmentMode = string.IsNullOrWhiteSpace(mode) ? "CaNhan" : mode.Trim();
            bool isBusinessMode = appointmentMode.Equals("DoanhNghiep", StringComparison.OrdinalIgnoreCase)
                               || appointmentMode.Equals("Business", StringComparison.OrdinalIgnoreCase);

            ViewBag.AppointmentMode = isBusinessMode ? "DoanhNghiep" : "CaNhan";
            ViewBag.CurrentUserId = userId;

            DateTime today = DateTime.Now.Date;

            IQueryable<Appointment> baseQuery = BuildBaseQuery(userId);

            baseQuery = LocTheoTrangThai(baseQuery, statusFilter);

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                baseQuery = baseQuery.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.CustomerEmail != null && EF.Functions.Like(a.CustomerEmail, $"%{keyword}%")) ||
                    (a.Note != null && EF.Functions.Like(a.Note, $"%{keyword}%")) ||
                    (a.MeetingLocation != null && EF.Functions.Like(a.MeetingLocation, $"%{keyword}%")) ||
                    (a.Property != null && a.Property.Title != null && EF.Functions.Like(a.Property.Title, $"%{keyword}%")) ||
                    (a.Project != null && a.Project.ProjectName != null && EF.Functions.Like(a.Project.ProjectName, $"%{keyword}%"))
                );
            }

            switch ((dateRange ?? "tat-ca").Trim().ToLowerInvariant())
            {
                case "hom-nay":
                case "today":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today && a.AppointmentDate < today.AddDays(1));
                    break;

                case "7-ngay":
                case "week":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today.AddDays(-7));
                    break;

                case "30-ngay":
                case "month":
                    baseQuery = baseQuery.Where(a => a.AppointmentDate >= today.AddMonths(-1));
                    break;
            }

            switch ((tab ?? "tat-ca").Trim().ToLowerInvariant())
            {
                case "lich-den":
                case "incoming":
                    baseQuery = baseQuery.Where(a => a.SellerID == userId);
                    break;

                case "lich-di":
                case "outgoing":
                    baseQuery = baseQuery.Where(a => a.BuyerID == userId);
                    break;

                case "sap-toi":
                case "upcoming":
                    baseQuery = baseQuery.Where(a =>
                        a.Status == TrangThaiLichHen.ChoXacNhan ||
                        a.Status == TrangThaiLichHen.DaXacNhan ||
                        a.Status == TrangThaiLichHen.DangDoiLich ||
                        a.Status == "Pending" ||
                        a.Status == "Confirmed" ||
                        a.Status == "Rescheduled"
                    );
                    break;

                case "hoan-tat":
                case "completed":
                    baseQuery = baseQuery.Where(a => a.Status == TrangThaiLichHen.DaHoanTat || a.Status == "Completed");
                    break;

                case "da-huy":
                case "cancelled":
                    baseQuery = baseQuery.Where(a => a.Status == TrangThaiLichHen.DaHuy || a.Status == "Cancelled");
                    break;
            }

            IQueryable<Appointment> fullQuery = BuildBaseQuery(userId);

            ViewBag.TotalAppointments = await fullQuery.CountAsync();

            ViewBag.PendingAppointments = await fullQuery.CountAsync(a =>
                a.Status == TrangThaiLichHen.ChoXacNhan ||
                a.Status == TrangThaiLichHen.DangDoiLich ||
                a.Status == "Pending" ||
                a.Status == "Rescheduled"
            );

            ViewBag.ConfirmedAppointments = await fullQuery.CountAsync(a =>
                a.Status == TrangThaiLichHen.DaXacNhan ||
                a.Status == "Confirmed"
            );

            ViewBag.CompletedAppointments = await fullQuery.CountAsync(a =>
                a.Status == TrangThaiLichHen.DaHoanTat ||
                a.Status == "Completed"
            );

            ViewBag.CancelledAppointments = await fullQuery.CountAsync(a =>
                a.Status == TrangThaiLichHen.DaHuy ||
                a.Status == "Cancelled"
            );

            ViewBag.TodayAppointments = await fullQuery.CountAsync(a =>
                a.AppointmentDate >= today &&
                a.AppointmentDate < today.AddDays(1)
            );

            int totalItems = await baseQuery.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            page = Math.Max(1, page);
            page = Math.Min(page, totalPages);

            List<Appointment> appointments = await baseQuery
                .OrderByDescending(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.ActiveTab = tab;
            ViewBag.StatusFilter = ChuanHoaTrangThai(statusFilter) == TrangThaiLichHen.ChoXacNhan && statusFilter == "Tất cả"
                ? "Tất cả"
                : ChuanHoaTrangThai(statusFilter);

            ViewBag.Keyword = keyword ?? "";
            ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(appointments);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            int? propertyId,
            int? projectId,
            int? leadId,
            string? customerName,
            string? customerPhone,
            string? customerEmail,
            DateTime appointmentDate,
            string appointmentTime,
            string? note,
            string? meetingLocation)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để tạo lịch hẹn." });

            if (!TimeSpan.TryParse(appointmentTime, out TimeSpan timeSpan))
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });

            Property? property = null;
            Project? project = null;
            ProjectLead? lead = null;

            if (propertyId.HasValue && propertyId.Value > 0)
            {
                property = await _context.Properties
                    .Include(p => p.Project)
                    .FirstOrDefaultAsync(p => p.PropertyID == propertyId.Value);

                if (property == null || property.IsDeleted == true)
                    return Json(new { success = false, message = "Bất động sản không tồn tại hoặc đã bị gỡ." });

                project ??= property.Project;
            }

            if (projectId.HasValue && projectId.Value > 0)
            {
                project ??= await _context.Projects
                    .FirstOrDefaultAsync(p => p.ProjectID == projectId.Value && !p.IsDeleted);

                if (project == null)
                    return Json(new { success = false, message = "Dự án không tồn tại." });
            }

            if (leadId.HasValue && leadId.Value > 0)
            {
                lead = await _context.ProjectLeads
                    .Include(l => l.Project)
                    .FirstOrDefaultAsync(l => l.LeadID == leadId.Value);

                if (lead == null)
                    return Json(new { success = false, message = "Không tìm thấy khách hàng tiềm năng." });

                project ??= lead.Project;
            }

            DateTime fullAppointmentDate = appointmentDate.Date.Add(timeSpan);

            if (fullAppointmentDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian hẹn phải ở trong tương lai." });

            int sellerId = property?.UserID
                        ?? project?.OwnerUserID
                        ?? lead?.Project?.OwnerUserID
                        ?? userId;

            User? seller = await _context.Users.FindAsync(sellerId);

            Appointment newAppointment = new Appointment
            {
                PropertyID = property?.PropertyID,
                ProjectID = project?.ProjectID,
                LeadID = lead?.LeadID,
                BuyerID = userId,
                SellerID = sellerId,
                CustomerName = string.IsNullOrWhiteSpace(customerName) ? null : customerName.Trim(),
                CustomerPhone = string.IsNullOrWhiteSpace(customerPhone) ? null : customerPhone.Trim(),
                CustomerEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim(),
                AppointmentDate = fullAppointmentDate,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                MeetingLocation = string.IsNullOrWhiteSpace(meetingLocation) ? null : meetingLocation.Trim(),
                Status = TrangThaiLichHen.ChoXacNhan,
                CreatedAt = DateTime.Now
            };

            _context.Appointments.Add(newAppointment);

            _context.Notifications.Add(new Notification
            {
                UserID = sellerId,
                Title = "Có lịch hẹn mới",
                Content = $"Khách hàng {newAppointment.CustomerName ?? "chưa rõ tên"} vừa đặt lịch hẹn vào {fullAppointmentDate:dd/MM/yyyy HH:mm}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (seller != null && !string.IsNullOrWhiteSpace(seller.Email))
            {
                string subject = $"[BDS Khánh Hòa] Có lịch hẹn mới từ {newAppointment.CustomerName ?? "khách hàng"}";

                string htmlMessage =
                    $"<h3>Bạn có một yêu cầu đặt lịch xem bất động sản mới</h3>" +
                    $"<p><strong>Khách hàng:</strong> {newAppointment.CustomerName ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>Số điện thoại:</strong> {newAppointment.CustomerPhone ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>Email:</strong> {newAppointment.CustomerEmail ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>Thời gian hẹn:</strong> {fullAppointmentDate:dd/MM/yyyy HH:mm}</p>" +
                    $"<p><strong>Điểm hẹn:</strong> {newAppointment.MeetingLocation ?? "Chưa cung cấp"}</p>" +
                    $"<p>Vui lòng đăng nhập hệ thống để xác nhận, dời lịch hoặc từ chối lịch hẹn.</p>";

                await _emailService.SendEmailAsync(seller.Email, subject, htmlMessage);
            }

            return Json(new
            {
                success = true,
                message = "Đã gửi yêu cầu đặt lịch hẹn thành công. Đang chờ người bán xác nhận."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerAccept(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (currentStatus != TrangThaiLichHen.ChoXacNhan)
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch hẹn đang chờ xác nhận mới được xác nhận."
                });
            }

            appointment.Status = TrangThaiLichHen.DaXacNhan;
            appointment.NegotiationNote = "Người bán đã xác nhận lịch hẹn.";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã được xác nhận",
                Content = $"Lịch hẹn của bạn vào lúc {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được người bán xác nhận.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Lịch hẹn đã được xác nhận",
                    $"Lịch hẹn của bạn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được xác nhận. Vui lòng đến đúng giờ."
                );
            }

            return Json(new
            {
                success = true,
                message = "Đã xác nhận lịch hẹn. Bạn có thể cập nhật kết quả sau khi gặp khách."
            });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReject(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerReject(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Lịch hẹn này đã đóng nên không thể hủy hoặc từ chối."
                });
            }

            string lyDo = CleanText(reason, 1000);

            if (string.IsNullOrWhiteSpace(lyDo))
            {
                return Json(new { success = false, message = "Vui lòng nhập lý do hủy hoặc từ chối lịch hẹn." });
            }

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.NegotiationNote = $"Người bán hủy/từ chối lịch hẹn. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã bị hủy",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã bị người bán hủy/từ chối. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Lịch hẹn đã bị hủy",
                    $"Người bán đã hủy/từ chối lịch hẹn của bạn.<br/>Lý do: {lyDo}"
                );
            }

            return Json(new { success = true, message = "Đã hủy/từ chối lịch hẹn." });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReschedule(int id, DateTime proposedDate, string proposedTime, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            if (!TimeSpan.TryParse(proposedTime, out TimeSpan timeSpan))
            {
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerReschedule(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch chờ xác nhận hoặc đã xác nhận mới được đề xuất dời lịch."
                });
            }

            DateTime fullProposedDate = proposedDate.Date.Add(timeSpan);

            if (fullProposedDate <= DateTime.Now)
            {
                return Json(new { success = false, message = "Thời gian dời lịch phải ở trong tương lai." });
            }

            string lyDo = CleanText(reason, 1000);

            if (string.IsNullOrWhiteSpace(lyDo))
            {
                return Json(new { success = false, message = "Vui lòng nhập lý do dời lịch." });
            }

            appointment.Status = TrangThaiLichHen.DangDoiLich;
            appointment.ProposedAppointmentDate = fullProposedDate;
            appointment.NegotiationNote = $"Người bán đề xuất dời lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Người bán đề xuất dời lịch hẹn",
                Content = $"Người bán muốn dời lịch sang {fullProposedDate:dd/MM/yyyy HH:mm}. Vui lòng kiểm tra và xác nhận.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Đề xuất dời lịch hẹn",
                    $"Người bán đề xuất dời lịch sang <strong>{fullProposedDate:dd/MM/yyyy HH:mm}</strong>.<br/>Lý do: {lyDo}<br/>Vui lòng đăng nhập hệ thống để đồng ý hoặc từ chối."
                );
            }

            return Json(new { success = true, message = "Đã gửi đề xuất dời lịch cho người mua." });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyerAcceptReschedule(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Seller)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.BuyerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

            string status = ChuanHoaTrangThai(appointment.Status);

            if (status != TrangThaiLichHen.DangDoiLich || !appointment.ProposedAppointmentDate.HasValue)
                return Json(new { success = false, message = "Lịch hẹn không ở trạng thái chờ xác nhận dời lịch." });

            appointment.AppointmentDate = appointment.ProposedAppointmentDate.Value;
            appointment.ProposedAppointmentDate = null;
            appointment.Status = TrangThaiLichHen.DaXacNhan;
            appointment.NegotiationNote = "Người mua đã đồng ý thời gian dời lịch mới.";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng đồng ý dời lịch",
                Content = $"Khách hàng đã đồng ý dời lịch sang {appointment.AppointmentDate:dd/MM/yyyy HH:mm}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Seller.Email,
                    "[BDS Khánh Hòa] Khách hàng đồng ý dời lịch",
                    $"Khách hàng {appointment.CustomerName ?? "chưa rõ tên"} đã đồng ý dời lịch hẹn sang {appointment.AppointmentDate:dd/MM/yyyy HH:mm}."
                );
            }

            return Json(new { success = true, message = "Bạn đã đồng ý thời gian dời lịch mới." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyerRejectReschedule(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Seller)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.BuyerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (currentStatus != TrangThaiLichHen.DangDoiLich)
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch đang dời lịch mới được từ chối giờ đề xuất."
                });
            }

            string lyDo = CleanText(reason, 1000);

            if (string.IsNullOrWhiteSpace(lyDo))
            {
                return Json(new { success = false, message = "Vui lòng nhập lý do không đồng ý giờ mới." });
            }

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.ProposedAppointmentDate = null;
            appointment.NegotiationNote = $"Người mua không đồng ý giờ dời lịch và hủy lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng không đồng ý dời lịch",
                Content = $"Khách hàng không đồng ý giờ dời lịch và đã hủy lịch. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Seller.Email,
                    "[BDS Khánh Hòa] Khách hàng không đồng ý dời lịch",
                    $"Khách hàng {appointment.CustomerName ?? "chưa rõ tên"} không đồng ý thời gian dời lịch và đã hủy lịch.<br/>Lý do: {lyDo}"
                );
            }

            return Json(new { success = true, message = "Bạn đã từ chối giờ dời lịch và hủy lịch hẹn." });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOutcome([FromForm] int id, [FromForm] string resultStatus, [FromForm] string? resultNote)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            string ketQua = ChuanHoaKetQua(resultStatus);

            if (string.IsNullOrWhiteSpace(ketQua))
            {
                return Json(new { success = false, message = "Kết quả khách hàng không hợp lệ." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Chỉ người bán/người phụ trách mới được cập nhật kết quả đi xem." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerEditOutcome(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch hẹn đã xác nhận mới được cập nhật kết quả đi xem. Lịch đã hủy, hoàn tất hoặc khách không đến sẽ bị khóa."
                });
            }

            string cleanNote = CleanText(resultNote, 3000);

            appointment.ResultStatus = ketQua;
            appointment.ResultNote = string.IsNullOrWhiteSpace(cleanNote) ? null : cleanNote;
            appointment.UpdatedAt = DateTime.Now;

            /*
                Lưu ý:
                Không chuyển Status sang "Đã hoàn tất" ở đây.
                Người bán có thể cập nhật kết quả nhiều lần.
                Chỉ khi bấm SellerComplete mới khóa lịch.
            */

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã được cập nhật kết quả",
                Content = $"Người bán đã cập nhật kết quả buổi xem: {ketQua}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã lưu kết quả đi xem. Lịch hẹn vẫn đang mở để bạn có thể cập nhật thêm hoặc hoàn tất sau."
            });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerComplete(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền hoàn tất lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerComplete(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch hẹn đã xác nhận mới được hoàn tất."
                });
            }

            if (string.IsNullOrWhiteSpace(appointment.ResultStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng cập nhật kết quả khách hàng trước khi hoàn tất lịch hẹn."
                });
            }

            appointment.Status = TrangThaiLichHen.DaHoanTat;
            appointment.CompletedAt = DateTime.Now;
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã hoàn tất",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được người bán đánh dấu hoàn tất.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Lịch hẹn đã hoàn tất",
                    $"Lịch hẹn của bạn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được đánh dấu hoàn tất."
                );
            }

            return Json(new
            {
                success = true,
                message = "Đã hoàn tất lịch hẹn. Lịch này sẽ được khóa để lưu lịch sử."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerNoShow(int id, string? reason)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.SellerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (currentStatus != TrangThaiLichHen.DaXacNhan)
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch hẹn đã xác nhận mới được đánh dấu khách không đến."
                });
            }

            string lyDo = CleanText(reason, 1000);

            appointment.Status = TrangThaiLichHen.KhachKhongDen;
            appointment.NegotiationNote = string.IsNullOrWhiteSpace(lyDo)
                ? "Người bán đánh dấu khách không đến buổi hẹn."
                : $"Người bán đánh dấu khách không đến. Ghi chú: {lyDo}";
            appointment.CompletedAt = DateTime.Now;
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn được đánh dấu khách không đến",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được người bán đánh dấu khách không đến.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã đánh dấu khách không đến. Lịch hẹn đã được khóa."
            });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BuyerCancel(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
            {
                return Json(new { success = false, message = "Vui lòng đăng nhập." });
            }

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Seller)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
            {
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });
            }

            if (appointment.BuyerID != userId)
            {
                return Json(new { success = false, message = "Bạn không có quyền hủy lịch hẹn này." });
            }

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanBuyerCancel(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Bạn chỉ có thể hủy lịch khi lịch còn chờ xác nhận hoặc đã xác nhận. Lịch đã đóng không thể hủy."
                });
            }

            string lyDo = CleanText(reason, 1000);

            if (string.IsNullOrWhiteSpace(lyDo))
            {
                return Json(new { success = false, message = "Vui lòng nhập lý do hủy lịch." });
            }

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.NegotiationNote = $"Người mua hủy lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng hủy lịch hẹn",
                Content = $"Khách hàng đã hủy lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm}. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                await _emailService.SendEmailAsync(
                    appointment.Seller.Email,
                    "[BDS Khánh Hòa] Khách hàng hủy lịch hẹn",
                    $"Khách hàng {appointment.CustomerName ?? "chưa rõ tên"} đã hủy lịch hẹn.<br/>Lý do: {lyDo}"
                );
            }

            return Json(new { success = true, message = "Bạn đã hủy lịch hẹn thành công." });
        }
        private static string CleanText(string? value, int maxLength = 1000)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string cleaned = value.Trim();

            if (cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength);
            }

            return cleaned;
        }

        private static bool IsFinalAppointmentStatus(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.DaHoanTat
                || status == TrangThaiLichHen.DaHuy
                || status == TrangThaiLichHen.KhachKhongDen;
        }

        private static bool CanSellerEditOutcome(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.DaXacNhan;
        }

        private static bool CanSellerComplete(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.DaXacNhan;
        }

        private static bool CanSellerReschedule(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.ChoXacNhan
                || status == TrangThaiLichHen.DaXacNhan;
        }

        private static bool CanSellerReject(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.ChoXacNhan
                || status == TrangThaiLichHen.DaXacNhan
                || status == TrangThaiLichHen.DangDoiLich;
        }

        private static bool CanBuyerCancel(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.ChoXacNhan
                || status == TrangThaiLichHen.DaXacNhan;
        }

        private static string GetSourceName(Appointment appointment)
        {
            return appointment.Property?.Title
                ?? appointment.Project?.ProjectName
                ?? appointment.Lead?.Project?.ProjectName
                ?? "bất động sản/dự án";
        }

    }
}