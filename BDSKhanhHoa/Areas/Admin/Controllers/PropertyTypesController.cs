using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class PropertyTypesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertyTypesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Logic Index hiển thị chia 2 nhóm rõ rệt
        public async Task<IActionResult> Index()
        {
            var allTypes = await _context.PropertyTypes.ToListAsync();
            // Truyền dữ liệu đã phân nhóm qua ViewBag để View hiển thị đẹp hơn
            ViewBag.BuyGroup = allTypes.Where(t => t.ParentID == 1 || t.TypeID == 1).ToList();
            ViewBag.RentGroup = allTypes.Where(t => t.ParentID == 2 || t.TypeID == 2).ToList();

            return View(allTypes);
        }

        // GET: Create
        public IActionResult Create(int? parentId) // Nhận thêm tham số parentId từ URL
        {
            var parents = _context.PropertyTypes.Where(t => t.ParentID == null).ToList();

            // Nếu có parentId truyền vào (từ nút bấm ở Index), chọn sẵn cho Admin
            ViewBag.ParentID = new SelectList(parents, "TypeID", "TypeName", parentId);

            return View();
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("TypeID,TypeName,Description,ParentID")] PropertyType propertyType)
        {
            if (ModelState.IsValid)
            {
                _context.Add(propertyType);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewBag.ParentID = new SelectList(_context.PropertyTypes.Where(t => t.ParentID == null), "TypeID", "TypeName", propertyType.ParentID);
            return View(propertyType);
        }

        // GET: Chỉnh sửa
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var propertyType = await _context.PropertyTypes.FindAsync(id);
            if (propertyType == null) return NotFound();

            ViewBag.ParentID = new SelectList(_context.PropertyTypes.Where(t => t.ParentID == null && t.TypeID != id), "TypeID", "TypeName", propertyType.ParentID);
            return View(propertyType);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("TypeID,TypeName,Description,ParentID")] PropertyType propertyType)
        {
            if (id != propertyType.TypeID) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(propertyType);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!PropertyTypeExists(propertyType.TypeID)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(propertyType);
        }

        // GET: Xóa
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var propertyType = await _context.PropertyTypes
                .FirstOrDefaultAsync(m => m.TypeID == id);
            if (propertyType == null) return NotFound();

            return View(propertyType);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var propertyType = await _context.PropertyTypes
                .Include(t => t.Properties)
                .FirstOrDefaultAsync(m => m.TypeID == id);

            if (propertyType != null)
            {
                // 1. Kiểm tra danh mục con (Tránh lỗi FK_PropertyTypes_Parent)
                bool hasChildren = await _context.PropertyTypes.AnyAsync(t => t.ParentID == id);
                if (hasChildren)
                {
                    TempData["Error"] = "Không thể xóa! Danh mục này đang chứa các loại hình con bên trong. Hãy xóa các loại hình con trước.";
                    return RedirectToAction(nameof(Delete), new { id = id });
                }

                // 2. Kiểm tra dữ liệu tin đăng (Ràng buộc nghiệp vụ)
                if (propertyType.Properties != null && propertyType.Properties.Any())
                {
                    TempData["Error"] = "Không thể xóa! Đã có tin đăng thực tế thuộc loại hình này.";
                    return RedirectToAction(nameof(Delete), new { id = id });
                }

                _context.PropertyTypes.Remove(propertyType);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa loại hình thành công.";
            }

            return RedirectToAction(nameof(Index));
        }
        private bool PropertyTypeExists(int id)
        {
            return _context.PropertyTypes.Any(e => e.TypeID == id);
        }
    }
}