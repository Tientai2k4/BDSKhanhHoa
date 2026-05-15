using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class VouchersController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuditLogService _auditLogService; // Thêm Service Log

        public VouchersController(ApplicationDbContext context, IAuditLogService auditLogService)
        {
            _context = context;
            _auditLogService = auditLogService;
        }

        public async Task<IActionResult> Index()
        {
            var vouchers = await _context.Vouchers.OrderByDescending(v => v.CreatedAt).ToListAsync();

            ViewBag.TotalVouchers = vouchers.Count;
            ViewBag.ActiveVouchers = vouchers.Count(v => v.IsActive && v.ExpiryDate >= DateTime.Now && v.StartDate <= DateTime.Now);
            ViewBag.TotalUsed = vouchers.Sum(v => v.UsedCount);

            ViewData["Title"] = "Quản lý Mã giảm giá (Vouchers)";
            return View(vouchers);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Voucher voucher)
        {
            if (ModelState.IsValid)
            {
                voucher.Code = voucher.Code.ToUpper().Trim();

                if (await _context.Vouchers.AnyAsync(v => v.Code == voucher.Code))
                {
                    TempData["Error"] = "Mã Voucher này đã tồn tại!";
                    return RedirectToAction(nameof(Index));
                }

                if (voucher.StartDate >= voucher.ExpiryDate)
                {
                    TempData["Error"] = "Ngày bắt đầu phải nhỏ hơn ngày hết hạn!";
                    return RedirectToAction(nameof(Index));
                }

                if (string.IsNullOrEmpty(voucher.Description))
                {
                    voucher.Description = $"Giảm {voucher.DiscountPercent}% tối đa {voucher.MaxDiscountAmount:N0}đ cho đơn từ {voucher.MinOrderAmount:N0}đ";
                }

                voucher.CreatedAt = DateTime.Now;
                voucher.UsedCount = 0;
                voucher.IsActive = true;

                _context.Vouchers.Add(voucher);
                await _context.SaveChangesAsync();

                // GHI LOG
                int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
                await _auditLogService.LogAsync(adminId, "Tạo mã giảm giá mới", "Vouchers", $"Code: {voucher.Code}", severity: "Info");

                TempData["Success"] = "Tạo mã giảm giá thành công!";
            }
            else
            {
                TempData["Error"] = "Dữ liệu không hợp lệ, vui lòng kiểm tra lại!";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var voucher = await _context.Vouchers.FindAsync(id);
            if (voucher == null) return NotFound();

            voucher.IsActive = !voucher.IsActive;
            await _context.SaveChangesAsync();

            // GHI LOG
            int adminId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
            await _auditLogService.LogAsync(adminId, voucher.IsActive ? "Kích hoạt Voucher" : "Khóa Voucher", "Vouchers", $"VoucherID: {id} - Code: {voucher.Code}", severity: voucher.IsActive ? "Info" : "Warning");

            TempData["Success"] = voucher.IsActive ? "Đã kích hoạt Voucher!" : "Đã khóa Voucher!";
            return RedirectToAction(nameof(Index));
        }
    }
}