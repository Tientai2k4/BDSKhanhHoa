using BDSKhanhHoa.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Controllers
{
    [Authorize]
    public class PackageController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PackageController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Buy()
        {
            /*
                CHUẨN NGHIỆP VỤ:
                - Admin bấm "Ngừng dùng" thì IsActive = false.
                - Gói IsActive = false không được hiển thị ở trang mua gói.
                - Không cho người dùng thêm giỏ hàng / thanh toán mới với gói đã ngừng.
                - Lịch sử giao dịch cũ vẫn giữ nguyên ở Transaction.
            */

            var packages = await _context.PostServicePackages
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.PriorityLevel)
                .ThenBy(p => p.Price)
                .ToListAsync();

            return View(packages);
        }
    }
}