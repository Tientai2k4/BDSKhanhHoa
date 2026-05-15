using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class VoucherController : Controller
    {
        private readonly ApplicationDbContext _context;

        public VoucherController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> MyVouchers()
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

            // 1. Lấy lịch sử tất cả các mã Voucher người dùng này đã từng xài
            // Thủ thuật: Đọc từ cột Description có chứa chuỗi [Voucher:CODE]
            var usedVoucherDescriptions = await _context.Transactions
                .Where(t => t.UserID == userId && t.Description != null && t.Description.Contains("[Voucher:") && t.Status != "Cancelled")
                .Select(t => t.Description)
                .ToListAsync();

            var usedCodes = usedVoucherDescriptions
                .Select(desc => ExtractVoucherCode(desc))
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();

            // 2. Lấy toàn bộ danh sách Voucher trên hệ thống
            var vouchers = await _context.Vouchers
                .OrderByDescending(v => v.IsActive)
                .ThenByDescending(v => v.ExpiryDate)
                .ToListAsync();

            // Truyền danh sách mã đã dùng sang View để xử lý UI (Làm mờ, vô hiệu hóa)
            ViewBag.UsedCodes = usedCodes;

            return View(vouchers);
        }

        // Hàm tiện ích bóc tách mã CODE từ chuỗi "[Voucher:TET2026] Mua lượt đăng"
        private string ExtractVoucherCode(string desc)
        {
            try
            {
                int start = desc.IndexOf("[Voucher:") + 9;
                int end = desc.IndexOf("]", start);
                if (start >= 9 && end > start)
                {
                    return desc.Substring(start, end - start).ToUpper();
                }
            }
            catch { }
            return "";
        }
    }
}