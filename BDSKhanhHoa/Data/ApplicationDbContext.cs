using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Models;
using System.Data;
using Microsoft.Data.SqlClient;

namespace BDSKhanhHoa.Data
{
    public class ApplicationDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        // --- CÁC BẢNG CỐT LÕI ---
        public DbSet<User> Users { get; set; }
        public DbSet<Property> Properties { get; set; }
        public DbSet<Area> Areas { get; set; }
        public DbSet<Ward> Wards { get; set; }
        public DbSet<PropertyType> PropertyTypes { get; set; }
        public DbSet<Blog> Blogs { get; set; }
        public DbSet<ContactMessage> ContactMessages { get; set; }
        public DbSet<RoleUpgradeRequest> RoleUpgradeRequests { get; set; }

        // --- CÁC BẢNG QUẢN LÝ VÀ HỆ THỐNG ---
        public DbSet<AuditLog> AuditLogs { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<UserViolation> UserViolations { get; set; }
        public DbSet<Banner> Banners { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. Cấu hình Khóa chính (Primary Keys)
            modelBuilder.Entity<User>().HasKey(u => u.UserID);
            modelBuilder.Entity<Property>().HasKey(p => p.PropertyID);
            modelBuilder.Entity<Area>().HasKey(a => a.AreaID);
            modelBuilder.Entity<Ward>().HasKey(w => w.WardID);
            modelBuilder.Entity<PropertyType>().HasKey(pt => pt.TypeID);
            modelBuilder.Entity<ContactMessage>().HasKey(c => c.ContactID);
            modelBuilder.Entity<Blog>().HasKey(b => b.BlogID);
            modelBuilder.Entity<RoleUpgradeRequest>().HasKey(r => r.RequestID);
            modelBuilder.Entity<AuditLog>().HasKey(al => al.LogID);
            modelBuilder.Entity<Transaction>().HasKey(t => t.TransactionID);
            modelBuilder.Entity<UserViolation>().HasKey(uv => uv.ViolationID);

            // 2. Cấu hình kiểu dữ liệu Decimal
            modelBuilder.Entity<User>()
                .Property(u => u.WalletBalance)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<Property>()
                .Property(p => p.Price)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<Property>()
                .Property(p => p.AreaSize)
                .HasColumnType("decimal(18,2)");

            modelBuilder.Entity<Transaction>()
                .Property(t => t.Amount)
                .HasColumnType("decimal(18,2)");

            // 3. Cấu hình các mối quan hệ (Foreign Keys)
            modelBuilder.Entity<AuditLog>()
                .HasOne(al => al.User)
                .WithMany()
                .HasForeignKey(al => al.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Transaction>()
                .HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserID)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserViolation>()
                .HasOne(uv => uv.User)
                .WithMany()
                .HasForeignKey(uv => uv.UserID)
                .OnDelete(DeleteBehavior.Cascade);
            // Cấu hình quan hệ Cha - Con cho PropertyType
            modelBuilder.Entity<PropertyType>()
                .HasOne<PropertyType>() // Mỗi loại có thể có 1 cha
                .WithMany()            // Một cha có thể có nhiều con
                .HasForeignKey(pt => pt.ParentID)
                .OnDelete(DeleteBehavior.Restrict); // Không xóa cha nếu còn con
            modelBuilder.Entity<Banner>().HasKey(b => b.BannerID);


        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        }
    }
}