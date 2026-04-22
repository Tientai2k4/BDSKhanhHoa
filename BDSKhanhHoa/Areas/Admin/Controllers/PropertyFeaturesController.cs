using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class PropertyFeaturesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PropertyFeaturesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Tạo sẵn các Lớp cha (FeatureGroup) chuẩn để Admin chọn
        private List<SelectListItem> GetFeatureGroups()
        {
            return new List<SelectListItem>
            {
                new SelectListItem { Value = "Hướng nhà", Text = "Hướng nhà" },
                new SelectListItem { Value = "Pháp lý", Text = "Giấy tờ Pháp lý" },
                new SelectListItem { Value = "Tiện ích", Text = "Tiện ích nổi bật (Hồ bơi, Gara...)" },
                new SelectListItem { Value = "Khác", Text = "Đặc điểm Khác" }
            };
        }

        [HttpGet]
        public async Task<IActionResult> Index(string keyword = "")
        {
            // ADMIN CHỈ QUẢN LÝ DỮ LIỆU GỐC (PropertyID == null)
            var query = _context.PropertyFeatures
                .Where(f => f.PropertyID == null)
                .AsQueryable();

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(f => f.FeatureName.Contains(keyword) || f.FeatureGroup.Contains(keyword));
            }

            // Sắp xếp theo Lớp cha trước, Lớp con sau để View dễ nhóm dữ liệu
            var features = await query
                .OrderBy(f => f.FeatureGroup)
                .ThenBy(f => f.FeatureName)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.FeatureGroups = GetFeatureGroups();
            return View(features);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PropertyFeature model)
        {
            // Ép buộc PropertyID = null vì đây là Master Data của Admin tạo ra
            model.PropertyID = null;

            _context.PropertyFeatures.Add(model);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Đã thêm danh mục tiện ích mới thành công!";

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(PropertyFeature model)
        {
            var existing = await _context.PropertyFeatures.FindAsync(model.FeatureID);

            // Chỉ cho phép sửa nếu đó là dữ liệu mẫu (PropertyID == null)
            if (existing != null && existing.PropertyID == null)
            {
                existing.FeatureGroup = model.FeatureGroup;
                existing.FeatureName = model.FeatureName;
                existing.FeatureValue = model.FeatureValue;

                _context.PropertyFeatures.Update(existing);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Cập nhật danh mục thành công!";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var feature = await _context.PropertyFeatures.FindAsync(id);

            if (feature != null && feature.PropertyID == null)
            {
                _context.PropertyFeatures.Remove(feature);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa danh mục thành công! Client sẽ không còn thấy tùy chọn này.";
            }
            return RedirectToAction(nameof(Index));
        }
    }
}