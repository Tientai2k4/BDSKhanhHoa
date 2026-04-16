using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // Chỉ Admin mới được chỉnh sửa bảng giá
    public class PostServicePackagesController : Controller
    {
        private readonly ApplicationDbContext _context;

        // Định nghĩa sẵn các hạng mục chuẩn của hệ thống BĐS
        private readonly List<string> _packageTypes = new List<string>
        {
            "Kim Cương", "Vàng", "Bạc", "Đồng", "Tin Thường"
        };

        public PostServicePackagesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // 1. DANH SÁCH GÓI DỊCH VỤ
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var packages = await _context.PostServicePackages
                .OrderByDescending(p => p.PriorityLevel)
                .ToListAsync();
            return View(packages);
        }

        // ==========================================
        // 2. THÊM GÓI DỊCH VỤ MỚI
        // ==========================================
        [HttpGet]
        public IActionResult Create()
        {
            ViewBag.PackageTypes = new SelectList(_packageTypes);
            return View(new PostServicePackage());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PostServicePackage package)
        {
            if (ModelState.IsValid)
            {
                if (await _context.PostServicePackages.AnyAsync(p => p.PackageName.ToLower() == package.PackageName.ToLower()))
                {
                    ModelState.AddModelError("PackageName", "Tên gói này đã tồn tại trên hệ thống!");
                    ViewBag.PackageTypes = new SelectList(_packageTypes);
                    return View(package);
                }

                _context.PostServicePackages.Add(package);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã thêm Gói dịch vụ mới thành công!";
                return RedirectToAction(nameof(Index));
            }
            ViewBag.PackageTypes = new SelectList(_packageTypes);
            return View(package);
        }

        // ==========================================
        // 3. CHỈNH SỬA GÓI DỊCH VỤ
        // ==========================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var package = await _context.PostServicePackages.FindAsync(id);
            if (package == null)
            {
                TempData["Error"] = "Không tìm thấy Gói dịch vụ!";
                return RedirectToAction(nameof(Index));
            }

            ViewBag.PackageTypes = new SelectList(_packageTypes);
            return View(package);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, PostServicePackage package)
        {
            if (id != package.PackageID) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    if (await _context.PostServicePackages.AnyAsync(p => p.PackageName.ToLower() == package.PackageName.ToLower() && p.PackageID != id))
                    {
                        ModelState.AddModelError("PackageName", "Tên gói này đã tồn tại trên hệ thống!");
                        ViewBag.PackageTypes = new SelectList(_packageTypes);
                        return View(package);
                    }

                    _context.Update(package);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Đã cập nhật cấu hình Gói dịch vụ!";
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!PackageExists(package.PackageID)) return NotFound();
                    else throw;
                }
            }
            ViewBag.PackageTypes = new SelectList(_packageTypes);
            return View(package);
        }

        // ==========================================
        // 4. XÓA GÓI DỊCH VỤ
        // ==========================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var package = await _context.PostServicePackages.FindAsync(id);
            if (package != null)
            {
                // Kiểm tra xem có tin BĐS nào đang dùng gói này không
                bool isUsed = await _context.Properties.AnyAsync(p => p.PackageID == id);
                if (isUsed)
                {
                    TempData["Error"] = "Không thể xóa! Đang có tin Bất động sản sử dụng gói dịch vụ này.";
                    return RedirectToAction(nameof(Index));
                }

                _context.PostServicePackages.Remove(package);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa Gói dịch vụ thành công!";
            }
            return RedirectToAction(nameof(Index));
        }

        private bool PackageExists(int id)
        {
            return _context.PostServicePackages.Any(e => e.PackageID == id);
        }
    }
}