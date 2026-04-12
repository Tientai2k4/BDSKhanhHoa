using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class AreasController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AreasController(ApplicationDbContext context)
        {
            _context = context;
        }

        // 1. Hiển thị danh sách
        public async Task<IActionResult> Index()
        {
            var areas = await _context.Areas.Include(a => a.Wards).ToListAsync();
            return View(areas);
        }

        // 2. Tạo mới Khu vực (Cha)
        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Area area)
        {
            // Hệ thống sẽ tự động kiểm tra AreaName dựa trên [Required] ở Model
            if (ModelState.IsValid)
            {
                _context.Add(area);
                await _context.SaveChangesAsync();
                TempData["Success"] = $"Đã thêm khu vực {area.AreaName} thành công!";
                return RedirectToAction(nameof(Index));
            }

            // Nếu có lỗi, trả về View kèm thông báo
            TempData["Error"] = "Vui lòng kiểm tra lại thông tin nhập liệu.";
            return View(area);
        }

        // 3. Chỉnh sửa & Quản lý Xã (Con)
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var area = await _context.Areas.Include(a => a.Wards).FirstOrDefaultAsync(m => m.AreaID == id);
            if (area == null) return NotFound();
            return View(area);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("AreaID,AreaName,Description")] Area area)
        {
            if (id != area.AreaID) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(area);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Cập nhật thành công!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!_context.Areas.Any(e => e.AreaID == area.AreaID)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(area);
        }

        // 4. Xóa Khu vực
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();
            var area = await _context.Areas.Include(a => a.Wards).FirstOrDefaultAsync(m => m.AreaID == id);
            if (area == null) return NotFound();
            return View(area);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var area = await _context.Areas.Include(a => a.Wards).FirstOrDefaultAsync(m => m.AreaID == id);
            if (area != null)
            {
                if (area.Wards != null && area.Wards.Any())
                {
                    _context.Wards.RemoveRange(area.Wards); // Xóa sạch xã con
                }
                _context.Areas.Remove(area);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa khu vực vĩnh viễn.";
            }
            return RedirectToAction(nameof(Index));
        }

        // 5. Quản lý Xã (Thêm/Xóa nhanh)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWard(int AreaID, string WardName)
        {
            if (!string.IsNullOrEmpty(WardName))
            {
                var ward = new Ward { AreaID = AreaID, WardName = WardName };
                _context.Wards.Add(ward);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã thêm xã mới.";
            }
            return RedirectToAction(nameof(Edit), new { id = AreaID });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteWard(int id)
        {
            var ward = await _context.Wards.FindAsync(id);
            if (ward != null)
            {
                int areaId = ward.AreaID;
                _context.Wards.Remove(ward);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Edit), new { id = areaId });
            }
            return RedirectToAction(nameof(Index));
        }
    }
}