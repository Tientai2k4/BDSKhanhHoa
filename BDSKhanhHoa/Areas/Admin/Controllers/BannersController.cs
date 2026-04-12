using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.IO;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class BannersController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;

        public BannersController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
        }

        public async Task<IActionResult> Index()
        {
            return View(await _context.Banners.OrderBy(b => b.DisplayOrder).ToListAsync());
        }

        public IActionResult Create() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Banner banner, IFormFile imageFile)
        {
            if (imageFile != null)
            {
                // Kiểm tra và tự động tạo thư mục nếu chưa có để tránh lỗi DirectoryNotFoundException
                string wwwRootPath = _hostEnvironment.WebRootPath;
                string folderPath = Path.Combine(wwwRootPath, @"images/banners");
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                string fileName = Guid.NewGuid().ToString() + Path.GetExtension(imageFile.FileName);
                string fullPath = Path.Combine(folderPath, fileName);

                using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(stream);
                }
                banner.ImageURL = "/images/banners/" + fileName;

                _context.Add(banner);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }

            ModelState.AddModelError("ImageURL", "Vui lòng chọn ảnh cho Banner");
            return View(banner);
        }

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();
            var banner = await _context.Banners.FindAsync(id);
            if (banner == null) return NotFound();
            return View(banner);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, Banner banner, IFormFile? imageFile)
        {
            if (id != banner.BannerID) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    if (imageFile != null)
                    {
                        string wwwRootPath = _hostEnvironment.WebRootPath;
                        string folderPath = Path.Combine(wwwRootPath, @"images/banners");

                        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                        // Xóa ảnh cũ nếu có để giải phóng dung lượng (tùy chọn)
                        if (!string.IsNullOrEmpty(banner.ImageURL))
                        {
                            var oldImagePath = Path.Combine(wwwRootPath, banner.ImageURL.TrimStart('/'));
                            if (System.IO.File.Exists(oldImagePath)) System.IO.File.Delete(oldImagePath);
                        }

                        string fileName = Guid.NewGuid().ToString() + Path.GetExtension(imageFile.FileName);
                        string fullPath = Path.Combine(folderPath, fileName);

                        using (var stream = new FileStream(fullPath, FileMode.Create))
                        {
                            await imageFile.CopyToAsync(stream);
                        }
                        banner.ImageURL = "/images/banners/" + fileName;
                    }
                    _context.Update(banner);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!BannerExists(banner.BannerID)) return NotFound();
                    else throw;
                }
                return RedirectToAction(nameof(Index));
            }
            return View(banner);
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var banner = await _context.Banners.FindAsync(id);
            if (banner != null)
            {
                // Xóa file vật lý trước khi xóa bản ghi
                if (!string.IsNullOrEmpty(banner.ImageURL))
                {
                    var imagePath = Path.Combine(_hostEnvironment.WebRootPath, banner.ImageURL.TrimStart('/'));
                    if (System.IO.File.Exists(imagePath)) System.IO.File.Delete(imagePath);
                }
                _context.Banners.Remove(banner);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        private bool BannerExists(int id) => _context.Banners.Any(e => e.BannerID == id);
    }
}