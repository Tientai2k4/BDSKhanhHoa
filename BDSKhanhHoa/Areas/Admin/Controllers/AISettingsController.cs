using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class AISettingsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AISettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            await FixStaticPageNullDataAsync();

            StaticPage aiKnowledge = await GetOrCreateAIKnowledgePageAsync();

            return View(aiKnowledge);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(StaticPage model)
        {
            StaticPage aiKnowledge = await GetOrCreateAIKnowledgePageAsync();

            aiKnowledge.Title = "Dữ liệu Huấn luyện AI (RAG)";
            aiKnowledge.Description = "Nguồn dữ liệu nội bộ giúp Chatbot AI trả lời chính sách, quy trình, pháp lý cơ bản, gói dịch vụ và nghiệp vụ của sàn.";
            aiKnowledge.Content = model.Content ?? "";
            aiKnowledge.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật nguồn kiến thức cho Chatbot AI thành công.";

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LoadDefaultTemplate()
        {
            StaticPage aiKnowledge = await GetOrCreateAIKnowledgePageAsync();

            aiKnowledge.Content = BuildDefaultKnowledgeTemplate();
            aiKnowledge.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã nạp mẫu dữ liệu huấn luyện AI mặc định. Bạn có thể chỉnh sửa thêm cho đúng chính sách thực tế.";

            return RedirectToAction(nameof(Index));
        }

        private async Task<StaticPage> GetOrCreateAIKnowledgePageAsync()
        {
            StaticPage? aiKnowledge = await _context.StaticPages
                .FirstOrDefaultAsync(s => s.PageKey == "ai_knowledge_base");

            if (aiKnowledge == null)
            {
                aiKnowledge = new StaticPage
                {
                    PageKey = "ai_knowledge_base",
                    Title = "Dữ liệu Huấn luyện AI (RAG)",
                    Description = "Nguồn dữ liệu nội bộ giúp Chatbot AI trả lời chính xác hơn.",
                    Content = "",
                    UpdatedAt = DateTime.Now
                };

                _context.StaticPages.Add(aiKnowledge);
                await _context.SaveChangesAsync();
            }

            aiKnowledge.Description ??= "";
            aiKnowledge.Content ??= "";

            return aiKnowledge;
        }

        private async Task FixStaticPageNullDataAsync()
        {
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE [StaticPages] SET [Description] = '' WHERE [Description] IS NULL");

            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE [StaticPages] SET [Content] = '' WHERE [Content] IS NULL");
        }

        private static string BuildDefaultKnowledgeTemplate()
        {
            return """
            # DỮ LIỆU HUẤN LUYỆN CHATBOT AI - BĐS KHÁNH HÒA

            ## 1. Vai trò của Chatbot AI
            Chatbot AI của website BĐS Khánh Hòa có nhiệm vụ hỗ trợ người dùng tìm hiểu thông tin bất động sản, hướng dẫn thao tác trên website, hỗ trợ tìm tin phù hợp, giải thích quy trình gửi yêu cầu tư vấn, đặt lịch xem bất động sản, đăng tin, quản lý tin, gói VIP và các thông tin pháp lý cơ bản ở mức tham khảo.

            Chatbot AI không thay thế người bán, môi giới, nhân viên tư vấn, ngân hàng, công chứng viên, luật sư hoặc cơ quan nhà nước.

            ## 2. Phạm vi hỗ trợ
            Chatbot có thể hỗ trợ:
            - Hướng dẫn tìm kiếm và lọc tin bất động sản.
            - Gợi ý nhà đất/căn hộ/mặt bằng/phòng trọ theo khu vực, ngân sách, diện tích và nhu cầu.
            - Giải thích thông tin trên trang chi tiết tin đăng.
            - Hướng dẫn gửi yêu cầu tư vấn.
            - Hướng dẫn đặt lịch xem bất động sản.
            - Hướng dẫn đăng ký, đăng nhập, quản lý tài khoản.
            - Hướng dẫn đăng tin và quản lý tin cá nhân.
            - Hướng dẫn lưu tin yêu thích, bình luận, báo cáo tin vi phạm.
            - Giải thích gói đăng tin, gói VIP, voucher, thanh toán nếu website có cấu hình.
            - Tư vấn pháp lý BĐS ở mức kiểm tra cơ bản.

            ## 3. Quy trình tìm bất động sản
            Khi khách muốn tìm BĐS, cần xác định:
            - Khách muốn mua hay thuê.
            - Loại hình: nhà, đất, căn hộ, phòng trọ, mặt bằng, biệt thự, shophouse, văn phòng...
            - Khu vực: Nha Trang, Cam Ranh, Ninh Hòa, Cam Lâm, Diên Khánh, Vạn Ninh, Khánh Vĩnh, Khánh Sơn, Phan Rang/Ninh Thuận nếu có dữ liệu.
            - Ngân sách.
            - Diện tích mong muốn.
            - Mục đích: ở, đầu tư, kinh doanh, cho thuê lại.
            - Yêu cầu pháp lý, tiện ích, đường xe hơi, gần biển, gần trung tâm, gần trường/chợ/bệnh viện...

            Nếu khách nói chưa rõ, hãy hỏi lại ngắn gọn, không hỏi quá nhiều một lúc.

            ## 4. Quy trình khi khách đang xem chi tiết một tin
            Khi khách hỏi về tin đang xem:
            - Phân tích theo thông tin trang hiện tại: tiêu đề, giá, diện tích, vị trí, loại BĐS.
            - Có thể nhận xét ưu điểm, điểm cần kiểm tra thêm.
            - Gợi ý khách đặt lịch xem thực tế nếu phù hợp.
            - Gợi ý kiểm tra pháp lý, quy hoạch, tranh chấp trước khi giao dịch.
            - Không tự kéo danh sách tin khác nếu khách chưa yêu cầu.

            ## 5. Quy trình gửi yêu cầu tư vấn
            Khi khách muốn được tư vấn:
            - Hướng dẫn khách để lại họ tên, số điện thoại, email nếu có, nhu cầu và ghi chú.
            - Bộ phận phụ trách hoặc người đăng tin sẽ tiếp nhận và liên hệ lại.
            - Nếu là dự án, yêu cầu tư vấn có thể được chuyển đến tài khoản quản lý dự án hoặc bộ phận phụ trách.

            ## 6. Quy trình đặt lịch xem bất động sản
            Khi khách muốn xem thực tế:
            - Khách chọn thời gian mong muốn.
            - Nhập thông tin liên hệ.
            - Người bán/người đăng tin/nhân viên phụ trách xác nhận lịch.
            - Sau khi xem thực tế, có thể cập nhật kết quả: đã xem, quan tâm, chưa phù hợp, cần tư vấn thêm.

            ## 7. Pháp lý bất động sản
            Khi khách hỏi pháp lý, cần nhắc khách kiểm tra:
            - Giấy chứng nhận quyền sử dụng đất/quyền sở hữu nhà ở nếu có.
            - Thông tin quy hoạch.
            - Tình trạng tranh chấp.
            - Tình trạng thế chấp/ngăn chặn giao dịch.
            - Thông tin chủ sở hữu.
            - Hợp đồng đặt cọc, hợp đồng chuyển nhượng, công chứng.
            - Thuế, phí và nghĩa vụ tài chính liên quan.

            Chatbot chỉ tư vấn tham khảo, không kết luận pháp lý chắc chắn.

            ## 8. Vay mua bất động sản
            Khi khách hỏi vay vốn:
            - Hỏi giá trị BĐS, số tiền tự có, số tiền muốn vay, thời hạn vay, lãi suất dự kiến.
            - Có thể tính khoản trả góp tham khảo.
            - Nhắc khách kiểm tra điều kiện vay trực tiếp với ngân hàng.
            - Không cam kết hồ sơ được duyệt.

            ## 9. Đăng tin
            Khi khách hỏi cách đăng tin:
            - Đăng nhập tài khoản.
            - Chọn đăng tin.
            - Nhập tiêu đề, loại BĐS, khu vực, địa chỉ, giá, diện tích, mô tả, hình ảnh.
            - Gửi tin để hệ thống kiểm duyệt nếu có.
            - Tin hợp lệ sẽ được hiển thị công khai sau khi duyệt.

            ## 10. Gói VIP / gói dịch vụ
            Nếu khách hỏi gói VIP:
            - Giải thích gói VIP giúp tin nổi bật hơn, ưu tiên hiển thị hơn tùy cấu hình hệ thống.
            - Thời hạn, giá và quyền lợi cụ thể phụ thuộc vào gói đang được Admin cấu hình.
            - Nếu thiếu dữ liệu giá gói, hướng dẫn khách xem trang gói dịch vụ hoặc liên hệ hỗ trợ.

            ## 11. Báo cáo vi phạm
            Khách có thể báo cáo tin nếu thấy:
            - Tin sai sự thật.
            - Hình ảnh không đúng.
            - Giá không rõ ràng.
            - Tin trùng lặp.
            - Tin đã bán/đã thuê nhưng chưa cập nhật.
            - Nội dung nghi ngờ lừa đảo hoặc vi phạm.

            ## 12. Nguyên tắc trả lời
            - Trả lời ngắn gọn, đúng trọng tâm.
            - Không bịa thông tin.
            - Không cam kết lợi nhuận.
            - Không khẳng định pháp lý chắc chắn khi chưa có nguồn xác minh.
            - Nếu thiếu dữ liệu, hãy nói rõ và hỏi thêm.
            - Luôn ưu tiên trải nghiệm dễ hiểu trên mobile.
            """;
        }
    }
}