using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Services
{
    public class ChatbotService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        public ChatbotService(ApplicationDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
            // Tăng thời gian Timeout để tránh lỗi "Lỗi kết nối" khi AI phản hồi chậm
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            string userMessage = req.Message.ToLower();

            // =========================================================================
            // BƯỚC 1: XÁC ĐỊNH NGỮ CẢNH TRANG HIỆN TẠI VÀ Ý ĐỊNH CỦA KHÁCH HÀNG
            // =========================================================================
            bool isViewingProperty = !string.IsNullOrWhiteSpace(req.PageContext);
            bool isAskingAboutCurrent = isViewingProperty &&
                (userMessage.Contains("này") || userMessage.Contains("đây") || userMessage.Contains("chỗ đó") || userMessage.Contains("căn trên"));

            List<Property> relevantProperties = new List<Property>();

            // =========================================================================
            // BƯỚC 2: TÌM KIẾM THÔNG MINH (Chỉ tìm BĐS khác nếu khách có nhu cầu tìm kiếm)
            // =========================================================================
            if (!isAskingAboutCurrent || userMessage.Contains("khác") || userMessage.Contains("tìm"))
            {
                var query = _context.Properties
                    .Include(p => p.Ward).ThenInclude(w => w.Area)
                    .Include(p => p.PropertyType)
                    .Where(p => p.Status == "Approved" && p.IsDeleted == false)
                    .AsQueryable();

                bool hasFilter = false;

                // 2.1. Phân tích loại Giao dịch
                if (userMessage.Contains("thuê") || userMessage.Contains("mướn"))
                {
                    query = query.Where(p => p.PropertyType.ParentID == 2 || p.TypeID == 2 || p.PropertyType.TypeName.ToLower().Contains("thuê"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("mua") || userMessage.Contains("bán"))
                {
                    query = query.Where(p => p.PropertyType.ParentID == 1 || p.TypeID == 1 || p.PropertyType.TypeName.ToLower().Contains("bán"));
                    hasFilter = true;
                }

                // 2.2. Phân tích loại hình Bất động sản
                if (userMessage.Contains("căn hộ") || userMessage.Contains("chung cư"))
                {
                    query = query.Where(p => p.PropertyType.TypeName.ToLower().Contains("căn hộ") || p.PropertyType.TypeName.ToLower().Contains("chung cư"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("đất"))
                {
                    query = query.Where(p => p.PropertyType.TypeName.ToLower().Contains("đất"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("nhà") || userMessage.Contains("biệt thự"))
                {
                    query = query.Where(p => p.PropertyType.TypeName.ToLower().Contains("nhà") || p.PropertyType.TypeName.ToLower().Contains("biệt thự"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("mặt bằng") || userMessage.Contains("kinh doanh"))
                {
                    query = query.Where(p => p.PropertyType.TypeName.ToLower().Contains("mặt bằng") || p.PropertyType.TypeName.ToLower().Contains("kinh doanh"));
                    hasFilter = true;
                }

                // 2.3. Phân tích Khu vực
                if (userMessage.Contains("nha trang"))
                {
                    query = query.Where(p => p.Ward.Area.AreaName.ToLower().Contains("nha trang") || p.AddressDetail.ToLower().Contains("nha trang"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("cam ranh"))
                {
                    query = query.Where(p => p.Ward.Area.AreaName.ToLower().Contains("cam ranh") || p.AddressDetail.ToLower().Contains("cam ranh"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("ninh hòa"))
                {
                    query = query.Where(p => p.Ward.Area.AreaName.ToLower().Contains("ninh hòa") || p.AddressDetail.ToLower().Contains("ninh hòa"));
                    hasFilter = true;
                }
                else if (userMessage.Contains("vạn ninh"))
                {
                    query = query.Where(p => p.Ward.Area.AreaName.ToLower().Contains("vạn ninh") || p.AddressDetail.ToLower().Contains("vạn ninh"));
                    hasFilter = true;
                }

                // Nếu có áp dụng bộ lọc (nghĩa là AI đoán được khách đang tìm BĐS)
                if (hasFilter)
                {
                    relevantProperties = await query
                        .OrderByDescending(p => p.PackageID) // Ưu tiên trả về tin VIP trước
                        .ThenByDescending(p => p.CreatedAt)
                        .Take(4) // Trả về tối đa 4 BĐS khớp nhất để thẻ UI không bị dài
                        .ToListAsync();
                }
            }

            // Xây dựng chuỗi thông tin BĐS gợi ý (nếu có)
            string realEstateContext = "";
            if (relevantProperties.Any())
            {
                realEstateContext = "[DANH SÁCH BẤT ĐỘNG SẢN PHÙ HỢP VỚI NHU CẦU ĐỂ BẠN GỢI Ý CHO KHÁCH]:\n" +
                    string.Join("\n", relevantProperties.Select(p =>
                    $"- Tên: {p.Title} | Giá: {p.Price:N0} VNĐ | Vị trí: {p.Ward?.WardName} | Link: /Property/Details/{p.PropertyID}"));
            }

            // =========================================================================
            // BƯỚC 3: LẤY DỮ LIỆU ĐÀO TẠO RAG VÀ THÔNG TIN TRANG HIỆN TẠI
            // =========================================================================
            var aiKnowledgeBase = await _context.StaticPages
                .Where(s => s.PageKey == "ai_knowledge_base")
                .Select(s => s.Content)
                .FirstOrDefaultAsync();

            string ragData = string.IsNullOrWhiteSpace(aiKnowledgeBase) ? "Chưa có quy định nội bộ." : aiKnowledgeBase;

            string currentPageInfo = isViewingProperty
                ? $"\n[THÔNG TIN TRANG HIỆN TẠI KHÁCH ĐANG XEM]:\n{req.PageContext}\n"
                : "";

            // =========================================================================
            // BƯỚC 4: THIẾT KẾ PROMPT KHOA HỌC (Chống lỗi Robot & Định dạng xấu)
            // =========================================================================
            var prompt = $"""
                Bạn là một Chuyên viên tư vấn Bất động sản cấp cao tại "Sàn BĐS Khánh Hòa". Bạn cực kỳ chuyên nghiệp, tinh tế và am hiểu thị trường.
                
                [KIẾN THỨC PHÁP LÝ & QUY ĐỊNH SÀN (RAG DATA)]:
                {ragData}

                {currentPageInfo}
                {realEstateContext}

                QUY TẮC TRẢ LỜI CỦA BẠN (BẮT BUỘC TUÂN THỦ):
                1. ĐỊNH DẠNG ĐẸP, DỄ ĐỌC: 
                   - Tuyệt đối KHÔNG viết một đoạn văn dài ngoằng, dính liền nhau.
                   - Phải xuống dòng giữa các ý.
                   - Sử dụng dấu gạch đầu dòng (-) hoặc dấu (*) khi liệt kê các ưu điểm, thông tin.
                   - In đậm (**chữ**) những điểm nhấn quan trọng như Giá, Vị trí.
                2. VĂN PHONG TỰ NHIÊN:
                   - Xưng hô là "Mình" hoặc "Em", gọi người hỏi là "Bạn" hoặc "Anh/Chị".
                   - KHÔNG BAO GIỜ đọc mã ID thô kệch (VD: "Bất động sản ID 48"). Hãy diễn đạt tự nhiên như người thật: "Căn góc 2 mặt tiền Lê Thánh Tôn mà bạn đang xem...".
                3. XỬ LÝ ĐÚNG TÂM LÝ KHÁCH HÀNG:
                   - Nếu khách hỏi "nên mua/thuê căn này không?" (về trang hiện tại): Hãy tập trung phân tích Ưu điểm, Vị trí, Mức giá của căn đó để thuyết phục khách. Khuyên họ "Bấm vào nút Đặt lịch xem nhà để đi xem thực tế".
                   - Nếu khách yêu cầu tìm kiếm: Dựa vào [DANH SÁCH BẤT ĐỘNG SẢN PHÙ HỢP] để giới thiệu ngắn gọn các căn đang có.
                   - Nếu không có BĐS phù hợp: Lịch sự báo hết hàng và mời khách để lại thông tin liên hệ.
                4. TRẢ LỜI LINH HOẠT: Có thể tâm sự, làm toán, giải đáp xã hội nếu khách hỏi ngoài lề. Không được từ chối cứng nhắc.

                CÂU HỎI CỦA KHÁCH HÀNG: {req.Message}
                """;

            // =========================================================================
            // BƯỚC 5: GỌI API GEMINI VỚI TRY-CATCH CHUẨN MỰC
            // =========================================================================
            var apiKey = _config["GeminiApiSettings:ApiKey"];
            var baseUrl = _config["GeminiApiSettings:BaseUrl"];
            var url = $"{baseUrl}/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var requestBody = new
            {
                contents = new[] {
                    new {
                        parts = new[] { new { text = prompt } }
                    }
                }
            };

            string botMessage = "";

            try
            {
                var response = await _httpClient.PostAsJsonAsync(url, requestBody);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    botMessage = doc.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString() ?? "";
                }
                else
                {
                    // Catch riêng lỗi API
                    string err = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Gemini API Error: {err}");
                    botMessage = "Hiện tại hệ thống AI đang quá tải lượt truy cập. Bạn vui lòng đợi vài phút rồi thử lại nhé! ⏳";
                }
            }
            catch (TaskCanceledException)
            {
                // Bắt chính xác lỗi Timeout (Mạng yếu hoặc API treo)
                botMessage = "Lỗi kết nối mạng hoặc máy chủ AI phản hồi quá chậm. Bạn vui lòng kiểm tra lại đường truyền và thử lại nhé! 🌐";
            }
            catch (Exception ex)
            {
                // Bắt các lỗi hệ thống khác
                Console.WriteLine($"System Error: {ex.Message}");
                botMessage = "Đã xảy ra sự cố kết nối với trợ lý AI. Vui lòng liên hệ Hotline nếu bạn cần hỗ trợ gấp!";
            }

            // =========================================================================
            // BƯỚC 6: LƯU LỊCH SỬ CHAT VÀ TRẢ VỀ DỮ LIỆU
            // =========================================================================
            var log = new ChatLogs
            {
                UserID = req.UserId > 0 ? req.UserId : null,
                UserMessage = req.Message,
                BotResponse = botMessage,
                CreatedAt = DateTime.Now
            };
            _context.ChatLogs.Add(log);
            await _context.SaveChangesAsync();

            return new ChatResponse
            {
                Message = botMessage,
                // Chỉ nạp thẻ Gợi ý UI (Card) nếu có tìm thấy BĐS mới và khách có ý định tìm kiếm
                SuggestedProperties = relevantProperties.Select(p => (object)new
                {
                    title = p.Title,
                    price = p.Price.HasValue
                            ? (p.Price >= 1_000_000_000 ? (p.Price / 1_000_000_000M)?.ToString("0.##") + " Tỷ" : (p.Price / 1_000_000M)?.ToString("0.##") + " Triệu")
                            : "Thỏa thuận",
                    link = $"/Property/Details/{p.PropertyID}"
                }).ToList()
            };
        }
    }
}