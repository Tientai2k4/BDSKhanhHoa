using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class AppointmentsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AppointmentsController> _logger;

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

        public AppointmentsController(
            ApplicationDbContext context,
            IEmailService emailService,
            IServiceScopeFactory scopeFactory,
            ILogger<AppointmentsController> logger)
        {
            _context = context;
            _emailService = emailService;
            _scopeFactory = scopeFactory;
            _logger = logger;
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

        private static int ThuTuKetQuaKhachHang(string? resultStatus)
        {
            string ketQua = ChuanHoaKetQua(resultStatus);

            if (string.IsNullOrWhiteSpace(ketQua))
                return 0;

            return ketQua switch
            {
                KetQuaLichHen.KhachQuanTam => 1,
                KetQuaLichHen.CanChamSocThem => 2,
                KetQuaLichHen.ChoDatCoc => 3,
                KetQuaLichHen.KhachKhongQuanTam => 4,
                _ => 0
            };
        }

        private static bool CanChangeOutcome(string? currentResultStatus, string newResultStatus, out string message)
        {
            message = string.Empty;

            string currentResult = ChuanHoaKetQua(currentResultStatus);
            string newResult = ChuanHoaKetQua(newResultStatus);

            if (string.IsNullOrWhiteSpace(newResult))
            {
                message = "Kết quả khách hàng không hợp lệ.";
                return false;
            }

            // Lần đầu cập nhật kết quả: cho chọn bất kỳ kết quả phù hợp thực tế.
            if (string.IsNullOrWhiteSpace(currentResult))
            {
                return true;
            }

            if (currentResult == newResult)
            {
                return true;
            }

            // Khách không quan tâm là hướng xử lý gần như đóng cơ hội.
            // Nếu muốn chăm sóc lại thì nên tạo/ghi nhận lead mới, không sửa lùi lịch sử lịch hẹn cũ.
            if (currentResult == KetQuaLichHen.KhachKhongQuanTam)
            {
                message = "Kết quả hiện tại là “Khách không quan tâm”, không nên đổi ngược sang trạng thái quan tâm trong cùng một lịch hẹn. Nếu khách phát sinh nhu cầu mới, hãy tạo yêu cầu/lịch hẹn mới để lưu lịch sử rõ ràng.";
                return false;
            }

            // Đã lên bước chờ đặt cọc thì không quay lại các bước thấp hơn.
            // Trường hợp khách đổi ý không tiếp tục thì được chuyển sang “Khách không quan tâm”.
            if (currentResult == KetQuaLichHen.ChoDatCoc)
            {
                if (newResult == KetQuaLichHen.KhachKhongQuanTam)
                {
                    return true;
                }

                message = "Khách đã ở bước “Chờ đặt cọc”, không được quay ngược về “Khách quan tâm” hoặc “Cần chăm sóc thêm”. Nếu khách đổi ý không tiếp tục, chọn “Khách không quan tâm”; nếu giao dịch tiến triển thì giữ “Chờ đặt cọc” và hoàn tất lịch khi xử lý xong.";
                return false;
            }

            // Đang chăm sóc thêm có thể lên chờ đặt cọc hoặc kết thúc là không quan tâm,
            // nhưng không quay ngược về mức quan tâm ban đầu.
            if (currentResult == KetQuaLichHen.CanChamSocThem)
            {
                if (newResult == KetQuaLichHen.ChoDatCoc ||
                    newResult == KetQuaLichHen.KhachKhongQuanTam)
                {
                    return true;
                }

                message = "Khách đang ở bước “Cần chăm sóc thêm”, không quay ngược về “Khách quan tâm”. Hãy chọn “Chờ đặt cọc” nếu khách tiến triển, hoặc “Khách không quan tâm” nếu khách không tiếp tục.";
                return false;
            }

            // Khách quan tâm là bước mở đầu sau buổi xem, có thể tiến lên hoặc kết thúc.
            if (currentResult == KetQuaLichHen.KhachQuanTam)
            {
                if (newResult == KetQuaLichHen.CanChamSocThem ||
                    newResult == KetQuaLichHen.ChoDatCoc ||
                    newResult == KetQuaLichHen.KhachKhongQuanTam)
                {
                    return true;
                }

                message = "Kết quả cập nhật không hợp lệ.";
                return false;
            }

            message = "Không thể đổi kết quả khách hàng do trạng thái hiện tại không hợp lệ.";
            return false;
        }


        private IQueryable<Appointment> BuildBaseQuery(int userId)
        {
            // Query nền chỉ lấy bảng Appointments để lọc/đếm nhanh.
            // Khi cần hiển thị mới Include dữ liệu liên quan bên dưới.
            return _context.Appointments
                .AsNoTracking()
                .Where(a => a.BuyerID == userId || a.SellerID == userId);
        }

        private static IQueryable<Appointment> IncludeAppointmentDisplayData(IQueryable<Appointment> query)
        {
            return query
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Project)
                .Include(a => a.Buyer)
                .Include(a => a.Seller)
                .Include(a => a.Lead).ThenInclude(l => l.Project);
        }

        private static IQueryable<Appointment> LocTheoTrangThai(IQueryable<Appointment> query, string? statusFilter)
        {
            if (string.IsNullOrWhiteSpace(statusFilter) || statusFilter == "Tất cả")
                return query;

            string trangThai = ChuanHoaTrangThai(statusFilter);

            return query.Where(a =>
                a.Status == trangThai ||
                a.Status == ChuyenTrangThaiVietSangCu(trangThai));
        }

        private static string CleanText(string? value, int maxLength = 1000)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = value.Trim();

            if (cleaned.Length > maxLength)
                cleaned = cleaned.Substring(0, maxLength);

            return cleaned;
        }

        private static bool TryParseAppointmentDate(string? value, out DateTime date)
        {
            date = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string input = value.Trim();
            string[] formats =
            {
                "yyyy-MM-dd",
                "dd/MM/yyyy",
                "d/M/yyyy",
                "MM/dd/yyyy",
                "M/d/yyyy"
            };

            if (DateTime.TryParseExact(input, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;

            if (DateTime.TryParse(input, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out date))
                return true;

            return DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static bool TryParseAppointmentTime(string? value, out TimeSpan timeSpan)
        {
            timeSpan = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string input = CleanText(value, 30);

            if (TimeSpan.TryParse(input, CultureInfo.InvariantCulture, out timeSpan))
                return timeSpan >= TimeSpan.Zero && timeSpan < TimeSpan.FromDays(1);

            string normalized = Regex.Replace(input, @"\s+", " ").Trim().ToUpperInvariant();
            normalized = normalized.Replace(" SA", " AM").Replace(" CH", " PM");

            string[] timeFormats =
            {
                "H:mm",
                "HH:mm",
                "H:mm:ss",
                "HH:mm:ss",
                "h:mm tt",
                "hh:mm tt"
            };

            if (DateTime.TryParseExact(normalized, timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exactTime))
            {
                timeSpan = exactTime.TimeOfDay;
                return true;
            }

            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedTime))
            {
                timeSpan = parsedTime.TimeOfDay;
                return true;
            }

            if (DateTime.TryParse(input, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out DateTime viTime))
            {
                timeSpan = viTime.TimeOfDay;
                return true;
            }

            return false;
        }

        private static string HtmlSafe(string? value, string fallback = "Chưa cung cấp")
        {
            string text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            return HtmlEncoder.Default.Encode(text);
        }

        private static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return false;

            phone = phone.Trim();

            return Regex.IsMatch(phone, @"^(0|\+84)[0-9\s\.\-]{8,15}$");
        }

        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return true;

            return Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
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

            // Chỉ cho đề xuất dời lịch khi lịch còn chờ xác nhận.
            // Sau khi đã xác nhận thì không dời lịch nữa để quy trình không bị lùi bước.
            return status == TrangThaiLichHen.ChoXacNhan;
        }

        private static bool CanSellerReject(string status)
        {
            status = ChuanHoaTrangThai(status);

            // Chỉ cho từ chối khi lịch còn chờ xác nhận.
            // Khi đã xác nhận, người bán chuyển sang xử lý sau buổi hẹn:
            // cập nhật kết quả, đánh dấu khách không đến hoặc hoàn tất.
            return status == TrangThaiLichHen.ChoXacNhan;
        }

        private static bool CanBuyerCancel(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.ChoXacNhan
                || status == TrangThaiLichHen.DaXacNhan;
        }

        private static bool CanDispatchStaff(string status)
        {
            status = ChuanHoaTrangThai(status);

            return status == TrangThaiLichHen.ChoXacNhan
                || status == TrangThaiLichHen.DaXacNhan
                || status == TrangThaiLichHen.DangDoiLich;
        }

        private static string GetSourceName(Appointment appointment)
        {
            return appointment.Property?.Title
                ?? appointment.Project?.ProjectName
                ?? appointment.Lead?.Project?.ProjectName
                ?? "bất động sản/dự án";
        }


        private async Task<bool> GuiEmailLichHenAnToanAsync(string? email, string subject, string htmlMessage)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

            try
            {
                await _emailService.SendEmailAsync(email.Trim(), subject, htmlMessage);
                return true;
            }
            catch (Exception ex)
            {
                // Không để lỗi SMTP/Brevo làm hỏng nghiệp vụ chính.
                // Lịch hẹn và thông báo trong hệ thống vẫn phải được lưu bình thường.
                _logger.LogWarning(ex, "Không gửi được email lịch hẹn tới {Email}.", email);
                return false;
            }
        }

        private void GuiEmailNenAnToan(string? email, string subject, string htmlMessage)
        {
            if (string.IsNullOrWhiteSpace(email))
                return;

            string toEmail = email.Trim();
            string safeSubject = CleanText(subject, 300);

            // Không await SMTP trong request chính.
            // Nhờ vậy lịch hẹn lưu xong là trả JSON ngay, không bị hiện “hệ thống bận” nếu Brevo/SMTP chậm.
            _ = Task.Run(async () =>
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await emailService.SendEmailAsync(toEmail, safeSubject, htmlMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không gửi được email nền lịch hẹn tới {Email}.", toEmail);
                }
            });
        }
        private async Task<bool> KiemTraNhanVienTrungLichAsync(
                    int currentAppointmentId,
                    int sellerId,
                    DateTime appointmentDate,
                    string? staffPhone,
                    string? staffEmail)
        {
            DateTime fromTime = appointmentDate.AddMinutes(-45);
            DateTime toTime = appointmentDate.AddMinutes(45);

            string phone = CleanText(staffPhone, 50);
            string email = CleanText(staffEmail, 255).ToLowerInvariant();

            IQueryable<Appointment> query = _context.Appointments
                .AsNoTracking()
                .Where(a =>
                    a.AppointmentID != currentAppointmentId &&
                    a.SellerID == sellerId &&
                    a.AppointmentDate >= fromTime &&
                    a.AppointmentDate <= toTime &&
                    (
                        a.Status == TrangThaiLichHen.ChoXacNhan ||
                        a.Status == TrangThaiLichHen.DaXacNhan ||
                        a.Status == TrangThaiLichHen.DangDoiLich ||
                        a.Status == "Pending" ||
                        a.Status == "Confirmed" ||
                        a.Status == "Rescheduled"
                    ));

            if (!string.IsNullOrWhiteSpace(phone) && !string.IsNullOrWhiteSpace(email))
            {
                return await query.AnyAsync(a =>
                    a.AssignedStaffPhone == phone ||
                    (a.AssignedStaffEmail != null && a.AssignedStaffEmail.ToLower() == email));
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                return await query.AnyAsync(a => a.AssignedStaffPhone == phone);
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                return await query.AnyAsync(a =>
                    a.AssignedStaffEmail != null &&
                    a.AssignedStaffEmail.ToLower() == email);
            }

            return false;
        }

        // =====================================================
        // DANH SÁCH LỊCH HẸN
        // =====================================================
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

            if (isBusinessMode)
            {
                baseQuery = baseQuery.Where(a => a.SellerID == userId && (a.ProjectID != null || a.LeadID != null || (a.Property != null && a.Property.ProjectID != null)));
            }

            baseQuery = LocTheoTrangThai(baseQuery, statusFilter);

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                baseQuery = baseQuery.Where(a =>
                    (a.CustomerName != null && EF.Functions.Like(a.CustomerName, $"%{keyword}%")) ||
                    (a.CustomerPhone != null && EF.Functions.Like(a.CustomerPhone, $"%{keyword}%")) ||
                    (a.CustomerEmail != null && EF.Functions.Like(a.CustomerEmail, $"%{keyword}%")) ||
                    (a.AssignedStaffName != null && EF.Functions.Like(a.AssignedStaffName, $"%{keyword}%")) ||
                    (a.AssignedStaffPhone != null && EF.Functions.Like(a.AssignedStaffPhone, $"%{keyword}%")) ||
                    (a.AssignedStaffEmail != null && EF.Functions.Like(a.AssignedStaffEmail, $"%{keyword}%")) ||
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
                        a.Status == "Rescheduled");
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

            if (isBusinessMode)
            {
                fullQuery = fullQuery.Where(a => a.SellerID == userId && (a.ProjectID != null || a.LeadID != null || (a.Property != null && a.Property.ProjectID != null)));
            }

            var summary = await fullQuery
                .GroupBy(a => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Pending = g.Count(a =>
                        a.Status == TrangThaiLichHen.ChoXacNhan ||
                        a.Status == TrangThaiLichHen.DangDoiLich ||
                        a.Status == "Pending" ||
                        a.Status == "Rescheduled"),
                    Confirmed = g.Count(a =>
                        a.Status == TrangThaiLichHen.DaXacNhan ||
                        a.Status == "Confirmed"),
                    Completed = g.Count(a =>
                        a.Status == TrangThaiLichHen.DaHoanTat ||
                        a.Status == "Completed"),
                    Cancelled = g.Count(a =>
                        a.Status == TrangThaiLichHen.DaHuy ||
                        a.Status == "Cancelled"),
                    Today = g.Count(a =>
                        a.AppointmentDate >= today &&
                        a.AppointmentDate < today.AddDays(1))
                })
                .FirstOrDefaultAsync();

            ViewBag.TotalAppointments = summary?.Total ?? 0;
            ViewBag.PendingAppointments = summary?.Pending ?? 0;
            ViewBag.ConfirmedAppointments = summary?.Confirmed ?? 0;
            ViewBag.CompletedAppointments = summary?.Completed ?? 0;
            ViewBag.CancelledAppointments = summary?.Cancelled ?? 0;
            ViewBag.TodayAppointments = summary?.Today ?? 0;

            int totalItems = await baseQuery.CountAsync();
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));

            page = Math.Max(1, page);
            page = Math.Min(page, totalPages);

            List<Appointment> appointments = await IncludeAppointmentDisplayData(baseQuery)
                .AsSplitQuery()
                .OrderByDescending(a => a.AppointmentDate)
                .ThenByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.ActiveTab = tab;
            ViewBag.StatusFilter = statusFilter == "Tất cả" ? "Tất cả" : ChuanHoaTrangThai(statusFilter);
            ViewBag.Keyword = keyword ?? "";
            ViewBag.DateRange = dateRange;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            return View(appointments);
        }

        // =====================================================
        // TẠO LỊCH HẸN TỪ PROPERTY / PROJECT / LEAD
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            int? propertyId,
            int? projectId,
            int? leadId,
            string? customerName,
            string? customerPhone,
            string? customerEmail,
            string? appointmentDate,
            string appointmentTime,
            string? note,
            string? meetingLocation)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập để tạo lịch hẹn." });

            if (!TryParseAppointmentTime(appointmentTime, out TimeSpan timeSpan))
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

            if (!TryParseAppointmentDate(appointmentDate, out DateTime appointmentDay))
                return Json(new { success = false, message = "Ngày hẹn không hợp lệ." });

            DateTime fullAppointmentDate = appointmentDay.Date.Add(timeSpan);

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
                CustomerName = string.IsNullOrWhiteSpace(customerName) ? lead?.Name : customerName.Trim(),
                CustomerPhone = string.IsNullOrWhiteSpace(customerPhone) ? lead?.Phone : customerPhone.Trim(),
                CustomerEmail = string.IsNullOrWhiteSpace(customerEmail) ? lead?.Email : customerEmail.Trim(),
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
                ActionUrl = "/Appointments/Index?mode=DoanhNghiep",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (seller != null && !string.IsNullOrWhiteSpace(seller.Email))
            {
                string subject = $"[BDS Khánh Hòa] Có lịch hẹn mới từ {newAppointment.CustomerName ?? "khách hàng"}";

                string htmlMessage =
                    $"<h3>Bạn có một yêu cầu đặt lịch xem bất động sản/dự án mới</h3>" +
                    $"<p><strong>Khách hàng:</strong> {HtmlSafe(newAppointment.CustomerName)}</p>" +
                    $"<p><strong>Số điện thoại:</strong> {HtmlSafe(newAppointment.CustomerPhone)}</p>" +
                    $"<p><strong>Email:</strong> {HtmlSafe(newAppointment.CustomerEmail)}</p>" +
                    $"<p><strong>Thời gian hẹn:</strong> {fullAppointmentDate:dd/MM/yyyy HH:mm}</p>" +
                    $"<p><strong>Điểm hẹn:</strong> {HtmlSafe(newAppointment.MeetingLocation)}</p>" +
                    $"<p>Vui lòng đăng nhập hệ thống để điều phối nhân viên, xác nhận, dời lịch hoặc từ chối lịch hẹn.</p>";

                GuiEmailNenAnToan(seller.Email, subject, htmlMessage);
            }

            return Json(new
            {
                success = true,
                message = "Đã gửi yêu cầu đặt lịch hẹn thành công. Đang chờ chủ đầu tư/người phụ trách xác nhận."
            });
        }

        // Đặt đúng chỗ: tạo lịch xem dự án từ lead nằm trong AppointmentsController, không đặt ở CRM EditLead.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateFromLead(
            int leadId,
            string? appointmentDate,
            string appointmentTime,
            string? meetingLocation,
            string? note)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            if (!TryParseAppointmentTime(appointmentTime, out TimeSpan timeSpan))
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });

            ProjectLead? lead = await _context.ProjectLeads
                .Include(l => l.Project)
                .FirstOrDefaultAsync(l =>
                    l.LeadID == leadId &&
                    l.Project != null &&
                    l.Project.OwnerUserID == userId &&
                    !l.Project.IsDeleted);

            if (lead == null || lead.Project == null)
                return Json(new { success = false, message = "Không tìm thấy lead hoặc bạn không có quyền tạo lịch cho khách này." });

            if (!TryParseAppointmentDate(appointmentDate, out DateTime appointmentDay))
                return Json(new { success = false, message = "Ngày hẹn không hợp lệ." });

            DateTime fullAppointmentDate = appointmentDay.Date.Add(timeSpan);

            if (fullAppointmentDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian hẹn phải ở trong tương lai." });

            Appointment appointment = new Appointment
            {
                ProjectID = lead.ProjectID,
                LeadID = lead.LeadID,
                BuyerID = userId,
                SellerID = userId,
                CustomerName = lead.Name,
                CustomerPhone = lead.Phone,
                CustomerEmail = lead.Email,
                AppointmentDate = fullAppointmentDate,
                MeetingLocation = string.IsNullOrWhiteSpace(meetingLocation)
                    ? $"Khu dự án {lead.Project.ProjectName}"
                    : meetingLocation.Trim(),
                Note = string.IsNullOrWhiteSpace(note)
                    ? $"Tạo lịch từ lead CRM: {lead.Message}"
                    : note.Trim(),
                Status = TrangThaiLichHen.ChoXacNhan,
                CreatedAt = DateTime.Now
            };

            _context.Appointments.Add(appointment);

            if (lead.LeadStatus == "New" || lead.LeadStatus == "Mới")
            {
                lead.LeadStatus = "Contacted";
            }

            string noteLine = $"[{DateTime.Now:dd/MM/yyyy HH:mm}] Tạo lịch xem dự án: {fullAppointmentDate:dd/MM/yyyy HH:mm}.";
            lead.Note = string.IsNullOrWhiteSpace(lead.Note)
                ? noteLine
                : noteLine + Environment.NewLine + lead.Note;

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Đã tạo lịch xem dự án từ lead. Vào Lịch hẹn dự án để điều phối nhân viên phụ trách."
            });
        }

        // =====================================================
        // ĐIỀU PHỐI NHÂN VIÊN ĐI GẶP KHÁCH - CHỦ ĐẦU TƯ
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DispatchStaff(
            int id,
            string assignedStaffName,
            string assignedStaffPhone,
            string? assignedStaffEmail,
            string? note)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .Include(a => a.Project)
                .Include(a => a.Property).ThenInclude(p => p.Project)
                .Include(a => a.Lead).ThenInclude(l => l.Project)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền điều phối lịch hẹn này." });

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanDispatchStaff(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Lịch hẹn đã đóng nên không thể điều phối nhân viên."
                });
            }

            string staffName = CleanText(assignedStaffName, 255);
            string staffPhone = CleanText(assignedStaffPhone, 20);
            string staffEmail = CleanText(assignedStaffEmail, 255);
            string dispatchNote = CleanText(note, 1500);

            if (string.IsNullOrWhiteSpace(staffName))
                return Json(new { success = false, message = "Vui lòng nhập tên nhân viên phụ trách." });

            if (!IsValidPhone(staffPhone))
                return Json(new { success = false, message = "Số điện thoại nhân viên không hợp lệ." });

            if (!IsValidEmail(staffEmail))
                return Json(new { success = false, message = "Email nhân viên không hợp lệ." });

            bool trungLich = await KiemTraNhanVienTrungLichAsync(
                appointment.AppointmentID,
                userId,
                appointment.AppointmentDate,
                staffPhone,
                staffEmail);

            if (trungLich)
            {
                return Json(new
                {
                    success = false,
                    message = "Nhân viên này đang có lịch khác trong khoảng ±45 phút. Vui lòng chọn nhân viên hoặc khung giờ khác."
                });
            }

            appointment.AssignedStaffName = staffName;
            appointment.AssignedStaffPhone = staffPhone;
            appointment.AssignedStaffEmail = string.IsNullOrWhiteSpace(staffEmail) ? null : staffEmail;

            string dispatchLine =
                $"[{DateTime.Now:dd/MM/yyyy HH:mm}] Điều phối nhân viên: {staffName} - {staffPhone}" +
                (string.IsNullOrWhiteSpace(staffEmail) ? "" : $" - {staffEmail}") +
                (string.IsNullOrWhiteSpace(dispatchNote) ? "" : $". Ghi chú nội bộ: {dispatchNote}");

            // KHÔNG ghi ghi chú điều phối của người bán/chủ đầu tư vào Appointment.Note.
            // Appointment.Note là ghi chú khách nhập khi đặt lịch, người mua có thể xem lại.
            // Ghi chú điều phối phải nằm trong NegotiationNote để chỉ người bán/chủ đầu tư thấy.
            appointment.NegotiationNote = string.IsNullOrWhiteSpace(appointment.NegotiationNote)
                ? dispatchLine
                : dispatchLine + Environment.NewLine + appointment.NegotiationNote;
            appointment.UpdatedAt = DateTime.Now;

            if (currentStatus == TrangThaiLichHen.ChoXacNhan)
            {
                appointment.Status = TrangThaiLichHen.DaXacNhan;
            }

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch xem dự án đã được điều phối",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được điều phối nhân viên {staffName} phụ trách. SĐT nhân viên: {staffPhone}.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                GuiEmailNenAnToan(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Lịch xem dự án đã được điều phối",
                    $"<p>Lịch hẹn của bạn vào <strong>{appointment.AppointmentDate:dd/MM/yyyy HH:mm}</strong> đã được chủ đầu tư điều phối nhân viên phụ trách.</p>" +
                    $"<p><strong>Nhân viên:</strong> {staffName}</p>" +
                    $"<p><strong>Số điện thoại:</strong> {staffPhone}</p>" +
                    $"<p><strong>Điểm hẹn:</strong> {appointment.MeetingLocation ?? "Chưa cập nhật"}</p>"
                );
            }

            if (!string.IsNullOrWhiteSpace(staffEmail))
            {
                string sourceName = GetSourceName(appointment);

                GuiEmailNenAnToan(
                    staffEmail,
                    "[BDS Khánh Hòa] Bạn được phân công gặp khách xem dự án",
                    $"<h3>Bạn được phân công phụ trách một lịch xem dự án</h3>" +
                    $"<p><strong>Dự án/BĐS:</strong> {sourceName}</p>" +
                    $"<p><strong>Khách hàng:</strong> {appointment.CustomerName ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>SĐT khách:</strong> {appointment.CustomerPhone ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>Email khách:</strong> {appointment.CustomerEmail ?? "Chưa cung cấp"}</p>" +
                    $"<p><strong>Thời gian:</strong> {appointment.AppointmentDate:dd/MM/yyyy HH:mm}</p>" +
                    $"<p><strong>Điểm hẹn:</strong> {appointment.MeetingLocation ?? "Chưa cập nhật"}</p>" +
                    $"<p><strong>Ghi chú:</strong> {(string.IsNullOrWhiteSpace(dispatchNote) ? "Không có" : dispatchNote)}</p>"
                );
            }

            return Json(new
            {
                success = true,
                message = "Đã điều phối nhân viên phụ trách lịch xem dự án."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerAccept(int id)
        {
            try
            {
                if (!TryGetCurrentUserId(out int userId))
                    return Json(new { success = false, message = "Vui lòng đăng nhập." });

                Appointment? appointment = await _context.Appointments
                    .Include(a => a.Buyer)
                    .Include(a => a.Property)
                    .Include(a => a.Project)
                    .Include(a => a.Lead).ThenInclude(l => l.Project)
                    .FirstOrDefaultAsync(a => a.AppointmentID == id);

                if (appointment == null)
                    return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

                if (appointment.SellerID != userId)
                    return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

                string currentStatus = ChuanHoaTrangThai(appointment.Status);

                if (currentStatus != TrangThaiLichHen.ChoXacNhan)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Chỉ lịch hẹn đang chờ xác nhận mới được xác nhận. Nếu lịch đã xác nhận thì hệ thống sẽ khóa nút xác nhận, dời lịch và từ chối."
                    });
                }

                DateTime now = DateTime.Now;
                string sourceName = GetSourceName(appointment);

                appointment.Status = TrangThaiLichHen.DaXacNhan;
                appointment.NegotiationNote = string.IsNullOrWhiteSpace(appointment.NegotiationNote)
                    ? $"[{now:HH:mm dd/MM/yyyy}] Người bán/chủ đầu tư đã xác nhận lịch hẹn."
                    : appointment.NegotiationNote.Trim() + Environment.NewLine + $"[{now:HH:mm dd/MM/yyyy}] Người bán/chủ đầu tư đã xác nhận lịch hẹn.";
                appointment.UpdatedAt = now;

                if (appointment.BuyerID > 0)
                {
                    _context.Notifications.Add(new Notification
                    {
                        UserID = appointment.BuyerID,
                        Title = "Lịch hẹn đã được xác nhận",
                        Content = $"Lịch hẹn của bạn vào lúc {appointment.AppointmentDate:dd/MM/yyyy HH:mm} liên quan đến \"{sourceName}\" đã được xác nhận. Vui lòng đến đúng giờ.",
                        ActionUrl = "/Appointments/Index?tab=lich-di",
                        ActionText = "Xem lịch hẹn",
                        IsRead = false,
                        CreatedAt = now
                    });
                }

                await _context.SaveChangesAsync();

                bool daGuiMail = false;

                if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
                {
                    GuiEmailNenAnToan(
                        appointment.Buyer.Email,
                        "[BDS Khánh Hòa] Lịch hẹn đã được xác nhận",
                        $"<h3>Lịch hẹn của bạn đã được xác nhận</h3>" +
                        $"<p><strong>Nguồn lịch hẹn:</strong> {sourceName}</p>" +
                        $"<p><strong>Thời gian hẹn:</strong> {appointment.AppointmentDate:dd/MM/yyyy HH:mm}</p>" +
                        $"<p><strong>Điểm hẹn:</strong> {appointment.MeetingLocation ?? "Chưa cập nhật"}</p>" +
                        $"<p>Vui lòng đến đúng giờ hoặc liên hệ người bán nếu có thay đổi.</p>"
                    );
                    daGuiMail = true;
                }

                return Json(new
                {
                    success = true,
                    message = daGuiMail
                        ? "Đã xác nhận lịch hẹn. Email thông báo đang được gửi nền, không làm chậm thao tác."
                        : "Đã xác nhận lịch hẹn. Thông báo trong hệ thống đã được tạo."
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Lỗi khi xác nhận lịch hẹn: " + ex.Message
                });
            }
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReject(int id, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

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
                return Json(new { success = false, message = "Vui lòng nhập lý do hủy hoặc từ chối lịch hẹn." });

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.NegotiationNote = $"Người bán/chủ đầu tư hủy/từ chối lịch hẹn. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn đã bị hủy",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã bị hủy/từ chối. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                GuiEmailNenAnToan(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Lịch hẹn đã bị hủy",
                    $"Lịch hẹn của bạn đã bị hủy/từ chối.<br/>Lý do: {lyDo}"
                );
            }

            return Json(new { success = true, message = "Đã hủy/từ chối lịch hẹn." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerReschedule(int id, string? proposedDate, string proposedTime, string reason)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            if (!TryParseAppointmentTime(proposedTime, out TimeSpan timeSpan))
                return Json(new { success = false, message = "Giờ hẹn không hợp lệ." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerReschedule(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch chờ xác nhận hoặc đã xác nhận mới được đề xuất dời lịch."
                });
            }

            if (!TryParseAppointmentDate(proposedDate, out DateTime proposedDay))
                return Json(new { success = false, message = "Ngày dời lịch không hợp lệ." });

            DateTime fullProposedDate = proposedDay.Date.Add(timeSpan);

            if (fullProposedDate <= DateTime.Now)
                return Json(new { success = false, message = "Thời gian dời lịch phải ở trong tương lai." });

            string lyDo = CleanText(reason, 1000);

            if (string.IsNullOrWhiteSpace(lyDo))
                return Json(new { success = false, message = "Vui lòng nhập lý do dời lịch." });

            appointment.Status = TrangThaiLichHen.DangDoiLich;
            appointment.ProposedAppointmentDate = fullProposedDate;
            appointment.NegotiationNote = $"Người bán/chủ đầu tư đề xuất dời lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Đề xuất dời lịch hẹn",
                Content = $"Người phụ trách muốn dời lịch sang {fullProposedDate:dd/MM/yyyy HH:mm}. Vui lòng kiểm tra và xác nhận.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                GuiEmailNenAnToan(
                    appointment.Buyer.Email,
                    "[BDS Khánh Hòa] Đề xuất dời lịch hẹn",
                    $"Người phụ trách đề xuất dời lịch sang <strong>{fullProposedDate:dd/MM/yyyy HH:mm}</strong>.<br/>Lý do: {lyDo}<br/>Vui lòng đăng nhập hệ thống để đồng ý hoặc từ chối."
                );
            }

            return Json(new { success = true, message = "Đã gửi đề xuất dời lịch cho khách hàng." });
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
            appointment.NegotiationNote = "Khách hàng đã đồng ý thời gian dời lịch mới.";
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng đồng ý dời lịch",
                Content = $"Khách hàng đã đồng ý dời lịch sang {appointment.AppointmentDate:dd/MM/yyyy HH:mm}.",
                ActionUrl = "/Appointments/Index?mode=DoanhNghiep",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                GuiEmailNenAnToan(
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
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Seller)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.BuyerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

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
                return Json(new { success = false, message = "Vui lòng nhập lý do không đồng ý giờ mới." });

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.ProposedAppointmentDate = null;
            appointment.NegotiationNote = $"Khách hàng không đồng ý giờ dời lịch và hủy lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng không đồng ý dời lịch",
                Content = $"Khách hàng không đồng ý giờ dời lịch và đã hủy lịch. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index?mode=DoanhNghiep",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                GuiEmailNenAnToan(
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
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            string ketQua = ChuanHoaKetQua(resultStatus);

            if (string.IsNullOrWhiteSpace(ketQua))
                return Json(new { success = false, message = "Kết quả khách hàng không hợp lệ." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Chỉ người bán/chủ đầu tư/người phụ trách mới được cập nhật kết quả đi xem." });

            string currentStatus = ChuanHoaTrangThai(appointment.Status);

            if (!CanSellerEditOutcome(currentStatus))
            {
                return Json(new
                {
                    success = false,
                    message = "Chỉ lịch hẹn đã xác nhận mới được cập nhật kết quả đi xem. Lịch đã hủy, hoàn tất hoặc khách không đến sẽ bị khóa."
                });
            }

            string oldResult = ChuanHoaKetQua(appointment.ResultStatus);

            if (!CanChangeOutcome(oldResult, ketQua, out string blockMessage))
            {
                return Json(new
                {
                    success = false,
                    message = blockMessage
                });
            }

            string cleanNote = CleanText(resultNote, 3000);
            DateTime now = DateTime.Now;

            appointment.ResultStatus = ketQua;
            appointment.ResultNote = string.IsNullOrWhiteSpace(cleanNote) ? null : cleanNote;
            appointment.UpdatedAt = now;

            string resultLog = string.IsNullOrWhiteSpace(oldResult)
                ? $"[{now:dd/MM/yyyy HH:mm}] Người phụ trách cập nhật kết quả khách hàng: {ketQua}."
                : $"[{now:dd/MM/yyyy HH:mm}] Người phụ trách đổi kết quả khách hàng từ “{oldResult}” sang “{ketQua}”.";

            appointment.NegotiationNote = string.IsNullOrWhiteSpace(appointment.NegotiationNote)
                ? resultLog
                : resultLog + Environment.NewLine + appointment.NegotiationNote;

            // Chỉ thông báo kết quả tổng quát cho người mua, tuyệt đối không gửi ResultNote/Ghi chú nội bộ.
            if (appointment.BuyerID > 0 && appointment.BuyerID != userId)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = appointment.BuyerID,
                    Title = "Lịch hẹn đã được cập nhật kết quả",
                    Content = $"Người phụ trách đã cập nhật kết quả buổi xem: {ketQua}.",
                    ActionUrl = "/Appointments/Index",
                    ActionText = "Xem lịch hẹn",
                    IsRead = false,
                    CreatedAt = now
                });
            }

            await _context.SaveChangesAsync();

            string message = string.IsNullOrWhiteSpace(oldResult)
                ? "Đã lưu kết quả khách hàng. Bạn có thể cập nhật tiếp theo đúng quy trình; khi chắc chắn xong hãy bấm Hoàn tất để khóa lịch."
                : "Đã cập nhật kết quả khách hàng đúng quy trình. Hệ thống không cho quay ngược các bước xử lý đã tiến triển.";

            return Json(new
            {
                success = true,
                message
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SellerComplete(int id)
        {
            if (!TryGetCurrentUserId(out int userId))
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền hoàn tất lịch hẹn này." });

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
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được đánh dấu hoàn tất.",
                ActionUrl = "/Appointments/Index",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Buyer != null && !string.IsNullOrWhiteSpace(appointment.Buyer.Email))
            {
                GuiEmailNenAnToan(
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
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Buyer)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.SellerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền thao tác lịch hẹn này." });

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
                ? "Người phụ trách đánh dấu khách không đến buổi hẹn."
                : $"Người phụ trách đánh dấu khách không đến. Ghi chú: {lyDo}";
            appointment.CompletedAt = DateTime.Now;
            appointment.UpdatedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.BuyerID,
                Title = "Lịch hẹn được đánh dấu khách không đến",
                Content = $"Lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm} đã được đánh dấu khách không đến.",
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
                return Json(new { success = false, message = "Vui lòng đăng nhập." });

            Appointment? appointment = await _context.Appointments
                .Include(a => a.Seller)
                .FirstOrDefaultAsync(a => a.AppointmentID == id);

            if (appointment == null)
                return Json(new { success = false, message = "Không tìm thấy lịch hẹn." });

            if (appointment.BuyerID != userId)
                return Json(new { success = false, message = "Bạn không có quyền hủy lịch hẹn này." });

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
                return Json(new { success = false, message = "Vui lòng nhập lý do hủy lịch." });

            appointment.Status = TrangThaiLichHen.DaHuy;
            appointment.NegotiationNote = $"Người mua hủy lịch. Lý do: {lyDo}";
            appointment.UpdatedAt = DateTime.Now;
            appointment.CompletedAt = DateTime.Now;

            _context.Notifications.Add(new Notification
            {
                UserID = appointment.SellerID,
                Title = "Khách hàng hủy lịch hẹn",
                Content = $"Khách hàng đã hủy lịch hẹn vào {appointment.AppointmentDate:dd/MM/yyyy HH:mm}. Lý do: {lyDo}",
                ActionUrl = "/Appointments/Index?mode=DoanhNghiep",
                ActionText = "Xem lịch hẹn",
                IsRead = false,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();

            if (appointment.Seller != null && !string.IsNullOrWhiteSpace(appointment.Seller.Email))
            {
                GuiEmailNenAnToan(
                    appointment.Seller.Email,
                    "[BDS Khánh Hòa] Khách hàng hủy lịch hẹn",
                    $"Khách hàng {appointment.CustomerName ?? "chưa rõ tên"} đã hủy lịch hẹn.<br/>Lý do: {lyDo}"
                );
            }

            return Json(new { success = true, message = "Bạn đã hủy lịch hẹn thành công." });
        }
    }
}
