using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

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
            _httpClient = new HttpClient();
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            // 1. Lấy dữ liệu BĐS thực tế
            var properties = await _context.Properties
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Where(p => p.Status == "Approved")
                .OrderByDescending(p => p.PackageID)
                .Take(5)
                .ToListAsync();

            var contextData = string.Join("\n", properties.Select(p =>
                $"- {p.Title}: {p.Price:N0} VNĐ tại {p.Ward?.WardName}. Link: /Property/Details/{p.PropertyID}"));

            // 2. Tạo Prompt "Siêu Trí Tuệ"
            var prompt = $"""
                Bạn là một Trợ lý AI đa năng, thông minh và là chuyên gia tư vấn của website Bất Động Sản Khánh Hòa.
                
                QUY TẮC TRẢ LỜI CỦA BẠN:
                1. ĐỐI VỚI BẤT ĐỘNG SẢN KHÁNH HÒA: Sử dụng dữ liệu thực tế này để tư vấn:
                {contextData}
                
                2. ĐỐI VỚI MỌI CÂU HỎI KHÁC: Bạn được quyền sử dụng toàn bộ kiến thức toàn cầu của mình để trả lời (ví dụ: chào hỏi, làm toán, viết văn, kiến thức xã hội, v.v.). Hãy trả lời nhiệt tình, đừng bao giờ nói "tôi chỉ tư vấn bất động sản".
                
                3. PHONG CÁCH: Thân thiện, lịch sự, trả lời bằng tiếng Việt rõ ràng.

                CÂU HỎI CỦA NGƯỜI DÙNG: {req.Message}
                """;

            // 3. Gọi API Gemini (NÂNG CẤP LÊN MODEL GEMINI 2.5 FLASH MỚI NHẤT)
            var apiKey = _config["GeminiApiSettings:ApiKey"];
            var baseUrl = _config["GeminiApiSettings:BaseUrl"];

            // Đã đổi từ 1.5 sang 2.5
            var url = $"{baseUrl}/models/gemini-2.5-flash:generateContent?key={apiKey}";

            var requestBody = new
            {
                contents = new[] {
                    new {
                        parts = new[] { new { text = prompt } }
                    }
                }
            };

            string botMessage = "Xin lỗi Tài, trợ lý AI đang bận xử lý một chút. Vui lòng thử lại sau giây lát!";

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
                        .GetString() ?? botMessage;
                }
                else
                {
                    // In lỗi chi tiết ra cửa sổ Output/Console của Visual Studio để dễ bắt bệnh
                    var errorDetail = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("--- LỖI TỪ GOOGLE API ---");
                    Console.WriteLine(errorDetail);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("--- LỖI HỆ THỐNG C# ---");
                Console.WriteLine(ex.Message);
            }

            // 4. Lưu Log vào Database
            var log = new ChatLogs
            {
                UserID = req.UserId,
                UserMessage = req.Message,
                BotResponse = botMessage,
                CreatedAt = DateTime.Now
            };
            _context.ChatLogs.Add(log);
            await _context.SaveChangesAsync();

            return new ChatResponse
            {
                Message = botMessage,
                SuggestedProperties = properties.Select(p => (object)new { p.Title, Price = $"{p.Price:N0}" }).ToList()
            };
        }
    }
}