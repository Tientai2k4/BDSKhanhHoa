using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class AreasController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AreasController(ApplicationDbContext context)
        {
            _context = context;
        }

        // =====================================================
        // 1. DANH SÁCH KHU VỰC
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var areas = await _context.Areas
                .AsNoTracking()
                .Include(a => a.Wards)
                .OrderBy(a => a.AreaName)
                .ToListAsync();

            return View(areas);
        }

        // =====================================================
        // 2. TẠO KHU VỰC
        // Dùng string AreaName, string? Description để tránh lỗi bind model.
        // =====================================================
        [HttpGet]
        public IActionResult Create()
        {
            return View(new Area());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string AreaName, string? Description)
        {
            AreaName = (AreaName ?? "").Trim();
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();

            if (string.IsNullOrWhiteSpace(AreaName))
            {
                ModelState.AddModelError("AreaName", "Tên khu vực là bắt buộc.");
                TempData["Error"] = "Vui lòng nhập tên khu vực.";

                return View(new Area
                {
                    AreaName = AreaName,
                    Description = Description
                });
            }

            if (AreaName.Length > 100)
            {
                ModelState.AddModelError("AreaName", "Tên khu vực không được vượt quá 100 ký tự.");
                TempData["Error"] = "Tên khu vực quá dài.";

                return View(new Area
                {
                    AreaName = AreaName,
                    Description = Description
                });
            }

            if (!string.IsNullOrWhiteSpace(Description) && Description.Length > 255)
            {
                ModelState.AddModelError("Description", "Mô tả không được vượt quá 255 ký tự.");
                TempData["Error"] = "Mô tả quá dài.";

                return View(new Area
                {
                    AreaName = AreaName,
                    Description = Description
                });
            }

            bool isDuplicate = await _context.Areas
                .AsNoTracking()
                .AnyAsync(a =>
                    a.AreaName != null &&
                    a.AreaName.ToLower() == AreaName.ToLower());

            if (isDuplicate)
            {
                ModelState.AddModelError("AreaName", "Tên khu vực này đã tồn tại.");
                TempData["Error"] = "Tên khu vực đã tồn tại trong hệ thống.";

                return View(new Area
                {
                    AreaName = AreaName,
                    Description = Description
                });
            }

            var newArea = new Area
            {
                AreaName = AreaName,
                Description = Description
            };

            _context.Areas.Add(newArea);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã thêm khu vực \"{newArea.AreaName}\" thành công. Bây giờ bạn có thể thêm xã/phường trực thuộc.";

            return RedirectToAction(nameof(Edit), new { id = newArea.AreaID });
        }

        // =====================================================
        // 3. SỬA KHU VỰC + QUẢN LÝ XÃ/PHƯỜNG
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id.Value <= 0)
            {
                return NotFound();
            }

            var area = await _context.Areas
                .Include(a => a.Wards)
                .FirstOrDefaultAsync(a => a.AreaID == id.Value);

            if (area == null)
            {
                return NotFound();
            }

            area.Wards = area.Wards
                .OrderBy(w => w.WardName)
                .ToList();

            return View(area);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, string AreaName, string? Description)
        {
            AreaName = (AreaName ?? "").Trim();
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();

            var existingArea = await _context.Areas
                .Include(a => a.Wards)
                .FirstOrDefaultAsync(a => a.AreaID == id);

            if (existingArea == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(AreaName))
            {
                ModelState.AddModelError("AreaName", "Tên khu vực là bắt buộc.");
                TempData["Error"] = "Vui lòng nhập tên khu vực.";

                existingArea.AreaName = AreaName;
                existingArea.Description = Description;
                existingArea.Wards = existingArea.Wards.OrderBy(w => w.WardName).ToList();

                return View(existingArea);
            }

            if (AreaName.Length > 100)
            {
                ModelState.AddModelError("AreaName", "Tên khu vực không được vượt quá 100 ký tự.");
                TempData["Error"] = "Tên khu vực quá dài.";

                existingArea.AreaName = AreaName;
                existingArea.Description = Description;
                existingArea.Wards = existingArea.Wards.OrderBy(w => w.WardName).ToList();

                return View(existingArea);
            }

            if (!string.IsNullOrWhiteSpace(Description) && Description.Length > 255)
            {
                ModelState.AddModelError("Description", "Mô tả không được vượt quá 255 ký tự.");
                TempData["Error"] = "Mô tả quá dài.";

                existingArea.AreaName = AreaName;
                existingArea.Description = Description;
                existingArea.Wards = existingArea.Wards.OrderBy(w => w.WardName).ToList();

                return View(existingArea);
            }

            bool isDuplicate = await _context.Areas
                .AsNoTracking()
                .AnyAsync(a =>
                    a.AreaID != id &&
                    a.AreaName != null &&
                    a.AreaName.ToLower() == AreaName.ToLower());

            if (isDuplicate)
            {
                ModelState.AddModelError("AreaName", "Tên khu vực này đã tồn tại.");
                TempData["Error"] = "Tên khu vực đã tồn tại trong hệ thống.";

                existingArea.AreaName = AreaName;
                existingArea.Description = Description;
                existingArea.Wards = existingArea.Wards.OrderBy(w => w.WardName).ToList();

                return View(existingArea);
            }

            existingArea.AreaName = AreaName;
            existingArea.Description = Description;

            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã cập nhật khu vực \"{existingArea.AreaName}\" thành công.";

            return RedirectToAction(nameof(Edit), new { id = existingArea.AreaID });
        }

        // =====================================================
        // 4. XÓA KHU VỰC
        // Không cho xóa nếu khu vực/xã phường đang có tin BĐS hoặc dự án dùng.
        // =====================================================
        [HttpGet]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null || id.Value <= 0)
            {
                return NotFound();
            }

            var area = await _context.Areas
                .AsNoTracking()
                .Include(a => a.Wards)
                .FirstOrDefaultAsync(a => a.AreaID == id.Value);

            if (area == null)
            {
                return NotFound();
            }

            var wardIds = area.Wards?
                .Select(w => w.WardID)
                .ToList() ?? new List<int>();

            int propertyCount = 0;
            int projectCount = 0;

            if (wardIds.Any())
            {
                propertyCount = await _context.Properties
                    .AsNoTracking()
                    .CountAsync(p => wardIds.Contains(p.WardID));

                projectCount = await _context.Projects
                    .AsNoTracking()
                    .CountAsync(p => p.AreaID == area.AreaID || wardIds.Contains(p.WardID));
            }
            else
            {
                projectCount = await _context.Projects
                    .AsNoTracking()
                    .CountAsync(p => p.AreaID == area.AreaID);
            }

            ViewBag.PropertyCount = propertyCount;
            ViewBag.ProjectCount = projectCount;
            ViewBag.CanDelete = propertyCount == 0 && projectCount == 0;

            return View(area);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var area = await _context.Areas
                .Include(a => a.Wards)
                .FirstOrDefaultAsync(a => a.AreaID == id);

            if (area == null)
            {
                TempData["Error"] = "Không tìm thấy khu vực cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            var wardIds = area.Wards?
                .Select(w => w.WardID)
                .ToList() ?? new List<int>();

            int propertyCount = 0;
            int projectCount = 0;

            if (wardIds.Any())
            {
                propertyCount = await _context.Properties
                    .AsNoTracking()
                    .CountAsync(p => wardIds.Contains(p.WardID));

                projectCount = await _context.Projects
                    .AsNoTracking()
                    .CountAsync(p => p.AreaID == area.AreaID || wardIds.Contains(p.WardID));
            }
            else
            {
                projectCount = await _context.Projects
                    .AsNoTracking()
                    .CountAsync(p => p.AreaID == area.AreaID);
            }

            if (propertyCount > 0 || projectCount > 0)
            {
                TempData["Error"] =
                    $"Không thể xóa khu vực \"{area.AreaName}\" vì đang có {propertyCount} tin bất động sản và {projectCount} dự án sử dụng khu vực/xã phường này. " +
                    "Vui lòng chuyển dữ liệu liên quan sang khu vực hoặc xã/phường khác trước.";

                return RedirectToAction(nameof(Edit), new { id = area.AreaID });
            }

            try
            {
                if (area.Wards != null && area.Wards.Any())
                {
                    _context.Wards.RemoveRange(area.Wards);
                }

                _context.Areas.Remove(area);
                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã xóa khu vực \"{area.AreaName}\" thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException)
            {
                TempData["Error"] =
                    $"Không thể xóa khu vực \"{area.AreaName}\" vì dữ liệu này vẫn đang được bảng khác tham chiếu. " +
                    "Hãy kiểm tra lại tin đăng, dự án hoặc dữ liệu liên quan trước khi xóa.";

                return RedirectToAction(nameof(Edit), new { id = area.AreaID });
            }
        }

        // =====================================================
        // 5. THÊM XÃ/PHƯỜNG
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWard(int AreaID, string WardName)
        {
            WardName = (WardName ?? "").Trim();

            var area = await _context.Areas
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AreaID == AreaID);

            if (area == null)
            {
                TempData["Error"] = "Khu vực không tồn tại.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(WardName))
            {
                TempData["Error"] = "Tên xã/phường không được để trống.";
                return RedirectToAction(nameof(Edit), new { id = AreaID });
            }

            if (WardName.Length > 100)
            {
                TempData["Error"] = "Tên xã/phường không được vượt quá 100 ký tự.";
                return RedirectToAction(nameof(Edit), new { id = AreaID });
            }

            bool isDuplicate = await _context.Wards
                .AsNoTracking()
                .AnyAsync(w =>
                    w.AreaID == AreaID &&
                    w.WardName != null &&
                    w.WardName.ToLower() == WardName.ToLower());

            if (isDuplicate)
            {
                TempData["Error"] = $"Xã/phường \"{WardName}\" đã tồn tại trong khu vực này.";
                return RedirectToAction(nameof(Edit), new { id = AreaID });
            }

            var ward = new Ward
            {
                AreaID = AreaID,
                WardName = WardName
            };

            _context.Wards.Add(ward);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã thêm xã/phường \"{WardName}\" thành công.";

            return RedirectToAction(nameof(Edit), new { id = AreaID });
        }

        // =====================================================
        // 6. XÓA XÃ/PHƯỜNG
        // Không cho xóa nếu xã/phường đang có tin BĐS hoặc dự án dùng.
        // =====================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWard(int id)
        {
            var ward = await _context.Wards
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.WardID == id);

            if (ward == null)
            {
                TempData["Error"] = "Không tìm thấy xã/phường cần xóa.";
                return RedirectToAction(nameof(Index));
            }

            int areaId = ward.AreaID;

            int propertyCount = await _context.Properties
                .AsNoTracking()
                .CountAsync(p => p.WardID == ward.WardID);

            int projectCount = await _context.Projects
                .AsNoTracking()
                .CountAsync(p => p.WardID == ward.WardID);

            if (propertyCount > 0 || projectCount > 0)
            {
                TempData["Error"] =
                    $"Không thể xóa xã/phường \"{ward.WardName}\" vì đang có {propertyCount} tin bất động sản và {projectCount} dự án sử dụng xã/phường này. " +
                    "Vui lòng chuyển tin/dự án sang xã/phường khác trước.";

                return RedirectToAction(nameof(Edit), new { id = areaId });
            }

            try
            {
                var wardToDelete = await _context.Wards
                    .FirstOrDefaultAsync(w => w.WardID == id);

                if (wardToDelete == null)
                {
                    TempData["Error"] = "Không tìm thấy xã/phường cần xóa.";
                    return RedirectToAction(nameof(Index));
                }

                _context.Wards.Remove(wardToDelete);
                await _context.SaveChangesAsync();

                TempData["Success"] = $"Đã xóa xã/phường \"{wardToDelete.WardName}\" thành công.";

                return RedirectToAction(nameof(Edit), new { id = areaId });
            }
            catch (DbUpdateException)
            {
                TempData["Error"] =
                    $"Không thể xóa xã/phường \"{ward.WardName}\" vì dữ liệu này vẫn đang được bảng khác tham chiếu. " +
                    "Hãy chuyển hoặc xóa dữ liệu liên quan trước khi xóa xã/phường.";

                return RedirectToAction(nameof(Edit), new { id = areaId });
            }
        }
    }
}