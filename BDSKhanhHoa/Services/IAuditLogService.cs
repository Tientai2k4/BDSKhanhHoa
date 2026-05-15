namespace BDSKhanhHoa.Services
{
    public interface IAuditLogService
    {
        // Hàm chuẩn để ghi log ở bất kỳ đâu trong hệ thống
        Task LogAsync(int userId, string action, string moduleName, string target, string oldValues = null, string newValues = null, string severity = "Info");
    }
}