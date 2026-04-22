// Controllers/PaymentController.cs
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class PaymentController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PaymentController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==================================================
        // 1. TRANG THANH TOÁN (CHECKOUT)
        // ==================================================
        [HttpGet]
        public async Task<IActionResult> Checkout(int packageId)
        {
            var package = await _context.PostServicePackages.FindAsync(packageId);
            if (package == null)
            {
                TempData["Error"] = "Gói dịch vụ không tồn tại!";
                return RedirectToAction("Buy", "Package");
            }

            return View(package);
        }

        // ==================================================
        // 2. API KIỂM TRA MÃ GIẢM GIÁ VÀ SỐ LƯỢNG (AJAX)
        // ==================================================
        [HttpPost]
        public async Task<IActionResult> ApplyVoucher([FromBody] VoucherRequest req)
        {
            if (req.Quantity <= 0) req.Quantity = 1;

            var package = await _context.PostServicePackages.FindAsync(req.PackageId);
            if (package == null) return Json(new { success = false, message = "Lỗi dữ liệu gói tin." });

            decimal totalBeforeDiscount = package.Price * req.Quantity;

            if (string.IsNullOrEmpty(req.Code))
            {
                return Json(new { success = true, discountAmount = 0, finalPrice = totalBeforeDiscount, message = "" });
            }

            var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Code.ToLower() == req.Code.ToLower());

            if (voucher == null || !voucher.IsActive || voucher.ExpiryDate < DateTime.Now || voucher.UsedCount >= voucher.Quantity)
            {
                return Json(new { success = false, message = "Mã giảm giá không hợp lệ hoặc đã hết lượt." });
            }

            decimal discountAmount = (totalBeforeDiscount * voucher.DiscountPercent) / 100;
            if (discountAmount > voucher.MaxDiscountAmount) discountAmount = voucher.MaxDiscountAmount;

            decimal finalPrice = totalBeforeDiscount - discountAmount;
            if (finalPrice < 0) finalPrice = 0;

            return Json(new
            {
                success = true,
                discountAmount = discountAmount,
                finalPrice = finalPrice,
                message = $"Áp dụng thành công! Giảm {voucher.DiscountPercent}%"
            });
        }

        public class VoucherRequest
        {
            public string Code { get; set; }
            public int PackageId { get; set; }
            public int Quantity { get; set; }
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPayment(int packageId, int quantity, string paymentMethod, string? voucherCode)
        {
            // Đảm bảo số lượng luôn hợp lệ
            if (quantity <= 0) quantity = 1;

            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var package = await _context.PostServicePackages.FindAsync(packageId);

            if (package == null)
            {
                TempData["Error"] = "Lỗi dữ liệu. Vui lòng thử lại!";
                return RedirectToAction("Buy", "Package");
            }

            decimal totalBeforeDiscount = package.Price * quantity;
            decimal finalPrice = totalBeforeDiscount;

            // Xử lý trừ lượt Voucher (nếu có)
            if (!string.IsNullOrEmpty(voucherCode))
            {
                var voucher = await _context.Vouchers.FirstOrDefaultAsync(v =>
                    v.Code.ToLower() == voucherCode.ToLower() &&
                    v.IsActive &&
                    v.ExpiryDate >= DateTime.Now &&
                    v.UsedCount < v.Quantity);

                if (voucher != null)
                {
                    decimal discount = (totalBeforeDiscount * voucher.DiscountPercent) / 100;
                    if (discount > voucher.MaxDiscountAmount) discount = voucher.MaxDiscountAmount;
                    finalPrice -= discount;
                    if (finalPrice < 0) finalPrice = 0;

                    voucher.UsedCount += 1;
                    _context.Update(voucher);
                }
            }

            // =================================================================
            // FIX LỖI Ở ĐÂY: Dùng vòng lặp tạo ra N dòng tương ứng với Số lượng
            // =================================================================
            decimal pricePerItem = finalPrice / quantity; // Tính đơn giá sau khi đã giảm
            string txCodeBase = "TXN" + DateTime.Now.ToString("yyyyMMddHHmmss") + userId;

            for (int i = 0; i < quantity; i++)
            {
                var transaction = new Transaction
                {
                    UserID = userId,
                    PackageID = package.PackageID,
                    PropertyID = null, // Null nghĩa là Lượt đăng này CHƯA SỬ DỤNG
                    Quantity = 1,      // Gán cứng = 1 để tương thích với logic đếm
                    Amount = pricePerItem,
                    PaymentMethod = paymentMethod,
                    TransactionCode = txCodeBase + "_" + i, // Thêm index để mã không trùng
                    Status = "Success",
                    Type = "Mua lượt đăng",
                    CreatedAt = DateTime.Now
                };

                _context.Transactions.Add(transaction);
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = $"Thanh toán thành công! Bạn vừa nhận được {quantity} lượt đăng gói [{package.PackageName}].";

            return RedirectToAction("History", "Payment");
        }
        // ==================================================
        // 4. LỊCH SỬ GIAO DỊCH
        // ==================================================
        [HttpGet]
        public async Task<IActionResult> History()
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
            var transactions = await _context.Transactions
                .Include(t => t.Package)
                .Include(t => t.Property)
                .Where(t => t.UserID == userId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();

            return View(transactions);
        }
    }
}