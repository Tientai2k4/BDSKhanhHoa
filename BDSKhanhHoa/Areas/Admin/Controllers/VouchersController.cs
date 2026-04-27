using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class VouchersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public VouchersController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var vouchers = await _context.Vouchers.OrderByDescending(v => v.CreatedAt).ToListAsync();

            ViewBag.TotalVouchers = vouchers.Count;
            ViewBag.ActiveVouchers = vouchers.Count(v => v.IsActive && v.ExpiryDate >= DateTime.Now);
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
                // Kiểm tra trùng mã
                if (await _context.Vouchers.AnyAsync(v => v.Code == voucher.Code))
                {
                    TempData["Error"] = "Mã Voucher này đã tồn tại!";
                    return RedirectToAction(nameof(Index));
                }

                voucher.CreatedAt = DateTime.Now;
                voucher.UsedCount = 0;
                voucher.IsActive = true;

                _context.Vouchers.Add(voucher);
                await _context.SaveChangesAsync();
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

            TempData["Success"] = voucher.IsActive ? "Đã kích hoạt Voucher!" : "Đã khóa Voucher!";
            return RedirectToAction(nameof(Index));
        }
    }
}